using VisionIQ.Api.Features.TicketFixes;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Cli;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Tests;

/// <summary>LRN-01/03/04 live-record handoff tests with no external secrets or source calls.</summary>
public sealed class TicketFixProjectionSinkTests
{
    private static readonly Uri Organization = new("https://dev.azure.com/contoso");

    /// <summary>Builds a completed PR with explicit ADO ticket and commit references.</summary>
    private static PullRequestRecord PullRequest() =>
        new(42, "repo-id", "repo", "Project-A", "completed", "Fix", DateTimeOffset.UtcNow)
        {
            WorkItemIds = [123, 456],
            CommitIds = [new string('a', 40)],
        };

    [Fact]
    public async Task One_pr_writes_canonical_evidence_before_projecting_two_links()
    {
        var evidence = new RecordingEvidenceSink();
        var links = new RecordingLinkStore();
        var sink = new TicketFixProjectionSink(evidence, links, Organization);
        var record = PullRequest();

        await sink.WriteAsync("pull-requests", "source/pr-42", record);

        Assert.Equal(new[] { "pull-requests" }, evidence.Streams);
        Assert.Equal(2, links.Claims.Count);
        Assert.Equal(new[] { "123", "456" }, links.Claims.Select(claim => claim.TicketId));
        Assert.All(links.Claims, claim =>
        {
            Assert.Equal("Project-A", claim.ProjectId);
            Assert.Equal("repo-id", claim.RepositoryId);
            Assert.Equal(TicketFixSourceClassification.Explicit, claim.Classification);
            Assert.Single(claim.SourceEvidenceRefs);
            Assert.StartsWith("evidence:", claim.SourceEvidenceRefs[0]);
            Assert.Equal(new string('a', 40), claim.CommitSha);
        });
    }

    [Fact]
    public async Task Work_item_artifact_relations_keep_the_repository_stated_in_the_link()
    {
        var evidence = new RecordingEvidenceSink();
        var links = new RecordingLinkStore();
        var sink = new TicketFixProjectionSink(evidence, links, Organization);
        var sha = new string('c', 40);
        var item = new WorkItemRecord(123, 7, "Bug", "Closed", "Title", [],
            DateTimeOffset.UtcNow)
        {
            Project = "Project-A",
            Relations =
            [
                new WorkItemRelation("ArtifactLink",
                    "vstfs:///Git/PullRequestId/project%2Frepo%2F42", "Fixed in PR"),
                new WorkItemRelation("ArtifactLink",
                    "vstfs:///Git/Commit/project%2Frepo%2F" + sha, "Commit"),
            ],
        };

        await sink.WriteAsync("work-items", "source/work-item", item);

        Assert.Equal(new[] { "work-items" }, evidence.Streams);
        Assert.Equal(2, links.Claims.Count);
        Assert.All(links.Claims, link =>
        {
            // B-11: the repository segment of the artifact URL is source data, not a guess.
            Assert.Equal("repo", link.RepositoryId);
            Assert.Equal("Project-A", link.ProjectId);
            Assert.Equal("123", link.TicketId);
            Assert.Equal(TicketFixSourceClassification.Explicit, link.Classification);
            Assert.Single(link.SourceEvidenceRefs);
        });
        Assert.Contains(links.Claims, link => link.PullRequestId == "42");
        Assert.Contains(links.Claims, link => link.CommitSha == sha);
    }

    [Fact]
    public async Task No_ticket_reference_produces_no_fix_even_if_the_pr_is_completed()
    {
        var links = new RecordingLinkStore();
        var sink = new TicketFixProjectionSink(new RecordingEvidenceSink(), links, Organization);
        await sink.WriteAsync("pull-requests", "source/pr-42",
            PullRequest() with { WorkItemIds = [] });
        Assert.Empty(links.Claims);
    }

    [Fact]
    public async Task A_ticket_mentioned_in_a_commit_is_only_an_inferred_reference()
    {
        var links = new RecordingLinkStore();
        var sink = new TicketFixProjectionSink(new RecordingEvidenceSink(), links, Organization);
        var commit = new CommitRecord(new string('b', 40), "repo-id", "Project-A",
            DateTimeOffset.UtcNow, "Investigation AB#321")
        { RepositoryName = "repo" };
        await sink.WriteAsync("commits", "source/commit", commit);

        var claim = Assert.Single(links.Claims);
        Assert.Equal("321", claim.TicketId);
        Assert.Null(claim.PullRequestId);
        Assert.Equal(TicketFixSourceClassification.Inferred, claim.Classification); // M-7
    }

    [Fact]
    public async Task Exact_revert_is_projected_but_ambiguous_short_sha_is_not()
    {
        var links = new RecordingLinkStore();
        var sink = new TicketFixProjectionSink(new RecordingEvidenceSink(), links, Organization);
        var reverted = new string('a', 40);
        var revertCommit = new CommitRecord(new string('b', 40), "repo-id", "Project-A",
            DateTimeOffset.UtcNow, "This reverts commit " + reverted)
        {
            RepositoryName = "repo",
        };
        await sink.WriteAsync("commits", "source/revert", revertCommit);
        Assert.Equal((reverted, revertCommit.CommitId), Assert.Single(links.Reverts));

        var shortSha = revertCommit with
        {
            CommitId = new string('c', 40),
            Comment = "This reverts commit abc1234",
        };
        await sink.WriteAsync("commits", "source/short", shortSha);
        Assert.Single(links.Reverts);
    }

    [Fact]
    public async Task An_abandoned_or_still_active_pr_is_kept_as_associated_work_never_as_a_fix()
    {
        // Review R4-03: confirmed against real ADO data (6 abandoned and 7 active PRs in the
        // Genoa Kiosk org carry WorkItemIds, e.g. abandoned PR 38497 -> work items 116399/116556).
        // The relationship ADO declared is kept, classified Unknown, so it is never mistaken for a
        // fix by any reader (they all require Explicit or Inferred) but is not silently discarded.
        var links = new RecordingLinkStore();
        var sink = new TicketFixProjectionSink(new RecordingEvidenceSink(), links, Organization);
        var abandoned = PullRequest() with { Id = 38497, Status = "abandoned", WorkItemIds = [116399, 116556] };
        var active = PullRequest() with { Id = 38241, Status = "active", WorkItemIds = [114985] };

        await sink.WriteAsync("pull-requests", "source/abandoned", abandoned);
        await sink.WriteAsync("pull-requests", "source/active", active);
        Assert.NotEmpty(links.Claims);
        Assert.All(links.Claims, claim => Assert.Equal(TicketFixSourceClassification.Unknown, claim.Classification));

        // Once ADO reports it completed, the same association upgrades to Explicit (never downgrades).
        await sink.WriteAsync("pull-requests", "source/completed", active with { Status = "completed" });
        Assert.Contains(links.Claims, claim => claim.TicketId == "114985" && claim.Classification == TicketFixSourceClassification.Explicit);
    }

    [Fact]
    public async Task A_pr_titled_as_ados_own_revert_of_a_pr_seen_earlier_records_a_candidate_not_a_confirmed_revert()
    {
        // Review R4-07, second-pass correction (independent review): reproduces real ADO data (PR
        // 38386 "KioskSpecific - Enable Tremble alarm on Beeston Double " merged as e158cff...; PR
        // 38394 "Revert 'KioskSpecific - ... Double '" merged as a7c4aadf..., both linked to work item
        // 110276). A title+SHA match is circumstantial, not proof one commit's diff reverses the
        // other's -- so it is recorded as a candidate (a queryable evidence reference), never a
        // confirmed revert that would hide the original fix with the same confidence as a literal
        // "This reverts commit <sha>" message.
        var links = new RecordingLinkStore();
        var sink = new TicketFixProjectionSink(new RecordingEvidenceSink(), links, Organization);
        var originalSha = new string('1', 40);
        var revertSha = new string('2', 40);
        var original = new PullRequestRecord(38386, "repo-id", "repo", "Project-A", "completed",
            "KioskSpecific - Enable Tremble alarm on Beeston Double ", DateTimeOffset.UtcNow)
        {
            WorkItemIds = [110276],
            CommitIds = [originalSha],
            MergeCommitId = originalSha,
        };
        var revert = original with
        {
            Id = 38394,
            Title = "Revert 'KioskSpecific - Enable Tremble alarm on Beeston Double '",
            CommitIds = [revertSha],
            MergeCommitId = revertSha,
        };

        await sink.WriteAsync("pull-requests", "source/original", original);
        await sink.WriteAsync("pull-requests", "source/revert", revert);

        Assert.Empty(links.Reverts);
        Assert.Contains(links.Claims, claim =>
            claim.PullRequestId == "38386" && claim.TicketId == "110276" && claim.CommitSha == originalSha &&
            claim.SourceEvidenceRefs.Any(reference =>
                reference.StartsWith("candidate-revert:pr-title-match:pr38394:", StringComparison.Ordinal) &&
                reference.EndsWith(revertSha, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task A_title_matched_candidate_revert_never_reactivates_a_link_a_newer_revision_already_retracted()
    {
        // Third-pass correction, Finding #1: UpsertSourceAsync (used by every direct, explicit
        // source-stated claim) always clears a prior retraction -- correct for fresh evidence, but
        // RecordTitleRevertAsync's candidate marker is circumstantial, not fresh authoritative evidence.
        // If the ORIGINAL ticket/PR/commit association was already retracted by a newer, real ADO
        // snapshot (the ticket was genuinely unlinked), a later title-matched "revert" candidate must
        // never resurrect it.
        var links = new RecordingLinkStore();
        var sink = new TicketFixProjectionSink(new RecordingEvidenceSink(), links, Organization);
        var originalSha = new string('1', 40);
        var revertSha = new string('2', 40);
        var originalV1 = new PullRequestRecord(38386, "repo-id", "repo", "Project-A", "completed",
            "KioskSpecific - Enable Tremble alarm on Beeston Double ", DateTimeOffset.UtcNow.AddHours(-2))
        {
            WorkItemIds = [110276],
            CommitIds = [originalSha],
            MergeCommitId = originalSha,
            ClosedDate = DateTimeOffset.UtcNow.AddHours(-2),
        };
        var key = new TicketFixClaim(
            Organization.AbsoluteUri.TrimEnd('/'), originalV1.Project, "110276", originalV1.RepositoryId,
            originalV1.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), originalSha,
            TicketFixSourceClassification.Explicit, ["x"], []).AssociationKey();

        await sink.WriteAsync("pull-requests", "source/original-v1", originalV1);
        Assert.DoesNotContain(key, links.RetractedKeys);

        // A newer, authoritative revision of the SAME PR drops the ticket entirely -- correctly
        // retracts the association (Review R4-02).
        var originalV2 = originalV1 with { WorkItemIds = [], ClosedDate = DateTimeOffset.UtcNow.AddHours(-1) };
        await sink.WriteAsync("pull-requests", "source/original-v2", originalV2);
        Assert.Contains(key, links.RetractedKeys);

        // A pull request titled as ADO's own revert of the original now arrives.
        var revert = originalV1 with
        {
            Id = 38394,
            Title = "Revert 'KioskSpecific - Enable Tremble alarm on Beeston Double '",
            CommitIds = [revertSha],
            MergeCommitId = revertSha,
            ClosedDate = DateTimeOffset.UtcNow,
        };
        await sink.WriteAsync("pull-requests", "source/revert", revert);

        // The candidate marker is still recorded as queryable evidence...
        Assert.Contains(links.Claims, claim =>
            claim.TicketId == "110276" && claim.CommitSha == originalSha &&
            claim.SourceEvidenceRefs.Any(reference =>
                reference.StartsWith("candidate-revert:pr-title-match:pr38394:", StringComparison.Ordinal)));
        // ...but must NOT reactivate a retraction a newer, authoritative snapshot already established.
        Assert.Contains(key, links.RetractedKeys);
    }

    [Fact]
    public async Task Two_pull_requests_sharing_the_same_title_are_never_guessed_as_the_revert_target()
    {
        // Feedback from a second review: a title is not a unique identifier like a Git SHA. If two
        // different pull requests in the same repository happen to share the exact original title,
        // neither is picked as "the" one that was reverted.
        var links = new RecordingLinkStore();
        var sink = new TicketFixProjectionSink(new RecordingEvidenceSink(), links, Organization);
        var titled = PullRequest() with { Title = "Shared title", CommitIds = [new string('1', 40)] };
        var sameTitleAgain = titled with { Id = 43, CommitIds = [new string('3', 40)] };
        var revert = titled with
        {
            Id = 44,
            Title = "Revert \"Shared title\"",
            CommitIds = [new string('4', 40)],
        };

        await sink.WriteAsync("pull-requests", "source/one", titled);
        await sink.WriteAsync("pull-requests", "source/two", sameTitleAgain);
        await sink.WriteAsync("pull-requests", "source/revert", revert);

        Assert.Empty(links.Reverts);
    }

    [Fact]
    public async Task A_revert_titled_pr_with_no_earlier_match_in_this_process_records_no_revert()
    {
        // The title alone is never enough; without a same-title pull request seen earlier
        // (e.g. a different sync window, or a webhook host that just started), nothing is guessed.
        var links = new RecordingLinkStore();
        var sink = new TicketFixProjectionSink(new RecordingEvidenceSink(), links, Organization);
        var revert = PullRequest() with
        {
            Id = 38155,
            Title = "Revert back to the original html to image package",
        };

        await sink.WriteAsync("pull-requests", "source/revert-only", revert);

        Assert.Empty(links.Reverts);
        Assert.Contains(links.Claims, claim => claim.PullRequestId == "38155"); // still a normal explicit fix claim
    }

    [Fact]
    public async Task Unlinking_a_ticket_from_a_prs_next_revision_retracts_it()
    {
        // Review R4-02: ADO states ticket 123 ↔ PR 42 at LastActivity T1, then a later revision of
        // the same PR (T2) no longer lists ticket 123. The association must be retracted, not left
        // stale -- but only once the sink has observed the newer, complete revision.
        var links = new RecordingLinkStore();
        var sink = new TicketFixProjectionSink(new RecordingEvidenceSink(), links, Organization);
        var t1 = DateTimeOffset.UtcNow.AddHours(-1);
        var t2 = DateTimeOffset.UtcNow;
        var withTicket = PullRequest() with { ClosedDate = t1 };
        var withoutTicket = withTicket with { WorkItemIds = [456], ClosedDate = t2 };

        await sink.WriteAsync("pull-requests", "source/pr-42-v1", withTicket);
        var key = links.Claims.First(claim => claim.TicketId == "123").AssociationKey();
        Assert.DoesNotContain(key, links.RetractedKeys);

        await sink.WriteAsync("pull-requests", "source/pr-42-v2", withoutTicket);
        Assert.Contains(key, links.RetractedKeys);
    }

    [Fact]
    public async Task A_stale_replayed_pr_revision_never_reactivates_an_already_retracted_link()
    {
        // Review R4-02 (second-pass correction, the "most important issue" from a second review):
        // revision 2 (newer) drops ticket 123 and correctly retracts it. Revision 1 (older) then
        // arrives late (a backfill/replay). It must NOT reactivate the retraction, even though it
        // still asserts ticket 123 as a claim -- the claim is recorded as historical evidence only.
        var links = new RecordingLinkStore();
        var sink = new TicketFixProjectionSink(new RecordingEvidenceSink(), links, Organization);
        var t1 = DateTimeOffset.UtcNow.AddHours(-2);
        var t2 = DateTimeOffset.UtcNow.AddHours(-1);
        var revision1 = PullRequest() with { WorkItemIds = [123], ClosedDate = t1 };
        var revision2 = revision1 with { WorkItemIds = [456], ClosedDate = t2 };
        var key = new TicketFixClaim(Organization.AbsoluteUri.TrimEnd('/'), revision1.Project,
            "123", revision1.RepositoryId, revision1.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            revision1.CommitIds[0], TicketFixSourceClassification.Explicit, ["x"], [])
            .AssociationKey();

        await sink.WriteAsync("pull-requests", "source/pr-42-v1", revision1);
        await sink.WriteAsync("pull-requests", "source/pr-42-v2", revision2);
        Assert.Contains(key, links.RetractedKeys);

        // revision1 replayed again (older than the already-accepted revision2)
        await sink.WriteAsync("pull-requests", "source/pr-42-v1-replay", revision1);
        Assert.Contains(key, links.RetractedKeys); // still retracted -- not reactivated
    }

    [Fact]
    public async Task A_complete_snapshot_with_zero_links_still_retracts_everything_previously_asserted()
    {
        // Review R4-02: a legitimate "ADO now says this PR has no linked tickets at all" snapshot must
        // retract every previously asserted association from this source, not be treated as a no-op
        // because the current claim list happens to be empty.
        var links = new RecordingLinkStore();
        var sink = new TicketFixProjectionSink(new RecordingEvidenceSink(), links, Organization);
        var withTickets = PullRequest() with { ClosedDate = DateTimeOffset.UtcNow.AddHours(-1) };
        var keys = withTickets.WorkItemIds.Select(id => new TicketFixClaim(
            Organization.AbsoluteUri.TrimEnd('/'), withTickets.Project, id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            withTickets.RepositoryId, withTickets.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            withTickets.CommitIds[0], TicketFixSourceClassification.Explicit, ["x"], []).AssociationKey()).ToList();
        await sink.WriteAsync("pull-requests", "source/pr-42-v1", withTickets);
        Assert.All(keys, key => Assert.DoesNotContain(key, links.RetractedKeys));

        var noLinks = withTickets with { WorkItemIds = [], ClosedDate = DateTimeOffset.UtcNow };
        await sink.WriteAsync("pull-requests", "source/pr-42-v2", noLinks);

        Assert.All(keys, key => Assert.Contains(key, links.RetractedKeys));
    }

    [Fact]
    public async Task Failed_canonical_archive_never_persists_unbacked_links()
    {
        var evidence = new RecordingEvidenceSink { Fail = true };
        var links = new RecordingLinkStore();
        var sink = new TicketFixProjectionSink(evidence, links, Organization);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.WriteAsync("pull-requests", "source/pr-42", PullRequest()));
        Assert.Empty(links.Claims);
    }

    /// <summary>Records only the source stream of successful canonical archive calls.</summary>
    [Fact]
    public async Task A_malformed_artifact_link_never_gets_a_guessed_repository()
    {
        var links = new RecordingLinkStore();
        var sink = new TicketFixProjectionSink(new RecordingEvidenceSink(), links, Organization);
        var item = new WorkItemRecord(123, 7, "Bug", "Closed", "Title", [], DateTimeOffset.UtcNow)
        {
            Project = "Project-A",
            Relations = [new WorkItemRelation("ArtifactLink", "vstfs:///Git/PullRequestId/42", "Fixed in PR")],
        };

        await sink.WriteAsync("work-items", "source/work-item", item);

        Assert.Null(Assert.Single(links.Claims).RepositoryId);
    }

    private sealed class RecordingEvidenceSink : ICanonicalEvidenceSink
    {
        public bool Fail { get; init; }
        public List<string> Streams { get; } = [];

        /// <summary>Fails before recording when archive storage is unavailable.</summary>
        public Task WriteAsync(string stream, string key, object record,
            CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new InvalidOperationException("Test archive unavailable.");
            }

            Streams.Add(stream);
            return Task.CompletedTask;
        }
    }

    /// <summary>Observes only the link-store calls and never changes source permissions.</summary>
    private sealed class RecordingLinkStore : ITicketFixLinkStore
    {
        public List<TicketFixClaim> Claims { get; } = [];
        public List<(string Original, string Reverting)> Reverts { get; } = [];

        /// <summary>Review R4-02: a simplified in-memory mirror of PostgresTicketFixLinkStore's
        /// revision-ordering and cross-source retraction rules, for sink-level wiring tests. The
        /// authoritative behavior is verified separately, directly against PostgreSQL.</summary>
        public HashSet<string> RetractedKeys { get; } = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTimeOffset> acceptedRevisionTime = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> currentBySource = new(StringComparer.Ordinal);

        /// <summary>Records a source-declared candidate in memory.</summary>
        public Task<string> UpsertSourceAsync(TicketFixClaim claim,
            CancellationToken cancellationToken = default)
        {
            Claims.Add(claim);
            RetractedKeys.Remove(claim.AssociationKey());
            return Task.FromResult(claim.AssociationKey());
        }

        /// <summary>Third-pass correction, Finding #1: records evidence without ever clearing a
        /// retraction -- mirrors PostgresTicketFixLinkStore.UpsertCandidateEvidenceAsync.</summary>
        public Task<string> UpsertCandidateEvidenceAsync(TicketFixClaim claim,
            CancellationToken cancellationToken = default)
        {
            Claims.Add(claim);
            return Task.FromResult(claim.AssociationKey());
        }

        public Task ReconcileSourceAsync(string sourceItemKey, string sourceRevisionId, DateTimeOffset sourceRevisionTime,
            bool isCompleteSnapshot, IReadOnlyList<TicketFixClaim> claims, CancellationToken cancellationToken = default)
        {
            Claims.AddRange(claims);
            var accepted = isCompleteSnapshot &&
                (!acceptedRevisionTime.TryGetValue(sourceItemKey, out var last) || sourceRevisionTime > last);
            if (!accepted)
            {
                return Task.CompletedTask;
            }

            acceptedRevisionTime[sourceItemKey] = sourceRevisionTime;
            var keys = claims.Select(claim => claim.AssociationKey()).ToHashSet(StringComparer.Ordinal);
            if (currentBySource.TryGetValue(sourceItemKey, out var previous))
            {
                foreach (var missing in previous.Except(keys))
                {
                    var stillAssertedElsewhere = currentBySource.Any(other =>
                        other.Key != sourceItemKey && other.Value.Contains(missing));
                    if (!stillAssertedElsewhere)
                    {
                        RetractedKeys.Add(missing);
                    }
                }
            }

            foreach (var key in keys)
            {
                RetractedKeys.Remove(key); // re-assertion always clears a prior retraction
            }

            currentBySource[sourceItemKey] = keys;
            return Task.CompletedTask;
        }

        /// <summary>Records a full SHA revert for the scoped repository.</summary>
        public Task<int> RecordRevertAsync(
            string sourceInstance, string projectId, string repositoryId,
            string originalCommitSha, string revertCommitSha,
            CancellationToken cancellationToken = default)
        {
            Reverts.Add((originalCommitSha, revertCommitSha));
            return Task.FromResult(1);
        }
    }
}
