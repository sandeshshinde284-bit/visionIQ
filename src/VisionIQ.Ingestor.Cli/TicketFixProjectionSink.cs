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
/// If either write throws, the exception propagates to the caller; the sync runners then
/// report the stream as failed and do not advance its checkpoint (m-12: that guarantee is
/// enforced by the runners, not by this class).
/// </summary>
public sealed class TicketFixProjectionSink(
    ICanonicalEvidenceSink evidence,
    ITicketFixLinkStore links,
    Uri adoOrganization) : ICanonicalEvidenceSink
{
    private readonly string sourceInstance = adoOrganization.AbsoluteUri.TrimEnd('/');

    /// <summary>
    /// Review R4-07: pull requests seen earlier in this sink's lifetime, by exact (project,
    /// repository, title), so a later "Revert '&lt;title&gt;'" pull request can be resolved to the
    /// commit(s) it reverts. A title is not a unique identifier the way a Git SHA is: when two
    /// different pull requests in the same project and repository share the exact title, neither is
    /// used as "the" original (ambiguous, so unresolved rather than guessed). Scoped to one process (a
    /// full CLI sync, or one long-lived webhook host): a revert of a pull request from before this
    /// process started, or from a run that never saw the original, is not resolved here. A persisted
    /// title index would close that gap; not implemented without a migration (see the review's open
    /// items).
    /// </summary>
    private readonly Dictionary<(string Project, string RepositoryId, string Title), List<PullRequestRecord>> pullRequestsByTitle = new();

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
    /// Projects native work-item artifact relations. The repository comes from the
    /// artifact URL's repository segment; a link without one keeps null scope.
    /// </summary>
    private async Task ProjectWorkItemAsync(
        WorkItemRecord item, CancellationToken cancellationToken)
    {
        var sourceKey = new EvidenceIdentity(
            "azure_devops", sourceInstance, item.Project, null,
            "work_item_current", item.Id.ToString(CultureInfo.InvariantCulture)).ItemKey();
        // Must match the stored revision: current work items carry derived fields (environment),
        // so their revision is content-addressed like B-9.
        var reference = "evidence:" + sourceKey + ":" + CanonicalEvidenceSink.ContentRevision(
            item.Revision.ToString(CultureInfo.InvariantCulture), item);

        var claims = new List<TicketFixClaim>();
        foreach (var link in LinkExtractor.FromWorkItem(item))
        {
            if (link.ToKind is not (EntityKind.PullRequest or EntityKind.Commit))
            {
                continue;
            }

            // B-11: the artifact URL itself states the repository ID; links without
            // that exact shape keep a null repository (never guessed from display text).
            claims.Add(new TicketFixClaim(
                sourceInstance, item.Project,
                item.Id.ToString(CultureInfo.InvariantCulture), link.ToRepositoryId,
                link.ToKind == EntityKind.PullRequest ? link.ToId : null,
                link.ToKind == EntityKind.Commit ? link.ToId : null,
                TicketFixSourceClassification.Explicit, [reference], []));
        }

        // Review R4-02: the work item's current revision is read in full or not at all
        // (AdoWorkItemReader.ParseWorkItem never returns a partial Relations list), so this is always
        // a complete snapshot of what this work item currently states -- including when claims is
        // legitimately empty (every relation was removed). ChangedDate orders revisions of this same
        // item; an out-of-order or replayed revision still records its claims as evidence but can
        // never retract what a newer revision already established (PostgresTicketFixLinkStore).
        await links.ReconcileSourceAsync(
            sourceKey, item.Revision.ToString(CultureInfo.InvariantCulture), item.ChangedDate,
            isCompleteSnapshot: true, claims, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves one claim per ADO-declared ticket/PR/commit association; an
    /// unrelated PR title or adjacent timestamp never creates a claim.
    /// </summary>
    private async Task ProjectPullRequestAsync(
        PullRequestRecord pr, CancellationToken cancellationToken)
    {
        // Review R4-07: resolve before recording this PR's own claims, so an exact self-title match
        // (impossible in practice, but kept defensive) can never record a PR as reverting itself.
        await RecordTitleRevertAsync(pr, cancellationToken).ConfigureAwait(false);
        var titleKey = (pr.Project, pr.RepositoryId, pr.Title);
        if (!pullRequestsByTitle.TryGetValue(titleKey, out var sameTitle))
        {
            pullRequestsByTitle[titleKey] = sameTitle = [];
        }

        if (sameTitle.All(seen => seen.Id != pr.Id))
        {
            sameTitle.Add(pr);
        }

        // Review R4-03: a pull request that never merged changed nothing on the target branch, so its
        // association is never stored as proof of a fix. The relationship ADO itself declared is kept
        // (not discarded) as Unknown — associated development work, not yet a fix — because every
        // reader that treats a link as a fix already requires Explicit or Inferred (see the enum's
        // doc comment). The same claim upgrades to Explicit by itself once the PR completes.
        var completed = pr.Status.Equals("completed", StringComparison.OrdinalIgnoreCase);

        // Must match the revision the canonical sink stored (B-9 content revision).
        var revision = CanonicalEvidenceSink.ContentRevision(
            pr.Status + "@" + pr.LastActivity.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), pr);
        var sourceKey = new EvidenceIdentity(
            "azure_devops", sourceInstance, pr.Project, pr.RepositoryId,
            "pull_request", pr.Id.ToString(CultureInfo.InvariantCulture)).ItemKey();
        var reference = "evidence:" + sourceKey + ":" + revision;
        var commits = FixCommitCandidates(pr);

        var claims = new List<TicketFixClaim>();
        foreach (var ticket in pr.WorkItemIds.Where(id => id > 0).Distinct())
        {
            foreach (var sha in commits.Length == 0 ? new string?[] { null } : commits)
            {
                claims.Add(new TicketFixClaim(
                    sourceInstance, pr.Project,
                    ticket.ToString(CultureInfo.InvariantCulture), pr.RepositoryId,
                    pr.Id.ToString(CultureInfo.InvariantCulture), sha,
                    completed ? TicketFixSourceClassification.Explicit : TicketFixSourceClassification.Unknown,
                    [reference], []));
            }
        }

        // Review R4-02: WorkItemIds and CommitIds -- the only two fields claims are built from -- are
        // read by AdoRepositorySource.EnrichAsync outside its gap-tolerant TryAsync calls, so a failed
        // fetch of either throws and this method is never reached with a partial list; only the
        // unrelated iterations/changed-files/threads/policies sub-fetches may report a gap. This is a
        // complete snapshot, including when claims is legitimately empty.
        // Known accepted limitation: the "/workitems" endpoint is read as a single page with no
        // continuation-token handling in AdoRepositorySource; if Azure DevOps ever caps that response
        // without signalling it, this would be treated as complete when it was not. Not independently
        // verified against a live organization.
        await links.ReconcileSourceAsync(
            sourceKey, revision, pr.LastActivity, isCompleteSnapshot: true, claims, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Review R4-07: when this pull request's title is Azure DevOps' own
    /// <c>Revert "&lt;original title&gt;"</c> and exactly one pull request with that exact title, in
    /// the same project and repository, was seen earlier in this process, record a revert for every
    /// pair of full Git SHAs between the two (the original's merge/iteration commits and this PR's
    /// own). A title match alone never records a revert: both sides must also agree on a real commit
    /// SHA, and an ambiguous title (more than one earlier match) resolves nothing.
    /// </summary>
    private async Task RecordTitleRevertAsync(PullRequestRecord pr, CancellationToken cancellationToken)
    {
        var originalTitle = LinkExtractor.QuotedRevertOriginalTitle(pr.Title);
        if (originalTitle is null ||
            !pullRequestsByTitle.TryGetValue((pr.Project, pr.RepositoryId, originalTitle), out var candidates))
        {
            return;
        }

        var matches = candidates.Where(candidate => candidate.Id != pr.Id).ToList();
        if (matches.Count != 1)
        {
            return; // no earlier match, or an ambiguous title shared by several pull requests
        }

        var original = matches[0];
        foreach (var originalSha in FixCommitCandidates(original))
        {
            foreach (var revertSha in FixCommitCandidates(pr))
            {
                if (IsSha(originalSha) && IsSha(revertSha) &&
                    !string.Equals(originalSha, revertSha, StringComparison.OrdinalIgnoreCase))
                {
                    await links.RecordRevertAsync(
                        sourceInstance, pr.Project, pr.RepositoryId,
                        originalSha, revertSha, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>The commit(s) a pull request actually put on the target branch: its merge commit once
    /// completed, plus its iteration commits (used while still open, and as revert-SHA candidates).</summary>
    private static string[] FixCommitCandidates(PullRequestRecord pr) =>
        pr.CommitIds
            .Concat(pr.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)
                && pr.MergeCommitId is not null ? [pr.MergeCommitId] : [])
            .Where(sha => !string.IsNullOrWhiteSpace(sha))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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
        var reference = "evidence:" + sourceKey + ":" + CanonicalEvidenceSink.ContentRevision(commit.CommitId, commit);

        foreach (var link in LinkExtractor.FromCommit(commit))
        {
            if (link.LinkType == "commit_mention" && link.ToKind == EntityKind.WorkItem)
            {
                // M-7: a "#1234" mention may mean "related to" or "partial work", so it is stored as
                // INFERRED (reviewable, not traversed as proof). ADO development links stay explicit,
                // and the store never downgrades an explicit association to inferred.
                var claim = new TicketFixClaim(
                    sourceInstance, commit.Project, link.ToId, commit.RepositoryId,
                    null, commit.CommitId, TicketFixSourceClassification.Inferred,
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
