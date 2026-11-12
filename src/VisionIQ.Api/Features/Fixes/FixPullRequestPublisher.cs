using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VisionIQ.Api.Features.Pickup;

namespace VisionIQ.Api.Features.Fixes;

/// <summary>
/// S11-14: opens the draft pull request of each fix result exactly once. A row is claimed with a lease, the
/// stored result is read, and only a result that carries a pull-request draft is delivered: a blocked, failed or
/// review-stopped fix is skipped with a reason. A failed call is retried with backoff; a permanent failure (no
/// permission, repository gone, a pushed tree that differs from the tested one) ends as <c>failed</c>.
/// Nothing here approves or completes a pull request.
/// </summary>
public sealed class FixPullRequestPublisher(
    IFixPullRequestStore store, IFixPullRequestPublisher pullRequests, PickupPolicy? policy,
    ILogger<FixPullRequestPublisher> logger)
{
    public const int MaxAttempts = 8;
    // One delivery makes about ten calls to Azure DevOps, each with its own timeout: the lease must outlast
    // all of them, or a second instance would start the same delivery.
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(15);

    /// <summary>Delivers due results; returns how many were claimed.</summary>
    public async Task<int> ProcessPendingAsync(int batchSize = 10, CancellationToken cancellationToken = default)
    {
        // One row per claim: its lease then always outlasts its own calls to Azure DevOps.
        var delivered = 0;
        while (delivered < batchSize)
        {
            var claimed = await store.ClaimAsync(1, Lease, cancellationToken);
            if (claimed.Count == 0)
            {
                break;
            }

            await PublishAsync(claimed[0], cancellationToken);
            delivered++;
        }

        return delivered;
    }

    /// <summary>Seconds to wait after a failed attempt: 15 s doubling to 15 minutes.</summary>
    public static int RetryDelaySeconds(int attempt) => (int)Math.Min(900, 15 * Math.Pow(2, Math.Max(0, attempt - 1)));

    /// <summary>The ADO project name of a run: runs carry the pickup policy's project id.</summary>
    internal static string ProjectName(PickupPolicy? policy, string projectId) =>
        policy?.Projects.FirstOrDefault(p => string.Equals(p.ProjectId, projectId, StringComparison.Ordinal))?.Project ?? projectId;

    private async Task PublishAsync(PendingFixPullRequest pending, CancellationToken cancellationToken)
    {
        FixLog.Delivering(logger, pending.RunId, pending.ProjectId, pending.TicketId, pending.Attempt);
        var fix = pending.FixJson is null ? null : FixDocument.Parse(pending.FixJson);
        if (fix is null)
        {
            await FailAsync(pending.RunId, "fix_result_unreadable", cancellationToken);
            return;
        }

        if (fix.PullRequest is not { } draft)
        {
            // Not verified, blocked or stopped for review: there is nothing to offer for review.
            await store.SkipAsync(pending.RunId, "no_draft_pull_request", cancellationToken);
            FixLog.Skipped(logger, pending.RunId, fix.Verdict);
            return;
        }

        if (!draft.IsDraft)
        {
            await FailAsync(pending.RunId, "pull_request_not_draft", cancellationToken);
            return;
        }

        if (fix.CommitFiles() is not { } files)
        {
            await FailAsync(pending.RunId, "candidate_files_missing", cancellationToken);
            return;
        }

        // The stored result says where to push. Before the only write this service makes to a repository, that
        // is checked against what the API itself recorded for the run: same ticket, repository and baseline.
        // (The store already joins the result to the run by both ids.)
        if (!string.Equals(draft.WorkItemId, pending.TicketId, StringComparison.Ordinal) ||
            (pending.RepositoryId is not null && !string.Equals(fix.RepositoryId, pending.RepositoryId, StringComparison.Ordinal)) ||
            (pending.BaseSha is not null && !string.Equals(draft.BaseSha, pending.BaseSha, StringComparison.OrdinalIgnoreCase)))
        {
            await FailAsync(pending.RunId, "fix_result_does_not_match_run", cancellationToken);
            return;
        }

        // The pilot list is applied again here: narrowing it also stops deliveries that were already queued.
        if (policy?.AllowedWorkItemIds is not null &&
            !(int.TryParse(pending.TicketId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var workItem) &&
              policy.IsWorkItemAllowed(workItem)))
        {
            await store.SkipAsync(pending.RunId, "work_item_not_allowlisted", cancellationToken);
            FixLog.Skipped(logger, pending.RunId, "not on the pilot list");
            return;
        }

        var project = ProjectName(policy, pending.ProjectId);
        try
        {
            var opened = await pullRequests.PublishAsync(project, fix, pending.Attempt > 1, cancellationToken);
            // The sandbox results are about the candidate's content; they are shown against the commit that
            // now carries that content on the pull request.
            var checks = FixGates.Checks(FixGates.ForPushedCommit(fix.Gates ?? [], opened.HeadSha), opened.HeadSha);
            await pullRequests.PostChecksAsync(project, fix.RepositoryId, opened.PullRequestId, checks, cancellationToken);
            var request = JsonSerializer.Serialize(
                new
                {
                    project,
                    repository_id = fix.RepositoryId,
                    draft.SourceBranch,
                    draft.TargetBranch,
                    draft.BaseSha,
                    draft.WorkItemId,
                    draft.Title,
                    draft.Description,
                    is_draft = true,
                    files = files.Select(file => new
                    {
                        file.Path,
                        file.Change,
                        sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(file.Content))),
                    }),
                    checks,
                },
                PostgresFixPullRequestStore.Json);
            await store.MarkPublishedAsync(
                pending.RunId,
                new FixPullRequestRecord(
                    pullRequests.Mode,
                    fix.RepositoryId,
                    draft.SourceBranch,
                    opened,
                    checks,
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request))),
                    pullRequests.Mode == FixPullRequestMode.Record ? request : null),
                cancellationToken);
            FixLog.Published(logger, pending.RunId, pending.ProjectId, pending.TicketId, pullRequests.Mode, opened.PullRequestId, checks.Count);
        }
        catch (FixPublishException error) when (error.Permanent || pending.Attempt >= MaxAttempts)
        {
            await FailAsync(pending.RunId, error.Permanent ? error.Reason : "publish_attempts_exhausted", cancellationToken);
        }
        catch (FixPublishException error)
        {
            await DeferAsync(pending, error.Reason, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Anything unexpected is retried and capped like any other failure, and never stops the rest of
            // the batch. Type only: provider messages can include connection details.
            if (pending.Attempt >= MaxAttempts)
            {
                await FailAsync(pending.RunId, "publish_attempts_exhausted", cancellationToken);
                return;
            }

            await DeferAsync(pending, error.GetType().Name, cancellationToken);
        }
    }

    private async Task FailAsync(Guid runId, string reason, CancellationToken cancellationToken)
    {
        await store.FailAsync(runId, reason, cancellationToken);
        FixLog.PublishFailed(logger, runId, reason);
    }

    private async Task DeferAsync(PendingFixPullRequest pending, string reason, CancellationToken cancellationToken)
    {
        var delay = RetryDelaySeconds(pending.Attempt);
        await store.DeferAsync(pending.RunId, TimeSpan.FromSeconds(delay), cancellationToken);
        FixLog.PublishDeferred(logger, pending.RunId, reason, pending.Attempt, delay);
    }
}

/// <summary>
/// S11-17: a new commit on a pull request VisionIQ opened invalidates the earlier results. The checks are posted
/// again for the new head, where every sandbox result is stale and therefore pending, never green. If the head
/// returns to the verified commit, the results apply again.
/// </summary>
public sealed class FixPullRequestHeadWatcher(
    IFixPullRequestStore store, IFixPullRequestPublisher pullRequests, PickupPolicy? policy,
    ILogger<FixPullRequestHeadWatcher> logger)
{
    public const string UnknownPullRequest = "not_a_visioniq_pull_request";
    public const string Unchanged = "head_unchanged";
    public const string Invalidated = "checks_invalidated";
    public const string Restored = "checks_restored";

    /// <summary>Applies a "pull request updated" delivery; returns a fixed outcome code.</summary>
    public async Task<string> OnUpdatedAsync(PullRequestUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var opened = await store.FindOpenedAsync(
            update.PullRequestId, [update.RepositoryId, update.RepositoryName], cancellationToken);
        if (opened is null)
        {
            FixLog.HeadUpdateIgnored(logger, update.PullRequestId, UnknownPullRequest);
            return UnknownPullRequest;
        }

        // The delivery only says that something changed. Deliveries can arrive late, twice or out of order, so
        // the head is the one Azure DevOps reports now, not the one in the body.
        var project = FixPullRequestPublisher.ProjectName(policy, opened.ProjectId);
        var head = await pullRequests.CurrentHeadAsync(project, opened.RepositoryId, opened.PullRequestId, cancellationToken)
            ?? update.HeadSha;
        if (string.Equals(opened.CurrentHeadSha, head, StringComparison.OrdinalIgnoreCase))
        {
            FixLog.HeadUpdateIgnored(logger, update.PullRequestId, Unchanged);
            return Unchanged;
        }

        // A result that cannot be read gives no gates, and then every check is posted as pending.
        var fix = opened.FixJson is null ? null : FixDocument.Parse(opened.FixJson);
        var gates = FixGates.ForPushedCommit(fix?.Gates ?? [], opened.VerifiedHeadSha);
        var checks = FixGates.Checks(gates, head);
        await pullRequests.PostChecksAsync(project, opened.RepositoryId, opened.PullRequestId, checks, cancellationToken);
        await store.MarkHeadAsync(opened.RunId, head, checks, cancellationToken);
        var restored = string.Equals(opened.VerifiedHeadSha, head, StringComparison.OrdinalIgnoreCase);
        FixLog.HeadChanged(logger, opened.RunId, opened.PullRequestId, restored ? Restored : Invalidated);
        return restored ? Restored : Invalidated;
    }
}

/// <summary>Runs <see cref="FixPullRequestPublisher"/> continuously.</summary>
public sealed class FixPullRequestWorker(
    FixPullRequestPublisher publisher, FixPullRequestOptions options, ILogger<FixPullRequestWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        FixLog.Enabled(logger, options.Mode);
        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;
            try
            {
                claimed = await publisher.ProcessPendingAsync(cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                // Type only: provider messages can include connection details.
                FixLog.WorkerFailed(logger, error.GetType().Name);
            }

            if (claimed == 0 && !await PickupWorker.DelayAsync(options.WorkerInterval, stoppingToken))
            {
                break;
            }
        }
    }
}

/// <summary>Source-generated fix delivery log messages. Ids and fixed codes only; no code or ticket text.</summary>
internal static partial class FixLog
{
    [LoggerMessage(EventId = 640, Level = LogLevel.Information, Message = "Fix pull requests on (mode {Mode})")]
    public static partial void Enabled(ILogger logger, FixPullRequestMode mode);

    [LoggerMessage(EventId = 641, Level = LogLevel.Information, Message = "Draft pull request {PullRequestId} for run {RunId} ({ProjectId} #{TicketId}) opened via {Mode} with {CheckCount} checks")]
    public static partial void Published(
        ILogger logger, Guid runId, string projectId, string ticketId, FixPullRequestMode mode, string pullRequestId, int checkCount);

    [LoggerMessage(EventId = 642, Level = LogLevel.Warning, Message = "Pull request for run {RunId} not opened ({Reason}), attempt {Attempt}; retrying in {DelaySeconds} s")]
    public static partial void PublishDeferred(ILogger logger, Guid runId, string reason, int attempt, int delaySeconds);

    [LoggerMessage(EventId = 643, Level = LogLevel.Error, Message = "Pull request for run {RunId} could not be opened: {Reason}")]
    public static partial void PublishFailed(ILogger logger, Guid runId, string reason);

    [LoggerMessage(EventId = 644, Level = LogLevel.Error, Message = "Fix pull request worker failed ({ErrorType})")]
    public static partial void WorkerFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 645, Level = LogLevel.Information, Message = "No pull request for run {RunId}: the fix result is {Verdict}")]
    public static partial void Skipped(ILogger logger, Guid runId, string verdict);

    [LoggerMessage(EventId = 646, Level = LogLevel.Information, Message = "Pull request {PullRequestId} of run {RunId} has a new head: {Outcome}")]
    public static partial void HeadChanged(ILogger logger, Guid runId, string pullRequestId, string outcome);

    [LoggerMessage(EventId = 647, Level = LogLevel.Information, Message = "Fix run {FixRunId} requested after the brief of run {BriefRunId}")]
    public static partial void FixRunRequested(ILogger logger, Guid fixRunId, Guid briefRunId);

    [LoggerMessage(EventId = 648, Level = LogLevel.Error, Message = "Pull request update could not be applied ({ErrorType}); ADO will retry")]
    public static partial void HeadUpdateFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 649, Level = LogLevel.Debug, Message = "Delivering the fix result of run {RunId} ({ProjectId} #{TicketId}), attempt {Attempt}")]
    public static partial void Delivering(ILogger logger, Guid runId, string projectId, string ticketId, int attempt);

    [LoggerMessage(EventId = 650, Level = LogLevel.Information, Message = "Fix result {ResultId} of run {RunId} ({ResultState}) queued for a draft pull request")]
    public static partial void Queued(ILogger logger, Guid resultId, Guid runId, string resultState);

    [LoggerMessage(EventId = 651, Level = LogLevel.Information, Message = "Branch {Branch} pushed to {RepositoryId} from {BaseSha}: commit {HeadSha}, {FileCount} file(s)")]
    public static partial void BranchPushed(ILogger logger, string branch, string repositoryId, string baseSha, string headSha, int fileCount);

    [LoggerMessage(EventId = 652, Level = LogLevel.Information, Message = "Retry found pull request {PullRequestId} already open in {RepositoryId}; no second one is opened")]
    public static partial void PullRequestFound(ILogger logger, string pullRequestId, string repositoryId);

    [LoggerMessage(EventId = 653, Level = LogLevel.Information, Message = "Retry found branch {Branch} already pushed at {HeadSha}; no second commit is made")]
    public static partial void BranchReused(ILogger logger, string branch, string headSha);

    [LoggerMessage(EventId = 654, Level = LogLevel.Debug, Message = "Pushed commit {HeadSha} has the tested tree {TreeSha}")]
    public static partial void TreeVerified(ILogger logger, string headSha, string treeSha);

    [LoggerMessage(EventId = 655, Level = LogLevel.Error, Message = "Pushed commit {HeadSha} does not have the tested tree {TreeSha}; no pull request is opened")]
    public static partial void TreeMismatch(ILogger logger, string headSha, string treeSha);

    [LoggerMessage(EventId = 656, Level = LogLevel.Information, Message = "Draft pull request {PullRequestId} created in {RepositoryId} for work item {WorkItemId}")]
    public static partial void PullRequestCreated(ILogger logger, string pullRequestId, string repositoryId, string workItemId);

    [LoggerMessage(EventId = 657, Level = LogLevel.Debug, Message = "Pull request {PullRequestId} status {CheckName}: {State}")]
    public static partial void StatusPosted(ILogger logger, string pullRequestId, string checkName, string state);

    [LoggerMessage(EventId = 658, Level = LogLevel.Warning, Message = "Azure DevOps {Method} {Operation} failed: {Reason}")]
    public static partial void AdoCallFailed(ILogger logger, string method, string operation, string reason);

    [LoggerMessage(EventId = 659, Level = LogLevel.Debug, Message = "Pull request {PullRequestId} update ignored: {Outcome}")]
    public static partial void HeadUpdateIgnored(ILogger logger, string pullRequestId, string outcome);

    [LoggerMessage(EventId = 661, Level = LogLevel.Error, Message = "Pull request update could not be applied ({Reason}); it is not retried")]
    public static partial void HeadUpdateRejected(ILogger logger, string reason);

    [LoggerMessage(EventId = 660, Level = LogLevel.Debug, Message = "No fix run after the brief of run {BriefRunId}: not a picked-up defect, or one already exists")]
    public static partial void NoFixRun(ILogger logger, Guid briefRunId);
}
