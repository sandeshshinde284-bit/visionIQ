using System.Globalization;
using VisionIQ.Api.Features.Backfill;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Links;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Cli;

public interface IAdoChangedPullRequestReader
{
    Task<(PullRequestRecord PullRequest, RepositoryRecord Repository)?> ReadAsync(
        string project, string repositoryId, int id, CancellationToken cancellationToken = default);
}

public sealed class AdoChangedPullRequestReader(AdoRepositorySource source) : IAdoChangedPullRequestReader
{
    public Task<(PullRequestRecord PullRequest, RepositoryRecord Repository)?> ReadAsync(
        string project, string repositoryId, int id, CancellationToken cancellationToken = default) =>
        source.GetPullRequestAsync(project, repositoryId, id, cancellationToken);
}

/// <summary>
/// LRN-12: applies a PR event by re-fetching the PR's CURRENT state (status, reviewers and votes, linked work
/// items, commits) and writing it exactly as the sync does: same stream, key, link records and evidence chain.
/// Because the current state is re-read, replayed, late or out-of-order events end in the same stored result;
/// identical content is stored once (content-addressed revisions, B-9). A PR event only records facts: links stay
/// explicit claims and nothing here can mark a root cause as verified (that needs human review).
/// </summary>
public sealed class AdoPullRequestChangeHandler : ISourceChangeHandler
{
    private readonly AdoOptions options;
    private readonly HashSet<string> approvedProjects;
    private readonly IAdoChangedPullRequestReader reader;
    private readonly ICanonicalEvidenceSink evidence;

    public AdoPullRequestChangeHandler(AdoOptions options, IAdoChangedPullRequestReader reader, ICanonicalEvidenceSink evidence)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(evidence);
        this.options = options;
        approvedProjects = new HashSet<string>(options.Projects, StringComparer.Ordinal);
        this.reader = reader;
        this.evidence = evidence;
    }

    public async Task ApplyAsync(SourceChangeNotification change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        change.Validate();
        if (!string.Equals(change.ProviderKind, "pull_request", StringComparison.Ordinal))
        {
            throw new NotSupportedException("Unsupported event kind; do not acknowledge it.");
        }

        var sourcePrefix = options.Tenant + "/ado/";
        var project = change.SourceId.StartsWith(sourcePrefix, StringComparison.Ordinal) ? change.SourceId[sourcePrefix.Length..] : null;
        if (project is null || !approvedProjects.Contains(project))
        {
            throw new InvalidOperationException("Notification project is not approved for ingestion.");
        }

        var separator = change.NativeId.LastIndexOf(':');
        if (separator < 1 ||
            !int.TryParse(change.NativeId[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            throw new ArgumentException("Notification requires a repository ID and a numeric PR ID.");
        }

        var repositoryId = change.NativeId[..separator];
        if (await reader.ReadAsync(project, repositoryId, id, cancellationToken).ConfigureAwait(false) is not { } read ||
            read.PullRequest.Id != id ||
            !string.Equals(read.Repository.Id, repositoryId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(read.Repository.Project, project, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The PR is missing, inaccessible, or not in the approved project.");
        }

        // Same repository allowlist as the sync (by name; empty means every repository).
        if (options.Repositories.Count > 0 &&
            !options.Repositories.Contains(read.Repository.Name, StringComparer.OrdinalIgnoreCase))
        {
            return; // not an approved repository: acknowledge, store nothing
        }

        var pullRequest = read.PullRequest;
        var version = pullRequest.Status + "@" + pullRequest.LastActivity.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var prefix = options.Tenant + "/ado/" + project + "/";
        await evidence.WriteAsync("pull-requests",
            prefix + "pull-request/" + pullRequest.Id.ToString(CultureInfo.InvariantCulture) + "@" + version,
            pullRequest, cancellationToken).ConfigureAwait(false);
        // Match SyncRunner exactly: reviewer votes are independent, versioned source facts.
        // Persist these before completing the event inbox; a failed write is retried.
        await PullRequestReviewProjection.WriteAsync(evidence, options.Tenant, pullRequest, cancellationToken)
            .ConfigureAwait(false);
        foreach (var link in LinkExtractor.FromPullRequest(pullRequest))
        {
            await evidence.WriteAsync("links", prefix + "link/" + link.Key, link, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Routes each inbox event to the handler for its kind (work items, deletions, pull requests).</summary>
public sealed class AdoChangeHandler(ISourceChangeHandler workItems, ISourceChangeHandler pullRequests) : ISourceChangeHandler
{
    public Task ApplyAsync(SourceChangeNotification change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        return change.ProviderKind == "pull_request"
            ? pullRequests.ApplyAsync(change, cancellationToken)
            : workItems.ApplyAsync(change, cancellationToken);
    }
}
