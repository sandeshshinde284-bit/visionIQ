using System.Globalization;
using VisionIQ.Api.Features.Evidence;
using VisionIQ.Api.Features.TicketFixes;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Links;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Cli;

/// <summary>
/// Writes immutable LRN-03 evidence first, then projects explicit LRN-04
/// source associations into PostgreSQL. No clock-proximity inference is used.
/// The caller's stream checkpoint cannot advance if either write fails.
/// </summary>
public sealed class TicketFixProjectionSink(
    ICanonicalEvidenceSink evidence,
    ITicketFixLinkStore links,
    Uri adoOrganization) : ICanonicalEvidenceSink
{
    private readonly string sourceInstance = adoOrganization.AbsoluteUri.TrimEnd('/');

    /// <summary>
    /// Archives the complete source record before persisting any links derived
    /// from that record, so each association cites an existing canonical revision.
    /// </summary>
    public async Task WriteAsync(
        string stream, string key, object record,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(record);

        await evidence.WriteAsync(stream, key, record, cancellationToken).ConfigureAwait(false);

        if (stream == "work-items" && record is WorkItemRecord item)
        {
            await ProjectWorkItemAsync(item, cancellationToken).ConfigureAwait(false);
        }
        else if (stream == "pull-requests" && record is PullRequestRecord pr)
        {
            await ProjectPullRequestAsync(pr, cancellationToken).ConfigureAwait(false);
        }
        else if (stream == "commits" && record is CommitRecord commit)
        {
            await ProjectCommitAsync(commit, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Projects native work-item artifact relations without guessing a target
    /// repository. A PR or commit discovered only via a ticket relation retains
    /// null repository scope until an authenticated PR source resolves it.
    /// </summary>
    private async Task ProjectWorkItemAsync(
        WorkItemRecord item, CancellationToken cancellationToken)
    {
        var sourceKey = new EvidenceIdentity(
            "azure_devops", sourceInstance, item.Project, null,
            "work_item_current", item.Id.ToString(CultureInfo.InvariantCulture)).ItemKey();
        var reference = "evidence:" + sourceKey + ":" +
            item.Revision.ToString(CultureInfo.InvariantCulture);

        foreach (var link in LinkExtractor.FromWorkItem(item))
        {
            if (link.ToKind is not (EntityKind.PullRequest or EntityKind.Commit))
            {
                continue;
            }

            // ADO work-item artifact URLs may identify only a PR/commit; never
            // infer repository or project GUID from their display text.
            var claim = new TicketFixClaim(
                sourceInstance, item.Project,
                item.Id.ToString(CultureInfo.InvariantCulture), null,
                link.ToKind == EntityKind.PullRequest ? link.ToId : null,
                link.ToKind == EntityKind.Commit ? link.ToId : null,
                TicketFixSourceClassification.Explicit, [reference], []);
            await links.UpsertSourceAsync(claim, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Saves one claim per ADO-declared ticket/PR/commit association; an
    /// unrelated PR title or adjacent timestamp never creates a claim.
    /// </summary>
    private async Task ProjectPullRequestAsync(
        PullRequestRecord pr, CancellationToken cancellationToken)
    {
        var revision = pr.Status + "@" +
            pr.LastActivity.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var sourceKey = new EvidenceIdentity(
            "azure_devops", sourceInstance, pr.Project, pr.RepositoryId,
            "pull_request", pr.Id.ToString(CultureInfo.InvariantCulture)).ItemKey();
        var reference = "evidence:" + sourceKey + ":" + revision;
        var commits = pr.CommitIds
            .Concat(pr.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)
                && pr.MergeCommitId is not null ? [pr.MergeCommitId] : [])
            .Where(sha => !string.IsNullOrWhiteSpace(sha))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var ticket in pr.WorkItemIds.Where(id => id > 0).Distinct())
        {
            foreach (var sha in commits.Length == 0 ? new string?[] { null } : commits)
            {
                var claim = new TicketFixClaim(
                    sourceInstance, pr.Project,
                    ticket.ToString(CultureInfo.InvariantCulture), pr.RepositoryId,
                    pr.Id.ToString(CultureInfo.InvariantCulture), sha,
                    TicketFixSourceClassification.Explicit, [reference], []);
                await links.UpsertSourceAsync(claim, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Stores explicit ticket references in commit messages, and marks complete
    /// SHA reverts without guessing what an abbreviated SHA means.
    /// </summary>
    private async Task ProjectCommitAsync(
        CommitRecord commit, CancellationToken cancellationToken)
    {
        var sourceKey = new EvidenceIdentity(
            "azure_devops", sourceInstance, commit.Project, commit.RepositoryId,
            "commit", commit.CommitId).ItemKey();
        var reference = "evidence:" + sourceKey + ":" + commit.CommitId;

        foreach (var link in LinkExtractor.FromCommit(commit))
        {
            if (link.LinkType == "commit_mention" && link.ToKind == EntityKind.WorkItem)
            {
                var claim = new TicketFixClaim(
                    sourceInstance, commit.Project, link.ToId, commit.RepositoryId,
                    null, commit.CommitId, TicketFixSourceClassification.Explicit,
                    [reference], []);
                await links.UpsertSourceAsync(claim, cancellationToken).ConfigureAwait(false);
            }
            else if (link.LinkType == "reverts" && link.ToKind == EntityKind.Commit &&
                IsSha(link.ToId) && IsSha(commit.CommitId))
            {
                await links.RecordRevertAsync(
                    sourceInstance, commit.Project, commit.RepositoryId,
                    link.ToId, commit.CommitId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Only complete Git SHA-1 object IDs can identify an exact revert target.</summary>
    private static bool IsSha(string value) =>
        value.Length == 40 && value.All(Uri.IsHexDigit);
}
