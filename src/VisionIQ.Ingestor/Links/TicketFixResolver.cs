using System.Collections.ObjectModel;
using System.Globalization;
using VisionIQ.Ingestor.Ado;

namespace VisionIQ.Ingestor.Links;

/// <summary>
/// Distinguishes source-observed references from hypotheses and reviewed fix assertions.
/// Explicit means the source states a relationship; it does not establish causality.
/// </summary>
public enum FixEvidenceStatus
{
    Unknown,
    Inferred,
    Explicit,
    Verified,
    Disputed,
}

/// <summary>A ticket-to-PR/commit association with independently retained provenance.</summary>
public sealed record TicketFixCandidate(
    string ProjectId,
    string TicketId,
    string? RepositoryId,
    string? PullRequestId,
    string? CommitSha,
    FixEvidenceStatus Status,
    IReadOnlyList<string> EvidenceTypes,
    IReadOnlyList<string> RevertCommitShas,
    bool PullRequestCompleted)
{
    /// <summary>Stable identity for one ticket/PR/commit association within its source scope.</summary>
    public string AssociationKey =>
        string.Join("/", new[] { ProjectId, TicketId, RepositoryId ?? "-", PullRequestId ?? "-", CommitSha ?? "-" }
            .Select(value => Uri.EscapeDataString(value)));

    /// <summary>True only if an authorized review has separately verified the actual fix.</summary>
    public bool IsVerifiedFix => Status == FixEvidenceStatus.Verified && RevertCommitShas.Count == 0;
}

/// <summary>
/// Resolves only documented ticket, PR, commit and revert references from source records.
/// No timestamp proximity, filename similarity or AI inference is promoted to proof.
/// </summary>
public static class TicketFixResolver
{
    /// <summary>
    /// Projects source-declared relationships into deterministic candidates. PRs in other
    /// projects are excluded and ambiguous abbreviated revert SHAs are not resolved.
    /// </summary>
    public static IReadOnlyList<TicketFixCandidate> Resolve(
        string projectId,
        IEnumerable<PullRequestRecord> pullRequests,
        IEnumerable<CommitRecord> commits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(pullRequests);
        ArgumentNullException.ThrowIfNull(commits);

        var sourcePrs = pullRequests.Where(pr => string.Equals(pr.Project, projectId, StringComparison.Ordinal))
            .OrderBy(pr => pr.RepositoryId, StringComparer.Ordinal)
            .ThenBy(pr => pr.Id)
            .ToArray();
        var sourceCommits = commits.Where(commit => string.Equals(commit.Project, projectId, StringComparison.Ordinal))
            .ToArray();
        var reverts = GetReverts(sourceCommits);
        var candidates = new Dictionary<string, TicketFixCandidate>(StringComparer.Ordinal);

        foreach (var pr in sourcePrs)
        {
            var completed = string.Equals(pr.Status, "completed", StringComparison.OrdinalIgnoreCase);
            var commitIds = pr.CommitIds
                .Concat(completed && pr.MergeCommitId is not null ? [pr.MergeCommitId] : [])
                .Where(sha => !string.IsNullOrWhiteSpace(sha))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var ticketId in pr.WorkItemIds.Distinct().Order())
            {
                if (ticketId <= 0)
                {
                    continue;
                }

                var ticket = ticketId.ToString(CultureInfo.InvariantCulture);
                foreach (var commitSha in commitIds.Length == 0 ? new string?[] { null } : commitIds)
                {
                    var types = commitSha is null
                        ? new[] { "pr_work_item" }
                        : new[] { "pr_work_item", commitSha == pr.MergeCommitId ? "pr_merge_commit" : "pr_commit" };
                    var candidate = new TicketFixCandidate(
                        projectId, ticket, pr.RepositoryId,
                        pr.Id.ToString(CultureInfo.InvariantCulture),
                        commitSha, FixEvidenceStatus.Explicit, types,
                        FindRevertShas(commitSha, reverts), completed);
                    candidates[candidate.AssociationKey] = candidate;
                }
            }
        }

        foreach (var commit in sourceCommits)
        {
            foreach (var link in LinkExtractor.FromCommit(commit)
                .Where(link => link.ToKind == EntityKind.WorkItem && link.LinkType == "commit_mention"))
            {
                // A commit message explicitly mentioning a ticket is NOT proof that it fixed it.
                var candidate = new TicketFixCandidate(projectId, link.ToId, commit.RepositoryId,
                    null, commit.CommitId, FixEvidenceStatus.Explicit,
                    ["commit_mention"], FindRevertShas(commit.CommitId, reverts), false);
                candidates.TryAdd(candidate.AssociationKey, candidate);
            }
        }

        return new ReadOnlyCollection<TicketFixCandidate>(candidates.Values
            .OrderBy(item => item.TicketId, StringComparer.Ordinal)
            .ThenBy(item => item.RepositoryId, StringComparer.Ordinal)
            .ThenBy(item => item.PullRequestId, StringComparer.Ordinal)
            .ThenBy(item => item.CommitSha, StringComparer.Ordinal)
            .ToArray());
    }

    /// <summary>Registers an unverified possible association without claiming a source reference.</summary>
    public static TicketFixCandidate Propose(
        string projectId, string ticketId, string? repositoryId, string? pullRequestId, string? commitSha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketId);
        if (string.IsNullOrWhiteSpace(pullRequestId) && string.IsNullOrWhiteSpace(commitSha))
        {
            throw new ArgumentException("A proposed link must identify a PR or commit.");
        }

        return new TicketFixCandidate(projectId, ticketId, repositoryId, pullRequestId,
            commitSha, FixEvidenceStatus.Inferred, [], [], false);
    }

    /// <summary>
    /// Applies a documented human review result; the source association remains immutable.
    /// A disputed candidate never becomes a verified fix without an explicit new review.
    /// </summary>
    public static TicketFixCandidate ApplyReview(
        TicketFixCandidate candidate, FixEvidenceStatus decision, string reviewReference)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewReference);
        if (decision is not (FixEvidenceStatus.Verified or FixEvidenceStatus.Disputed))
        {
            throw new ArgumentOutOfRangeException(nameof(decision), "Only reviewed outcomes may be applied.");
        }

        if (decision == FixEvidenceStatus.Verified && candidate.RevertCommitShas.Count > 0)
        {
            throw new InvalidOperationException("A reverted association requires a fresh fix revision for verification.");
        }

        return candidate with { Status = decision };
    }

    /// <summary>Indexes only exact or unambiguous SHA-prefix revert references.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> GetReverts(
        IReadOnlyList<CommitRecord> commits)
    {
        var shaSet = commits.Select(commit => commit.CommitId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var commit in commits)
        {
            foreach (var link in LinkExtractor.FromCommit(commit).Where(link => link.LinkType == "reverts"))
            {
                var matches = shaSet.Where(sha =>
                    sha.StartsWith(link.ToId, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(sha, commit.CommitId, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1)
                {
                    continue;
                }

                if (!map.TryGetValue(matches[0], out var reverting))
                {
                    reverting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    map[matches[0]] = reverting;
                }

                reverting.Add(commit.CommitId);
            }
        }

        return map.ToDictionary(entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value.Order(StringComparer.Ordinal).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Returns all source-observed reverting commits for a candidate revision.</summary>
    private static IReadOnlyList<string> FindRevertShas(
        string? commitSha, IReadOnlyDictionary<string, IReadOnlyList<string>> reverts)
    {
        return commitSha is not null && reverts.TryGetValue(commitSha, out var shas)
            ? shas : [];
    }
}
