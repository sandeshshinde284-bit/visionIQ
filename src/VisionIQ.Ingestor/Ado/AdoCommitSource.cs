using System.Globalization;
using System.Text.Json;

namespace VisionIQ.Ingestor.Ado;

public sealed record ChangedFile(string Path, string ChangeType);

public sealed record CommitRecord(string CommitId, string RepositoryId, string Project, DateTimeOffset CommitterDate, string Comment)
{
    public string RepositoryName { get; init; } = string.Empty;

    public DateTimeOffset? AuthorDate { get; init; }

    public bool CommentTruncated { get; init; }

    public IReadOnlyList<ChangedFile> ChangedFiles { get; init; } = [];
}

/// <summary>Reads commit metadata and changed files through the Azure DevOps REST API, without cloning (S04-10).</summary>
public sealed class AdoCommitSource
{
    private const int PageSize = 100;
    private readonly AdoClient client;

    public AdoCommitSource(AdoClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
    }

    /// <summary>Commits on the default branch, from the given date (inclusive, to the second).</summary>
    public async Task<IReadOnlyList<CommitRecord>> GetCommitsAsync(string project, RepositoryRecord repository, DateTimeOffset? fromDate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var results = new List<CommitRecord>();
        for (var skip = 0; ; skip += PageSize)
        {
            var query = new List<KeyValuePair<string, string>>
            {
                new("searchCriteria.$top", PageSize.ToString(CultureInfo.InvariantCulture)),
                new("searchCriteria.$skip", skip.ToString(CultureInfo.InvariantCulture)),
            };
            if (fromDate is { } from)
            {
                query.Add(new("searchCriteria.fromDate", from.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
            }

            using var response = await client.GetAsync(client.BuildUri(project, "_apis/git/repositories/" + repository.Id + "/commits", query), cancellationToken).ConfigureAwait(false);
            var page = JsonRead.Items(response.Root, "value").ToList();
            results.AddRange(page.Select(item => ParseCommit(item, project, repository)).OfType<CommitRecord>());
            if (page.Count < PageSize)
            {
                return results;
            }
        }
    }

    /// <summary>Adds the full message (when the list truncated it) and the changed files.</summary>
    public async Task<CommitRecord> EnrichAsync(string project, RepositoryRecord repository, CommitRecord commit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(commit);
        var basePath = "_apis/git/repositories/" + repository.Id + "/commits/" + commit.CommitId;

        var comment = commit.Comment;
        if (commit.CommentTruncated)
        {
            using var detail = await client.GetAsync(client.BuildUri(project, basePath), cancellationToken).ConfigureAwait(false);
            comment = JsonRead.String(detail.Root, "comment") ?? comment;
        }

        List<ChangedFile> files;
        using (var changes = await client.GetAsync(client.BuildUri(project, basePath + "/changes", [new("top", "1000")]), cancellationToken).ConfigureAwait(false))
        {
            files = JsonRead.Items(changes.Root, "changes")
                .Select(change => (Item: JsonRead.Child(change, "item"), Type: JsonRead.String(change, "changeType")))
                .Where(change => change.Item is { } item
                    && !JsonRead.Bool(item, "isFolder")
                    && JsonRead.String(item, "gitObjectType") != "tree"
                    && JsonRead.String(item, "path") is not null)
                .Select(change => new ChangedFile(JsonRead.String(change.Item!.Value, "path")!, change.Type ?? "unknown"))
                .ToList();
        }

        return commit with { Comment = comment, CommentTruncated = false, ChangedFiles = files };
    }

    public static CommitRecord? ParseCommit(JsonElement element, string project, RepositoryRecord repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (JsonRead.String(element, "commitId") is not { } id
            || JsonRead.Child(element, "committer") is not { } committer
            || JsonRead.Date(committer, "date") is not { } date)
        {
            return null;
        }

        return new CommitRecord(id, repository.Id, project, date, JsonRead.String(element, "comment") ?? string.Empty)
        {
            RepositoryName = repository.Name,
            AuthorDate = JsonRead.Child(element, "author") is { } author ? JsonRead.Date(author, "date") : null,
            CommentTruncated = JsonRead.Bool(element, "commentTruncated"),
        };
    }
}
