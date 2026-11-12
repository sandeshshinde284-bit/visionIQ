using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VisionIQ.Api.Features.Pickup;

namespace VisionIQ.Api.Features.Briefs;

/// <summary>
/// S10-08: delivers each run's brief to its ticket exactly once. A publication is claimed with a lease, rendered,
/// posted and then marked published together with its pickup-to-comment time (BRF-07). A failed post is retried
/// with backoff; a permanent failure (no permission, ticket gone) or too many attempts ends as <c>failed</c> with
/// a reason, and the run's result stays stored either way.
/// </summary>
public sealed class BriefPublisher(
    IBriefPublicationStore store, IBriefCommentPublisher comments, PickupPolicy? policy, ILogger<BriefPublisher> logger)
{
    public const int MaxAttempts = 8;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    /// <summary>Delivers due publications; returns how many were claimed.</summary>
    public async Task<int> ProcessPendingAsync(int batchSize = 10, CancellationToken cancellationToken = default)
    {
        // One publication per claim: its lease then always outlasts its own post, so a second API
        // instance never picks it up while this one is still working on an earlier item.
        var published = 0;
        while (published < batchSize)
        {
            var claimed = await store.ClaimAsync(1, Lease, cancellationToken);
            if (claimed.Count == 0)
            {
                break;
            }

            await PublishAsync(claimed[0], cancellationToken);
            published++;
        }

        return published;
    }

    /// <summary>Seconds to wait after a failed attempt: 15 s doubling to 15 minutes.</summary>
    public static int RetryDelaySeconds(int attempt) => (int)Math.Min(900, 15 * Math.Pow(2, Math.Max(0, attempt - 1)));

    private async Task PublishAsync(PendingPublication publication, CancellationToken cancellationToken)
    {
        var target = publication.Target;
        if (!int.TryParse(target.TicketId, NumberStyles.None, CultureInfo.InvariantCulture, out var workItemId) || workItemId <= 0)
        {
            await store.FailAsync(target.RunId, "ticket_id_not_a_work_item", cancellationToken);
            BriefLog.PublishFailed(logger, target.RunId, "ticket_id_not_a_work_item");
            return;
        }

        var brief = publication.BriefJson is null ? null : BriefDocument.Parse(publication.BriefJson);
        if (publication.BriefJson is not null && brief is null)
        {
            // An unreadable brief is never half-rendered: the ticket gets nothing rather than something wrong.
            await store.FailAsync(target.RunId, "brief_unreadable", cancellationToken);
            BriefLog.PublishFailed(logger, target.RunId, "brief_unreadable");
            return;
        }

        // Runs carry the policy's project id; ADO is addressed by the project's name.
        var project = policy?.Projects.FirstOrDefault(p => string.Equals(p.ProjectId, target.ProjectId, StringComparison.Ordinal))?.Project
            ?? target.ProjectId;
        try
        {
            var html = BriefCommentRenderer.Render(target, brief);
            var commentId = await comments.PublishAsync(
                project, workItemId, html, BriefCommentRenderer.Marker(target.RunId), publication.Attempt > 1, cancellationToken);
            var elapsed = await store.MarkPublishedAsync(
                target.RunId,
                comments.Mode,
                commentId,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(html))),
                comments.Mode == BriefPublishMode.Record ? html : null,
                cancellationToken);
            if (elapsed is { } timeToBrief)
            {
                // S10-11: one log line per run with the pickup-to-comment time.
                BriefLog.Published(
                    logger, target.RunId, target.ProjectId, target.TicketId, target.ResultState,
                    comments.Mode, (long)timeToBrief.TotalMilliseconds, brief?.Advice?.Count ?? 0);
            }
        }
        catch (BriefPublishException error) when (error.Permanent || publication.Attempt >= MaxAttempts)
        {
            await store.FailAsync(target.RunId, error.Permanent ? error.Reason : "publish_attempts_exhausted", cancellationToken);
            BriefLog.PublishFailed(logger, target.RunId, error.Reason);
        }
        catch (BriefPublishException error)
        {
            var delay = RetryDelaySeconds(publication.Attempt);
            await store.DeferAsync(target.RunId, TimeSpan.FromSeconds(delay), cancellationToken);
            BriefLog.PublishDeferred(logger, target.RunId, error.Reason, publication.Attempt, delay);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Anything unexpected (a dropped connection mid-response, a brief field this version cannot
            // render) is retried and capped like any other failure, and never stops the rest of the batch.
            // Type only: provider messages can include connection details.
            if (publication.Attempt >= MaxAttempts)
            {
                await store.FailAsync(target.RunId, "publish_attempts_exhausted", cancellationToken);
                BriefLog.PublishFailed(logger, target.RunId, error.GetType().Name);
                return;
            }

            var delay = RetryDelaySeconds(publication.Attempt);
            await store.DeferAsync(target.RunId, TimeSpan.FromSeconds(delay), cancellationToken);
            BriefLog.PublishDeferred(logger, target.RunId, error.GetType().Name, publication.Attempt, delay);
        }
    }
}

/// <summary>Runs <see cref="BriefPublisher"/> continuously, so a brief is on the ticket seconds after it is stored.</summary>
public sealed class BriefPublishWorker(BriefPublisher publisher, BriefPublishOptions options, ILogger<BriefPublishWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BriefLog.Enabled(logger, options.Mode);
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
                BriefLog.WorkerFailed(logger, error.GetType().Name);
            }

            if (claimed == 0 && !await PickupWorker.DelayAsync(options.WorkerInterval, stoppingToken))
            {
                break;
            }
        }
    }
}

/// <summary>
/// Source-generated brief delivery log messages. Run, project, ticket id and fixed codes only; the brief text is
/// never logged.
/// </summary>
internal static partial class BriefLog
{
    [LoggerMessage(EventId = 620, Level = LogLevel.Information, Message = "Brief publishing on (mode {Mode})")]
    public static partial void Enabled(ILogger logger, BriefPublishMode mode);

    [LoggerMessage(EventId = 621, Level = LogLevel.Information, Message = "Brief for run {RunId} ({ProjectId} #{TicketId}) published as {ResultState} via {Mode}: time to brief {TimeToBriefMs} ms, {AdviceCount} advice items")]
    public static partial void Published(
        ILogger logger, Guid runId, string projectId, string ticketId, string resultState, BriefPublishMode mode,
        long timeToBriefMs, int adviceCount);

    [LoggerMessage(EventId = 622, Level = LogLevel.Warning, Message = "Brief for run {RunId} not published ({Reason}), attempt {Attempt}; retrying in {DelaySeconds} s")]
    public static partial void PublishDeferred(ILogger logger, Guid runId, string reason, int attempt, int delaySeconds);

    [LoggerMessage(EventId = 623, Level = LogLevel.Error, Message = "Brief for run {RunId} could not be published: {Reason}")]
    public static partial void PublishFailed(ILogger logger, Guid runId, string reason);

    [LoggerMessage(EventId = 624, Level = LogLevel.Error, Message = "Brief publish worker failed ({ErrorType})")]
    public static partial void WorkerFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 625, Level = LogLevel.Information, Message = "Advice feedback on {ProjectId} #{WorkItemId}: {Outcome} ({Recorded} recorded)")]
    public static partial void Feedback(ILogger logger, string projectId, int workItemId, string outcome, int recorded);

    [LoggerMessage(EventId = 626, Level = LogLevel.Error, Message = "Advice feedback on {ProjectId} #{WorkItemId} could not be stored ({ErrorType}); ADO will retry")]
    public static partial void FeedbackFailed(ILogger logger, string projectId, int workItemId, string errorType);
}
