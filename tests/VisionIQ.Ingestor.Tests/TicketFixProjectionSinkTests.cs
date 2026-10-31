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
    public async Task Work_item_artifact_relations_are_persisted_without_guessing_repository_scope()
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
            Assert.Null(link.RepositoryId);
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
    public async Task A_ticket_mentioned_in_a_commit_is_only_an_explicit_reference()
    {
        var links = new RecordingLinkStore();
        var sink = new TicketFixProjectionSink(new RecordingEvidenceSink(), links, Organization);
        var commit = new CommitRecord(new string('b', 40), "repo-id", "Project-A",
            DateTimeOffset.UtcNow, "Investigation AB#321") { RepositoryName = "repo" };
        await sink.WriteAsync("commits", "source/commit", commit);

        var claim = Assert.Single(links.Claims);
        Assert.Equal("321", claim.TicketId);
        Assert.Null(claim.PullRequestId);
        Assert.Equal(TicketFixSourceClassification.Explicit, claim.Classification);
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

        /// <summary>Records a source-declared candidate in memory.</summary>
        public Task<string> UpsertSourceAsync(TicketFixClaim claim,
            CancellationToken cancellationToken = default)
        {
            Claims.Add(claim);
            return Task.FromResult(claim.AssociationKey());
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
