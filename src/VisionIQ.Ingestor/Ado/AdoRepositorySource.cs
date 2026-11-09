using System.Globalization;
using System.Text.Json;

namespace VisionIQ.Ingestor.Ado;

public sealed record RepositoryRecord(string Id, string Name, string Project, string? DefaultBranch)
{
    /// <summary>The ADO project GUID (needed for branch-policy evaluations).</summary>
    public string? ProjectId { get; init; }
}

public sealed record PullRequestRecord(
    int Id,
    string RepositoryId,
    string RepositoryName,
    string Project,
    string Status,
    string Title,
    DateTimeOffset CreatedDate)
{
    public DateTimeOffset? ClosedDate { get; init; }

    public string? Description { get; init; }

    public string? SourceBranch { get; init; }

    public string? TargetBranch { get; init; }

    /// <summary>Azure DevOps identity ID of the author. Names and emails are not stored.</summary>
    public string? CreatedById { get; init; }

    /// <summary>The merge commit on the target branch. Only set for completed pull requests.</summary>
    public string? MergeCommitId { get; init; }

    public IReadOnlyList<int> WorkItemIds { get; init; } = [];

    public IReadOnlyList<string> CommitIds { get; init; } = [];

    /// <summary>LRN-12: a draft PR is not yet ready for review.</summary>
    public bool IsDraft { get; init; }

    /// <summary>§8.12: head commit of the source branch for the latest iteration (the code under review).</summary>
    public string? SourceCommitId { get; init; }

    /// <summary>§8.12: target-branch commit the latest iteration was compared against.</summary>
    public string? TargetCommitId { get; init; }

    /// <summary>§8.12: every push to the PR (PR_ITERATION) with its source and target commits.</summary>
    public IReadOnlyList<PullRequestIteration>? Iterations { get; init; }

    /// <summary>§8.12: files the PR changes overall (latest iteration vs. its base). Null when unavailable.</summary>
    public IReadOnlyList<PullRequestFileChange>? ChangedFiles { get; init; }

    /// <summary>§8.12: review threads (rationale), excluding system messages. Null when unavailable.</summary>
    public IReadOnlyList<PullRequestThread>? ReviewThreads { get; init; }

    /// <summary>§8.12: branch-policy evaluations (required reviewers, builds...). Null when unavailable.</summary>
    public IReadOnlyList<PullRequestPolicyEvaluation>? PolicyEvaluations { get; init; }

    /// <summary>Parts of the enrichment that could not be read ("unavailable" is never shown as "none").</summary>
    public IReadOnlyList<string> EnrichmentGaps { get; init; } = [];

    /// <summary>
    /// §8.12 change-set fingerprint: SHA-256 of the sorted "changeType path" lines of <see cref="ChangedFiles"/>.
    /// Identifies the same set of file changes across PRs; it is file-level, not a content diff.
    /// </summary>
    public string? PatchFingerprint => ChangedFiles is null
        ? null
        : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Join('\n', ChangedFiles.Select(change => change.ChangeType + " " + change.Path).Order(StringComparer.Ordinal)))));

    /// <summary>LRN-12: reviewers and their votes, by ADO identity ID only (no names or e-mails).</summary>
    public IReadOnlyList<PullRequestReviewer> Reviewers { get; init; } = [];

    /// <summary>When the pull request was closed, or created if it is still open.</summary>
    public DateTimeOffset LastActivity => ClosedDate ?? CreatedDate;

    /// <summary>LRN-12 (S12-01): the lifecycle state derived from status, draft flag and votes.</summary>
    public string Lifecycle => PullRequestLifecycle.Of(this);
}

/// <summary>§8.12 PR_ITERATION: one push to the PR.</summary>
public sealed record PullRequestIteration(int Id, string? SourceCommitId, string? TargetCommitId, DateTimeOffset? CreatedDate);

/// <summary>§8.12: one file changed by the PR (path and ADO change type, e.g. "edit", "add", "delete, rename").</summary>
public sealed record PullRequestFileChange(string Path, string ChangeType);

/// <summary>§8.12: a review thread; authors by ADO identity ID only.</summary>
public sealed record PullRequestThread(int Id, string? Status, string? FilePath, IReadOnlyList<PullRequestComment> Comments);

public sealed record PullRequestComment(int Id, string? AuthorId, DateTimeOffset? PublishedDate, string Content);

/// <summary>§8.12: one branch-policy evaluation (e.g. "Minimum number of reviewers": approved, blocking).</summary>
public sealed record PullRequestPolicyEvaluation(string Policy, string Status, bool IsBlocking);

/// <summary>An ADO reviewer vote: 10 approved, 5 approved with suggestions, 0 none, -5 waiting for author, -10 rejected.</summary>
public sealed record PullRequestReviewer(string Id, int Vote, bool IsRequired, bool HasDeclined);

/// <summary>
/// S05-05: one reviewer's current vote, versioned as its own evidence item independent of the PR's
/// own revision cadence (LRN-03) -- a vote can change without the PR itself gaining a new native
/// revision, so without this a reviewer's history is only ever visible as of the last PR snapshot.
/// Never implies a root cause is verified (see PullRequestLifecycle); this is just the raw vote fact.
/// </summary>
public sealed record PullRequestReviewRecord(
    string ReviewerId, int PullRequestId, string RepositoryId, string RepositoryName, string Project,
    int Vote, bool IsRequired, bool HasDeclined, DateTimeOffset Observed)
{
    public string Status => ReviewVoteStatus.Of(Vote, HasDeclined);
}

/// <summary>Per-reviewer counterpart to <see cref="PullRequestLifecycle"/>'s PR-level aggregate.</summary>
public static class ReviewVoteStatus
{
    public const string Approved = "approved";
    public const string ApprovedWithSuggestions = "approved_with_suggestions";
    public const string NoVote = "no_vote";
    public const string WaitingForAuthor = "waiting_for_author";
    public const string Rejected = "rejected";
    public const string Declined = "declined";

    public static string Of(int vote, bool hasDeclined)
    {
        if (hasDeclined)
        {
            return Declined;
        }

        return vote switch
        {
            <= -10 => Rejected,
            -5 => WaitingForAuthor,
            >= 10 => Approved,
            5 => ApprovedWithSuggestions,
            _ => NoVote,
        };
    }
}

/// <summary>
/// LRN-12 (S12-01) PR lifecycle states. A closed PR is merged or abandoned; an open PR is draft, rejected,
/// waiting for author, approved (every required reviewer approved and nobody rejected or waiting) or active.
/// A lifecycle state is a fact about the PR, never evidence that a root cause is verified.
/// </summary>
public static class PullRequestLifecycle
{
    public const string Merged = "merged";
    public const string Abandoned = "abandoned";
    public const string Draft = "draft";
    public const string Rejected = "rejected";
    public const string WaitingForAuthor = "waiting_for_author";
    public const string Approved = "approved";
    public const string Active = "active";

    public static string Of(PullRequestRecord pullRequest)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        switch (pullRequest.Status)
        {
            case "completed":
                return Merged;
            case "abandoned":
                return Abandoned;
        }

        if (pullRequest.IsDraft)
        {
            return Draft;
        }

        // A reviewer who declined is not part of the outcome at all (not a rejection, not a pending vote).
        var votes = pullRequest.Reviewers.Where(reviewer => !reviewer.HasDeclined).ToList();
        if (votes.Any(reviewer => reviewer.Vote <= -10))
        {
            return Rejected;
        }

        if (votes.Any(reviewer => reviewer.Vote == -5))
        {
            return WaitingForAuthor;
        }

        // "Approved" requires every *required* reviewer to be at >=5, but at least one vote overall; a PR with
        // zero reviewers isn't approved, it's just active.
        var required = votes.Where(reviewer => reviewer.IsRequired).ToList();
        var approvals = votes.Where(reviewer => reviewer.Vote >= 5).ToList();
        return approvals.Count > 0 && required.All(reviewer => reviewer.Vote >= 5) ? Approved : Active;
    }
}

/// <summary>Reads repositories and pull requests with their linked work items and commits (S04-04).</summary>
public sealed class AdoRepositorySource
{
    private const int PageSize = 100;
    private readonly AdoClient client;

    public AdoRepositorySource(AdoClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
    }

    /// <summary>Enabled repositories in the project. Disabled repositories are skipped because their APIs fail.</summary>
    public async Task<IReadOnlyList<RepositoryRecord>> GetRepositoriesAsync(string project, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(client.BuildUri(project, "_apis/git/repositories"), cancellationToken).ConfigureAwait(false);
        return JsonRead.Items(response.Root, "value")
            .Where(repository => !JsonRead.Bool(repository, "isDisabled"))
            .Select(repository => ParseRepository(repository, project))
            .OfType<RepositoryRecord>()
            .ToList();
    }

    /// <summary>Every pull request in the repository (active, completed and abandoned).</summary>
    public async Task<IReadOnlyList<PullRequestRecord>> GetPullRequestsAsync(string project, RepositoryRecord repository, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var results = new List<PullRequestRecord>();
        for (var skip = 0; ; skip += PageSize)
        {
            var uri = client.BuildUri(
                project,
                "_apis/git/repositories/" + repository.Id + "/pullrequests",
                [
                    new("searchCriteria.status", "all"),
                    new("$top", PageSize.ToString(CultureInfo.InvariantCulture)),
                    new("$skip", skip.ToString(CultureInfo.InvariantCulture)),
                ]);
            using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            var page = JsonRead.Items(response.Root, "value").ToList();
            results.AddRange(page.Select(item => ParsePullRequest(item, project, repository)).OfType<PullRequestRecord>());
            if (page.Count < PageSize)
            {
                return results;
            }
        }
    }

    /// <summary>
    /// LRN-12: re-fetches one PR (current state, reviewers and votes) for a webhook event and enriches it like the
    /// sync does. The repository and its project come from ADO's answer, never from the event.
    /// </summary>
    public async Task<(PullRequestRecord PullRequest, RepositoryRecord Repository)?> GetPullRequestAsync(
        string project, string repositoryId, int id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        RepositoryRecord? repository;
        PullRequestRecord? parsed;
        using (var response = await client.GetAsync(client.BuildUri(project,
            "_apis/git/repositories/" + Uri.EscapeDataString(repositoryId) + "/pullrequests/" + id.ToString(CultureInfo.InvariantCulture)),
            cancellationToken).ConfigureAwait(false))
        {
            var root = response.Root;
            repository = JsonRead.Child(root, "repository") is { } repo &&
                JsonRead.Child(repo, "project") is { } repoProject && JsonRead.String(repoProject, "name") is { } projectName
                ? ParseRepository(repo, projectName)
                : null;
            parsed = repository is null ? null : ParsePullRequest(root, repository.Project, repository);
        }

        if (repository is null || parsed is null)
        {
            return null;
        }

        return (await EnrichAsync(repository.Project, repository, parsed, cancellationToken).ConfigureAwait(false), repository);
    }

    public async Task<PullRequestRecord> EnrichAsync(string project, RepositoryRecord repository, PullRequestRecord pullRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(pullRequest);
        var basePath = "_apis/git/repositories/" + repository.Id + "/pullRequests/" + pullRequest.Id.ToString(CultureInfo.InvariantCulture);

        List<int> workItemIds;
        using (var workItems = await client.GetAsync(client.BuildUri(project, basePath + "/workitems"), cancellationToken).ConfigureAwait(false))
        {
            workItemIds = JsonRead.Items(workItems.Root, "value").Select(item => JsonRead.Int(item, "id")).OfType<int>().Distinct().ToList();
        }

        var commitIds = new List<string>();
        string? token = null;
        string? previous;
        do
        {
            var query = new List<KeyValuePair<string, string>> { new("$top", PageSize.ToString(CultureInfo.InvariantCulture)) };
            if (token is not null)
            {
                query.Add(new("continuationToken", token));
            }

            using var page = await client.GetAsync(client.BuildUri(project, basePath + "/commits", query), cancellationToken).ConfigureAwait(false);
            commitIds.AddRange(JsonRead.Items(page.Root, "value").Select(item => JsonRead.String(item, "commitId")).OfType<string>());
            previous = token;
            token = page.ContinuationToken;
        }
        while (token is not null && token != previous); // guards against a server returning the same token forever

        var gaps = new List<string>();
        var iterations = await TryAsync("iterations", gaps, () => GetIterationsAsync(project, basePath, cancellationToken)).ConfigureAwait(false);
        var latest = iterations?.Count > 0 ? iterations.Max(iteration => iteration.Id) : (int?)null;
        // Distinguish "no iterations exist" (changes = empty list, a real fact) from "iterations couldn't be read"
        // (changes = null, an unknown) so EnrichmentGaps stays the only source of truth for "unavailable".
        var changes = latest is { } last
            ? await TryAsync("changed files", gaps, () => GetChangedFilesAsync(project, basePath, last, cancellationToken)).ConfigureAwait(false)
            : iterations is null ? null : [];
        var threads = await TryAsync("review threads", gaps, () => GetThreadsAsync(project, basePath, cancellationToken)).ConfigureAwait(false);
        var policies = repository.ProjectId is { Length: > 0 } projectId
            ? await TryAsync("branch policies", gaps, () => GetPolicyEvaluationsAsync(project, projectId, pullRequest.Id, cancellationToken)).ConfigureAwait(false)
            : null;
        if (repository.ProjectId is not { Length: > 0 })
        {
            gaps.Add("branch policies: project ID unknown");
        }

        return pullRequest with
        {
            WorkItemIds = workItemIds,
            CommitIds = commitIds.Distinct(StringComparer.Ordinal).ToList(),
            Iterations = iterations,
            ChangedFiles = changes,
            ReviewThreads = threads,
            PolicyEvaluations = policies,
            EnrichmentGaps = gaps,
        };
    }

    /// <summary>Reads one optional part; on a source error records a gap and returns null (unavailable, not empty).</summary>
    private static async Task<T?> TryAsync<T>(string part, List<string> gaps, Func<Task<T>> read) where T : class
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (AdoSourceException error)
        {
            gaps.Add(part + ": " + (error.StatusCode is { } status ? "HTTP " + ((int)status).ToString(CultureInfo.InvariantCulture) : "unavailable"));
            return null;
        }
    }

    private async Task<IReadOnlyList<PullRequestIteration>> GetIterationsAsync(string project, string basePath, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(client.BuildUri(project, basePath + "/iterations"), cancellationToken).ConfigureAwait(false);
        return JsonRead.Items(response.Root, "value")
            .Select(item => JsonRead.Int(item, "id") is { } id
                ? new PullRequestIteration(id,
                    JsonRead.Child(item, "sourceRefCommit") is { } source ? JsonRead.String(source, "commitId") : null,
                    JsonRead.Child(item, "targetRefCommit") is { } target ? JsonRead.String(target, "commitId") : null,
                    JsonRead.Date(item, "createdDate"))
                : null)
            .OfType<PullRequestIteration>()
            .OrderBy(iteration => iteration.Id)
            .ToList();
    }

    /// <summary>Cumulative changes of the latest iteration against the PR base ($compareTo=0), paged.</summary>
    private async Task<IReadOnlyList<PullRequestFileChange>> GetChangedFilesAsync(string project, string basePath, int iteration, CancellationToken cancellationToken)
    {
        var changes = new List<PullRequestFileChange>();
        var skip = 0;
        for (var page = 0; page < 100; page++)
        {
            var uri = client.BuildUri(project, basePath + "/iterations/" + iteration.ToString(CultureInfo.InvariantCulture) + "/changes",
            [
                new("$compareTo", "0"),
                new("$top", "2000"),
                new("$skip", skip.ToString(CultureInfo.InvariantCulture)),
            ]);
            using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            foreach (var entry in JsonRead.Items(response.Root, "changeEntries"))
            {
                var path = JsonRead.Child(entry, "item") is { } item ? JsonRead.String(item, "path") : null;
                if (path is not null && JsonRead.String(entry, "changeType") is { } type)
                {
                    changes.Add(new PullRequestFileChange(path, type));
                }
            }

            var next = JsonRead.Int(response.Root, "nextSkip") ?? 0;
            if (next <= skip)
            {
                break; // no further page (or a non-advancing page token)
            }

            skip = next;
        }

        return changes
            .DistinctBy(change => change.Path, StringComparer.Ordinal)
            .OrderBy(change => change.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Review threads with human comments; system messages and deleted comments/threads are skipped.</summary>
    private async Task<IReadOnlyList<PullRequestThread>> GetThreadsAsync(string project, string basePath, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(client.BuildUri(project, basePath + "/threads"), cancellationToken).ConfigureAwait(false);
        var threads = new List<PullRequestThread>();
        foreach (var thread in JsonRead.Items(response.Root, "value"))
        {
            if (JsonRead.Int(thread, "id") is not { } id || JsonRead.Bool(thread, "isDeleted"))
            {
                continue;
            }

            var comments = JsonRead.Items(thread, "comments")
                .Where(comment => !JsonRead.Bool(comment, "isDeleted") &&
                    !string.Equals(JsonRead.String(comment, "commentType"), "system", StringComparison.OrdinalIgnoreCase))
                .Select(comment => JsonRead.Int(comment, "id") is { } commentId && JsonRead.String(comment, "content") is { } content
                    ? new PullRequestComment(commentId, JsonRead.Child(comment, "author") is { } author ? JsonRead.String(author, "id") : null,
                        JsonRead.Date(comment, "publishedDate"), content)
                    : null)
                .OfType<PullRequestComment>()
                .OrderBy(comment => comment.Id)
                .ToList();
            if (comments.Count > 0)
            {
                var file = JsonRead.Child(thread, "threadContext") is { } context ? JsonRead.String(context, "filePath") : null;
                threads.Add(new PullRequestThread(id, JsonRead.String(thread, "status"), file, comments));
            }
        }

        return threads.OrderBy(thread => thread.Id).ToList();
    }

    private async Task<IReadOnlyList<PullRequestPolicyEvaluation>> GetPolicyEvaluationsAsync(string project, string projectId, int pullRequestId, CancellationToken cancellationToken)
    {
        var artifact = "vstfs:///CodeReview/CodeReviewId/" + projectId + "/" + pullRequestId.ToString(CultureInfo.InvariantCulture);
        using var response = await client.GetAsync(
            client.BuildUri(project, "_apis/policy/evaluations", [new("artifactId", artifact)], apiVersion: "7.1-preview.1"),
            cancellationToken).ConfigureAwait(false);
        return JsonRead.Items(response.Root, "value")
            .Select(evaluation =>
            {
                var configuration = JsonRead.Child(evaluation, "configuration");
                var policy = configuration is { } config && JsonRead.Child(config, "type") is { } type ? JsonRead.String(type, "displayName") : null;
                return policy is not null && JsonRead.String(evaluation, "status") is { } status
                    ? new PullRequestPolicyEvaluation(policy, status, configuration is { } c && JsonRead.Bool(c, "isBlocking"))
                    : null;
            })
            .OfType<PullRequestPolicyEvaluation>()
            .OrderBy(evaluation => evaluation.Policy, StringComparer.Ordinal)
            .ThenBy(evaluation => evaluation.Status, StringComparer.Ordinal)
            .ToList();
    }

    public static RepositoryRecord? ParseRepository(JsonElement element, string project)
    {
        var id = JsonRead.String(element, "id");
        var name = JsonRead.String(element, "name");
        return id is null || name is null
            ? null
            : new RepositoryRecord(id, name, project, JsonRead.String(element, "defaultBranch"))
            {
                ProjectId = JsonRead.Child(element, "project") is { } owner ? JsonRead.String(owner, "id") : null,
            };
    }

    public static PullRequestRecord? ParsePullRequest(JsonElement element, string project, RepositoryRecord repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (JsonRead.Int(element, "pullRequestId") is not { } id
            || JsonRead.String(element, "status") is not { } status
            || JsonRead.String(element, "title") is not { } title
            || JsonRead.Date(element, "creationDate") is not { } created)
        {
            return null;
        }

        // An active pull request's lastMergeCommit is a preview merge, not a commit on the target branch, so it
        // would be misleading as "the merge commit" — only read it once the PR is actually completed.
        var mergeCommit = status == "completed" && JsonRead.Child(element, "lastMergeCommit") is { } merge
            ? JsonRead.String(merge, "commitId")
            : null;

        return new PullRequestRecord(id, repository.Id, repository.Name, project, status, title, created)
        {
            ClosedDate = JsonRead.Date(element, "closedDate"),
            Description = JsonRead.String(element, "description"),
            SourceBranch = JsonRead.String(element, "sourceRefName"),
            TargetBranch = JsonRead.String(element, "targetRefName"),
            CreatedById = JsonRead.Child(element, "createdBy") is { } author ? JsonRead.String(author, "id") : null,
            MergeCommitId = mergeCommit,
            IsDraft = JsonRead.Bool(element, "isDraft"),
            SourceCommitId = JsonRead.Child(element, "lastMergeSourceCommit") is { } head ? JsonRead.String(head, "commitId") : null,
            TargetCommitId = JsonRead.Child(element, "lastMergeTargetCommit") is { } baseCommit ? JsonRead.String(baseCommit, "commitId") : null,
            Reviewers = JsonRead.Items(element, "reviewers")
                .Select(reviewer => JsonRead.String(reviewer, "id") is { } reviewerId
                    ? new PullRequestReviewer(reviewerId, JsonRead.Int(reviewer, "vote") ?? 0,
                        JsonRead.Bool(reviewer, "isRequired"), JsonRead.Bool(reviewer, "hasDeclined"))
                    : null)
                .OfType<PullRequestReviewer>()
                .OrderBy(reviewer => reviewer.Id, StringComparer.Ordinal) // stable order: same votes, same content
                .ToList(),
        };
    }
}
