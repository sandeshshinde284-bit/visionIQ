using System.Globalization;
using System.Text.Json;

namespace VisionIQ.Ingestor.Ado;

public sealed record RepositoryRecord(string Id, string Name, string Project, string? DefaultBranch);

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

    /// <summary>LRN-12: reviewers and their votes, by ADO identity ID only (no names or e-mails).</summary>
    public IReadOnlyList<PullRequestReviewer> Reviewers { get; init; } = [];

    /// <summary>When the pull request was closed, or created if it is still open.</summary>
    public DateTimeOffset LastActivity => ClosedDate ?? CreatedDate;

    /// <summary>LRN-12 (S12-01): the lifecycle state derived from status, draft flag and votes.</summary>
    public string Lifecycle => PullRequestLifecycle.Of(this);
}

/// <summary>An ADO reviewer vote: 10 approved, 5 approved with suggestions, 0 none, -5 waiting for author, -10 rejected.</summary>
public sealed record PullRequestReviewer(string Id, int Vote, bool IsRequired, bool HasDeclined);

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

        var votes = pullRequest.Reviewers.Where(reviewer => !reviewer.HasDeclined).ToList();
        if (votes.Any(reviewer => reviewer.Vote <= -10))
        {
            return Rejected;
        }

        if (votes.Any(reviewer => reviewer.Vote == -5))
        {
            return WaitingForAuthor;
        }

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

    /// <summary>Adds the work items and commits that Azure DevOps itself links to the pull request.</summary>
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
        while (token is not null && token != previous);

        return pullRequest with
        {
            WorkItemIds = workItemIds,
            CommitIds = commitIds.Distinct(StringComparer.Ordinal).ToList(),
        };
    }

    public static RepositoryRecord? ParseRepository(JsonElement element, string project)
    {
        var id = JsonRead.String(element, "id");
        var name = JsonRead.String(element, "name");
        return id is null || name is null ? null : new RepositoryRecord(id, name, project, JsonRead.String(element, "defaultBranch"));
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

        // An active pull request's lastMergeCommit is a preview merge, not a commit on the target branch.
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
