using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace VisionIQ.Api.Features.Fixes;

/// <summary>The pull request that now exists for a fix: its id and the commit at the head of its branch.</summary>
public sealed record PublishedPullRequest(string PullRequestId, string HeadSha);

/// <summary>
/// Opens the draft pull request of a verified fix and posts its checks. There is deliberately no method to
/// approve, vote on, complete or merge a pull request: the engineer decides (HG-01).
/// </summary>
public interface IFixPullRequestPublisher
{
    FixPullRequestMode Mode { get; }

    /// <param name="project">The ADO project the repository belongs to.</param>
    /// <param name="fix">The stored fix result; it has a pull-request draft and the candidate's files.</param>
    /// <param name="mayAlreadyExist">True on a retry: an earlier attempt may have pushed or opened the PR.</param>
    Task<PublishedPullRequest> PublishAsync(
        string project, FixDocument fix, bool mayAlreadyExist, CancellationToken cancellationToken);

    /// <summary>Posts one status per check on the pull request (S13-06).</summary>
    Task PostChecksAsync(
        string project, string repositoryId, string pullRequestId, IReadOnlyList<FixCheck> checks,
        CancellationToken cancellationToken);

    /// <summary>
    /// The head commit the platform reports for the pull request right now; null when this publisher has no
    /// platform to ask (record mode). Read-only.
    /// </summary>
    Task<string?> CurrentHeadAsync(
        string project, string repositoryId, string pullRequestId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}

/// <summary>The pull request could not be opened. <see cref="Permanent"/> failures are not retried.</summary>
public sealed class FixPublishException : Exception
{
    public FixPublishException()
        : this("publish_failed", false)
    {
    }

    public FixPublishException(string message)
        : this(message, false)
    {
    }

    public FixPublishException(string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = "publish_failed";
    }

    public FixPublishException(string reason, bool permanent)
        : base(reason)
    {
        Reason = reason;
        Permanent = permanent;
    }

    /// <summary>A fixed reason code, safe to log and store.</summary>
    public string Reason { get; }

    public bool Permanent { get; }
}

/// <summary>
/// <c>FIX_PR_MODE=record</c>: nothing leaves the service. The worker stores what would have been pushed and opened
/// in <c>workflow.fix_pull_requests</c>, which is how the flow is tested without writing to a real repository.
/// </summary>
public sealed class RecordingPullRequestPublisher : IFixPullRequestPublisher
{
    public FixPullRequestMode Mode => FixPullRequestMode.Record;

    public Task<PublishedPullRequest> PublishAsync(
        string project, FixDocument fix, bool mayAlreadyExist, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fix);
        // Nothing was pushed, so the head is the sandbox candidate itself.
        return Task.FromResult(new PublishedPullRequest("recorded", fix.PullRequest!.HeadSha));
    }

    public Task PostChecksAsync(
        string project, string repositoryId, string pullRequestId, IReadOnlyList<FixCheck> checks,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// S04-05 / S11-14 / S13-06: pushes the candidate as one commit on a new branch, opens a DRAFT pull request linked
/// to the work item and posts VisionIQ's checks, all through the Azure DevOps REST API with the bot's own token.
/// It can create a branch, a pull request and statuses, and nothing else: it never votes, never sets
/// auto-complete, never completes a pull request and never changes or deletes an existing branch.
/// On a retry it first looks for the branch and the pull request, so a failure after a successful call never
/// produces a second commit or a second pull request. Before the pull request is opened, the pushed commit's tree
/// is compared with the tree that was tested: a different tree is never offered for review.
/// </summary>
public sealed class AdoPullRequestPublisher : IFixPullRequestPublisher
{
    public const string StatusGenre = "visioniq";
    private const string ApiVersion = "7.1";
    private const string CheckPrefix = "visioniq/";
    private const int MaxTitleLength = 400;
    private const int MaxDescriptionLength = 4000;


    private readonly HttpClient http;
    private readonly string root;
    private readonly AuthenticationHeaderValue authorization;
    private readonly ILogger logger;

    public AdoPullRequestPublisher(
        HttpClient http, Uri organizationUrl, string pullRequestToken, ILogger<AdoPullRequestPublisher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(organizationUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(pullRequestToken);
        this.http = http;
        this.logger = logger ?? NullLogger<AdoPullRequestPublisher>.Instance;
        root = organizationUrl.AbsoluteUri.TrimEnd('/');
        authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pullRequestToken)));
    }

    public FixPullRequestMode Mode => FixPullRequestMode.Ado;

    public async Task<PublishedPullRequest> PublishAsync(
        string project, FixDocument fix, bool mayAlreadyExist, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(fix);
        var draft = fix.PullRequest ?? throw new FixPublishException("no_draft_pull_request", permanent: true);
        var files = fix.CommitFiles() ?? throw new FixPublishException("candidate_files_missing", permanent: true);
        // Everything that can be refused is refused before the first call: nothing is pushed for a result
        // that could not be opened as a pull request anyway.
        if (fix.Patch?.TreeSha is not { Length: > 0 } tested || !FixPatterns.Sha().IsMatch(tested))
        {
            // Without the tested tree there is nothing to compare the pushed commit with.
            throw new FixPublishException("tested_tree_missing", permanent: true);
        }

        if (!FixPatterns.FixBranch().IsMatch(draft.SourceBranch) || !FixPatterns.TargetBranch().IsMatch(draft.TargetBranch) || !FixPatterns.Sha().IsMatch(draft.BaseSha))
        {
            throw new FixPublishException("branch_not_allowed", permanent: true);
        }

        if (!int.TryParse(draft.WorkItemId, NumberStyles.None, CultureInfo.InvariantCulture, out var workItemId) || workItemId <= 0)
        {
            throw new FixPublishException("ticket_id_not_a_work_item", permanent: true);
        }

        if (draft.Title.Length is 0 or > MaxTitleLength || draft.Description.Length > MaxDescriptionLength)
        {
            throw new FixPublishException("pull_request_text_too_long", permanent: true);
        }

        var repository = Repository(project, fix.RepositoryId);
        var sourceRef = "refs/heads/" + draft.SourceBranch;
        var targetRef = "refs/heads/" + draft.TargetBranch;

        if (mayAlreadyExist && await FindPullRequestAsync(repository, draft.SourceBranch, sourceRef, targetRef, cancellationToken) is { } existing)
        {
            FixLog.PullRequestFound(logger, existing.PullRequestId, fix.RepositoryId);
            // Its branch may have moved since the earlier attempt. Results are only shown against a head that
            // carries the tested content; otherwise every check is posted as pending and the delivery ends.
            if (!await HasTreeAsync(repository, existing.HeadSha, tested, cancellationToken))
            {
                FixLog.TreeMismatch(logger, existing.HeadSha, tested);
                await PostChecksAsync(
                    project, fix.RepositoryId, existing.PullRequestId, FixGates.Checks(fix.Gates ?? [], existing.HeadSha), cancellationToken);
                throw new FixPublishException("pull_request_head_not_tested", permanent: true);
            }

            FixLog.TreeVerified(logger, existing.HeadSha, tested);
            return existing;
        }

        var head = mayAlreadyExist ? await BranchHeadAsync(repository, draft.SourceBranch, sourceRef, cancellationToken) : null;
        if (head is not null)
        {
            FixLog.BranchReused(logger, draft.SourceBranch, head);
        }
        else
        {
            head = await PushAsync(repository, sourceRef, draft, files, cancellationToken);
            FixLog.BranchPushed(logger, draft.SourceBranch, fix.RepositoryId, draft.BaseSha, head, files.Count);
        }

        // The commit made by ADO has its own SHA; what must be identical is its content.
        if (!await HasTreeAsync(repository, head, tested, cancellationToken))
        {
            FixLog.TreeMismatch(logger, head, tested);
            throw new FixPublishException("pushed_tree_differs_from_tested_tree", permanent: true);
        }

        FixLog.TreeVerified(logger, head, tested);
        using var created = await SendAsync(
            HttpMethod.Post,
            new Uri($"{repository}/pullrequests?api-version={ApiVersion}"),
            new
            {
                sourceRefName = sourceRef,
                targetRefName = targetRef,
                title = draft.Title,
                description = DescriptionFor(draft.Description, head, tested),
                isDraft = true,
                workItemRefs = new[] { new { id = draft.WorkItemId } },
            },
            cancellationToken);
        var pullRequestId = Number(created.RootElement, "pullRequestId")
            ?? throw new FixPublishException("pull_request_id_missing", permanent: false);
        FixLog.PullRequestCreated(logger, pullRequestId, fix.RepositoryId, draft.WorkItemId);

        // The pull request points at the branch tip when it is created, which can be newer than the commit verified
        // above. Its real head is checked again before any result is shown against it.
        var openedHead = await CurrentHeadAsync(project, fix.RepositoryId, pullRequestId, cancellationToken);
        if (openedHead is null || !await HasTreeAsync(repository, openedHead, tested, cancellationToken))
        {
            FixLog.TreeMismatch(logger, openedHead ?? head, tested);
            await PostChecksAsync(
                project, fix.RepositoryId, pullRequestId, FixGates.Checks(fix.Gates ?? [], openedHead ?? head), cancellationToken);
            throw new FixPublishException("pull_request_head_not_tested", permanent: true);
        }

        return new PublishedPullRequest(pullRequestId, openedHead);
    }

    public async Task PostChecksAsync(
        string project, string repositoryId, string pullRequestId, IReadOnlyList<FixCheck> checks,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(checks);
        var statuses = new Uri(
            $"{Repository(project, repositoryId)}/pullRequests/{Uri.EscapeDataString(pullRequestId)}/statuses?api-version={ApiVersion}");
        foreach (var check in checks)
        {
            var name = check.Name.StartsWith(CheckPrefix, StringComparison.Ordinal) ? check.Name[CheckPrefix.Length..] : check.Name;
            using var posted = await SendAsync(
                HttpMethod.Post,
                statuses,
                new
                {
                    // ADO's own vocabulary; "not_applicable" is spelled notApplicable there.
                    state = check.State == "not_applicable" ? "notApplicable" : check.State,
                    description = check.Description,
                    context = new { name, genre = StatusGenre },
                },
                cancellationToken);
            FixLog.StatusPosted(logger, pullRequestId, check.Name, check.State);
        }
    }

    private string Repository(string project, string repositoryId) =>
        $"{root}/{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}";

    /// <summary>
    /// The head commit Azure DevOps itself reports for a pull request; null when it has none. A webhook body is
    /// only a hint that something changed: this is what the checks are judged against.
    /// </summary>
    public async Task<string?> CurrentHeadAsync(
        string project, string repositoryId, string pullRequestId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        using var pullRequest = await SendAsync(
            HttpMethod.Get,
            new Uri($"{Repository(project, repositoryId)}/pullrequests/{Uri.EscapeDataString(pullRequestId)}?api-version={ApiVersion}"),
            null,
            cancellationToken);
        return pullRequest.RootElement.TryGetProperty("lastMergeSourceCommit", out var source) &&
            Text(source, "commitId") is { } head && FixPatterns.Sha().IsMatch(head.ToLowerInvariant())
                ? head.ToLowerInvariant()
                : null;
    }

    /// <summary>The description with the commit that was really pushed: the sandbox commit it cites is not in the repository.</summary>
    internal static string DescriptionFor(string description, string pushedSha, string treeSha)
    {
        var note = $"\n\nPushed as commit `{pushedSha}` (tree `{treeSha}`, identical to the tested tree).";
        var room = MaxDescriptionLength - note.Length;
        return (description.Length <= room ? description : description[..room]) + note;
    }

    private async Task<bool> HasTreeAsync(string repository, string commitSha, string tested, CancellationToken cancellationToken)
    {
        using var commit = await SendAsync(
            HttpMethod.Get, new Uri($"{repository}/commits/{Uri.EscapeDataString(commitSha)}?api-version={ApiVersion}"), null, cancellationToken);
        return string.Equals(Text(commit.RootElement, "treeId"), tested, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The ACTIVE pull request from this branch into the expected target, if an earlier attempt opened one. An
    /// abandoned or completed pull request on the same branch is somebody's decision and is never taken over.
    /// </summary>
    private async Task<PublishedPullRequest?> FindPullRequestAsync(
        string repository, string branch, string sourceRef, string targetRef, CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"{repository}/pullrequests?searchCriteria.sourceRefName={Uri.EscapeDataString(sourceRef)}" +
            $"&searchCriteria.status=active&$top=10&api-version={ApiVersion}");
        string? id = null;
        string? head = null;
        using (var found = await SendAsync(HttpMethod.Get, uri, null, cancellationToken))
        {
            if (!found.RootElement.TryGetProperty("value", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var pullRequest in list.EnumerateArray())
            {
                // Older API versions omit the target in a search result; a present one must be the expected one.
                if (Number(pullRequest, "pullRequestId") is { } number &&
                    (Text(pullRequest, "targetRefName") is not { } target || string.Equals(target, targetRef, StringComparison.Ordinal)))
                {
                    id = number;
                    head = pullRequest.TryGetProperty("lastMergeSourceCommit", out var source) ? Text(source, "commitId") : null;
                    break;
                }
            }
        }

        if (id is null)
        {
            return null;
        }

        // The pull request exists, so a second one must not be opened even when the search result did not
        // carry its head: the branch has it.
        head ??= await BranchHeadAsync(repository, branch, sourceRef, cancellationToken)
            ?? throw new FixPublishException("pull_request_head_unknown", permanent: false);
        return new PublishedPullRequest(id, head);
    }

    private async Task<string?> BranchHeadAsync(
        string repository, string branch, string sourceRef, CancellationToken cancellationToken)
    {
        var uri = new Uri($"{repository}/refs?filter={Uri.EscapeDataString("heads/" + branch)}&api-version={ApiVersion}");
        using var refs = await SendAsync(HttpMethod.Get, uri, null, cancellationToken);
        if (!refs.RootElement.TryGetProperty("value", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var reference in list.EnumerateArray())
        {
            // The filter is a prefix match; only the exact branch counts.
            if (string.Equals(Text(reference, "name"), sourceRef, StringComparison.Ordinal))
            {
                return Text(reference, "objectId");
            }
        }

        return null;
    }

    private async Task<string> PushAsync(
        string repository, string sourceRef, FixPullRequestDraft draft, IReadOnlyList<FixFileChange> files,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            // A new branch that starts at the pinned baseline: the commit below is its only change.
            refUpdates = new[] { new { name = sourceRef, oldObjectId = draft.BaseSha } },
            commits = new[]
            {
                new
                {
                    comment = draft.Title + "\n\nProposed by VisionIQ. Not reviewed.",
                    changes = files.Select(file => new
                    {
                        changeType = file.Change == "added" ? "add" : "edit",
                        item = new { path = "/" + file.Path },
                        newContent = new { content = file.Content, contentType = "rawtext" },
                    }),
                },
            },
        };
        using var pushed = await SendAsync(
            HttpMethod.Post, new Uri($"{repository}/pushes?api-version={ApiVersion}"), body, cancellationToken);
        if (pushed.RootElement.TryGetProperty("commits", out var commits) && commits.ValueKind == JsonValueKind.Array)
        {
            foreach (var commit in commits.EnumerateArray())
            {
                if (Text(commit, "commitId") is { } commitId)
                {
                    return commitId;
                }
            }
        }

        throw new FixPublishException("pushed_commit_id_missing", permanent: false);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static string? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetRawText()
            : null;

    private async Task<JsonDocument> SendAsync(HttpMethod method, Uri uri, object? body, CancellationToken cancellationToken)
    {
        try
        {
            return await SendCoreAsync(method, uri, body, cancellationToken);
        }
        catch (FixPublishException error)
        {
            // The operation is the last path segment (pushes, pullrequests, statuses, refs, a commit id):
            // never the query string, a header or the body.
            FixLog.AdoCallFailed(logger, method.Method, uri.Segments[^1].TrimEnd('/'), error.Reason);
            throw;
        }
    }

    private async Task<JsonDocument> SendCoreAsync(HttpMethod method, Uri uri, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = authorization;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException ||
            (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new FixPublishException("ado_unreachable", permanent: false);
        }

        using (response)
        {
            // 203 is how ADO answers an expired or wrong token (a sign-in page instead of JSON).
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NonAuthoritativeInformation)
            {
                throw new FixPublishException("ado_pull_request_not_permitted", permanent: true);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new FixPublishException("ado_repository_not_found", permanent: true);
            }

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity)
            {
                // The request itself is refused; sending it again gives the same answer.
                throw new FixPublishException("ado_request_rejected", permanent: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new FixPublishException("ado_http_" + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), permanent: false);
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            }
            catch (JsonException)
            {
                throw new FixPublishException("ado_response_not_json", permanent: false);
            }
            catch (Exception error) when (error is IOException or HttpRequestException)
            {
                // The connection dropped while the answer was being read.
                throw new FixPublishException("ado_unreachable", permanent: false);
            }
        }
    }
}
