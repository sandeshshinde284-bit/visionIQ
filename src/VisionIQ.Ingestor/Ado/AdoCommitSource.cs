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

    /// <summary>
    /// M-6: true only when the commit has more changed files than <see cref="AdoCommitSource.MaxChangedFiles"/>;
    /// <see cref="ChangedFiles"/> is then incomplete. Omitted from JSON when false.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ChangedFilesTruncated { get; init; }
}

/// <summary>Reads commit metadata and changed files through the Azure DevOps REST API, without cloning (S04-10).</summary>
public sealed class AdoCommitSource
{
    private const int PageSize = 100;
    private const int ChangesPageSize = 1000;

    /// <summary>M-6: upper bound of changed files read per commit (very large imports or generated code).</summary>
    public const int MaxChangedFiles = 50_000;
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

        // The list endpoint truncates long commit messages; only pay for the extra per-commit call when it did.
        var comment = commit.Comment;
        if (commit.CommentTruncated)
        {
            using var detail = await client.GetAsync(client.BuildUri(project, basePath), cancellationToken).ConfigureAwait(false);
            comment = JsonRead.String(detail.Root, "comment") ?? comment;
        }

        // M-6: page through every change instead of silently stopping at the first 1,000.
        var files = new List<ChangedFile>();
        var truncated = false;
        for (var skip = 0; ; skip += ChangesPageSize)
        {
            if (skip >= MaxChangedFiles)
            {
                truncated = true;
                break;
            }

            using var changes = await client.GetAsync(
                client.BuildUri(project, basePath + "/changes",
                [
                    new("top", ChangesPageSize.ToString(CultureInfo.InvariantCulture)),
                    new("skip", skip.ToString(CultureInfo.InvariantCulture)),
                ]),
                cancellationToken).ConfigureAwait(false);
            var page = JsonRead.Items(changes.Root, "changes").ToList();
            // The changes API mixes in directory entries alongside file changes; both isFolder and gitObjectType
            // are checked because neither flag is reliably present on every item shape ADO returns.
            files.AddRange(page
                .Select(change => (Item: JsonRead.Child(change, "item"), Type: JsonRead.String(change, "changeType")))
                .Where(change => change.Item is { } item
                    && !JsonRead.Bool(item, "isFolder")
                    && JsonRead.String(item, "gitObjectType") != "tree"
                    && JsonRead.String(item, "path") is not null)
                .Select(change => new ChangedFile(JsonRead.String(change.Item!.Value, "path")!, change.Type ?? "unknown")));
            if (page.Count < ChangesPageSize)
            {
                break;
            }
        }

        return commit with { Comment = comment, CommentTruncated = false, ChangedFiles = files, ChangedFilesTruncated = truncated };
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
