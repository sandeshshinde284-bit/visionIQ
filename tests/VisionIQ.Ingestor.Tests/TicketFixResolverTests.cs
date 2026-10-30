using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Links;

namespace VisionIQ.Ingestor.Tests;

/// <summary>Acceptance-oriented deterministic link and revert checks for LRN-04.</summary>
public sealed class TicketFixResolverTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Returns a source-declared PR with optional explicitly related commits.</summary>
    private static PullRequestRecord Pr(int id, int[] ticketIds, string[] commitIds,
        string repository = "repo-a", string status = "completed") =>
        new(id, repository, repository, "Project-A", status, "Fix", When)
        {
            WorkItemIds = ticketIds,
            CommitIds = commitIds,
        };

    /// <summary>Returns a source commit in the same named project.</summary>
    private static CommitRecord Commit(string sha, string message = "", string repo = "repo-a") =>
        new(sha, repo, "Project-A", When, message);

    [Fact]
    public void Five_known_ticket_fix_associations_are_explicit_and_not_falsely_verified()
    {
        var prs = Enumerable.Range(1, 5)
            .Select(n => Pr(n, [1000 + n], [new string((char)('a' + n), 40)]))
            .ToArray();

        var candidates = TicketFixResolver.Resolve("Project-A", prs, []);

        Assert.Equal(5, candidates.Count);
        Assert.Equal(new[] { "1001", "1002", "1003", "1004", "1005" },
            candidates.Select(candidate => candidate.TicketId).ToArray());
        Assert.All(candidates, candidate =>
        {
            Assert.Equal(FixEvidenceStatus.Explicit, candidate.Status);
            Assert.False(candidate.IsVerifiedFix);
            Assert.Contains("pr_work_item", candidate.EvidenceTypes);
            Assert.Contains("pr_commit", candidate.EvidenceTypes);
        });
    }

    [Fact]
    public void Several_fixes_to_one_ticket_are_preserved_without_duplicate_replays()
    {
        var pr = Pr(1, [100, 100], ["a" + new string('1', 39), "b" + new string('2', 39)]);
        var output = TicketFixResolver.Resolve("Project-A", [pr, pr], []);

        Assert.Equal(2, output.Count);
        Assert.Equal(2, output.Select(candidate => candidate.AssociationKey).Distinct().Count());
    }

    [Fact]
    public void Newer_commits_do_not_create_a_fix_when_no_explicit_reference_exists()
    {
        var candidates = TicketFixResolver.Resolve("Project-A", [], [
            Commit(new string('a', 40)),
            Commit(new string('b', 40)),
        ]);
        Assert.Empty(candidates);
    }

    [Fact]
    public void A_commit_message_mention_is_explicit_but_not_proof_of_a_fix()
    {
        var output = TicketFixResolver.Resolve("Project-A", [], [
            Commit(new string('a', 40), "Investigate AB#1234; may be unrelated"),
        ]);
        var link = Assert.Single(output);
        Assert.Equal("1234", link.TicketId);
        Assert.Equal(FixEvidenceStatus.Explicit, link.Status);
        Assert.False(link.IsVerifiedFix);
        Assert.Equal("commit_mention", Assert.Single(link.EvidenceTypes));
    }

    [Fact]
    public void A_revert_is_attributed_only_to_the_unambiguously_matching_sha()
    {
        var fixedSha = "a" + new string('1', 39);
        var revertedBy = "b" + new string('2', 39);
        var candidates = TicketFixResolver.Resolve("Project-A", [Pr(12, [101], [fixedSha])],
            [Commit(fixedSha), Commit(revertedBy, $"This reverts commit {fixedSha}")]);

        var fix = Assert.Single(candidates);
        Assert.Equal(new[] { revertedBy }, fix.RevertCommitShas);
        Assert.False(fix.IsVerifiedFix);
        Assert.Throws<InvalidOperationException>(() =>
            TicketFixResolver.ApplyReview(fix, FixEvidenceStatus.Verified, "approved-review-123"));
    }

    [Fact]
    public void A_short_ambiguous_sha_is_not_assigned_to_a_revert()
    {
        var sha1 = "abc1234" + new string('a', 33);
        var sha2 = "abc1234" + new string('b', 33);
        var revertedBy = new string('c', 40);
        var candidates = TicketFixResolver.Resolve("Project-A", [Pr(12, [101], [sha1, sha2])],
            [Commit(sha1), Commit(sha2), Commit(revertedBy, "This reverts commit abc1234")]);
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.Empty(candidate.RevertCommitShas));
    }

    [Fact]
    public void Source_identity_scope_prevents_cross_project_and_repo_mixing()
    {
        var expected = Pr(12, [101], [new string('a', 40)]);
        var otherProject = expected with { Project = "Another-Project" };
        var differentRepo = expected with { RepositoryId = "repo-b" };
        var candidates = TicketFixResolver.Resolve("Project-A", [expected, otherProject, differentRepo], []);
        Assert.Equal(2, candidates.Count);
        Assert.Equal(2, candidates.Select(link => link.AssociationKey).Distinct().Count());
    }

    [Fact]
    public void Inference_is_kept_separate_until_human_evidence_is_supplied()
    {
        var proposed = TicketFixResolver.Propose("Project-A", "100", "repo-a", "42", null);
        Assert.Equal(FixEvidenceStatus.Inferred, proposed.Status);
        Assert.False(proposed.IsVerifiedFix);
        var approved = TicketFixResolver.ApplyReview(proposed, FixEvidenceStatus.Verified, "review-record-15");
        Assert.True(approved.IsVerifiedFix);
        var disputed = TicketFixResolver.ApplyReview(proposed, FixEvidenceStatus.Disputed, "dispute-record-16");
        Assert.Equal(FixEvidenceStatus.Disputed, disputed.Status);
    }
}
