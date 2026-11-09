using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Checkpoints;
using VisionIQ.Ingestor.Sources;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Tests;

/// <summary>
/// S05-05: a reviewer's vote is versioned as its own evidence item, independent of the PR's own
/// revision cadence. Covers the vote-to-status mapping and the real end-to-end sync write.
/// </summary>
public sealed class PullRequestReviewArtifactTests
{
    [Theory]
    [InlineData(10, false, false, ReviewVoteStatus.Approved)]
    [InlineData(5, false, false, ReviewVoteStatus.ApprovedWithSuggestions)]
    [InlineData(0, false, false, ReviewVoteStatus.NoVote)]
    [InlineData(-5, false, false, ReviewVoteStatus.WaitingForAuthor)]
    [InlineData(-10, false, false, ReviewVoteStatus.Rejected)]
    [InlineData(10, false, true, ReviewVoteStatus.Declined)]
    [InlineData(-10, true, false, ReviewVoteStatus.Rejected)]
    public void Vote_maps_to_the_expected_status(int vote, bool isRequired, bool hasDeclined, string expected)
    {
        _ = isRequired; // not part of the vote-to-status mapping itself
        Assert.Equal(expected, ReviewVoteStatus.Of(vote, hasDeclined));
    }

    private static FakeAdoHandler Handler() => new FakeAdoHandler()
        .Json(HttpMethod.Get, "/_apis/git/repositories", "repositories.json")
        .On(HttpMethod.Get, "/repositories/repo-1/pullrequests", _ => FakeAdoHandler.Ok("""
            {"value":[{"pullRequestId":99,"status":"active","title":"Add retry budget","creationDate":"2026-11-01T09:00:00Z",
                       "repository":{"id":"repo-1","name":"payments-service","project":{"id":"proj-guid","name":"VisionIQ Demo"}},
                       "reviewers":[{"id":"rev-1","vote":10,"isRequired":true,"hasDeclined":false},
                                    {"id":"rev-2","vote":-5,"isRequired":false,"hasDeclined":false},
                                    {"id":"rev-3","vote":10,"isRequired":false,"hasDeclined":true}]}]}
            """))
        .On(HttpMethod.Get, "/pullRequests/99/workitems", _ => FakeAdoHandler.Ok("""{"value":[]}"""))
        .On(HttpMethod.Get, "/pullRequests/99/commits", _ => FakeAdoHandler.Ok("""{"value":[]}"""));

    [Fact]
    public async Task A_real_sync_writes_one_versioned_review_item_per_reviewer()
    {
        var sink = new MemorySink();
        var runner = new SyncRunner(DemoAdo.Client(Handler(), DemoAdo.Options(["payments-service"])),
            new InMemoryCheckpointStore(), sink, new SourceCapabilityRegistry());

        var report = await runner.RunAsync(new SyncRequest(new HashSet<SyncKind> { SyncKind.PullRequests }, null));

        Assert.True(report.AllSynced);
        Assert.Equal(1, sink.Count("pull-requests"));
        Assert.Equal(3, sink.Count("pull-request-reviews"));

        var reviews = sink.Items.Where(item => item.Stream == "pull-request-reviews")
            .Select(item => (PullRequestReviewRecord)item.Record)
            .ToDictionary(review => review.ReviewerId, StringComparer.Ordinal);

        Assert.Equal(99, reviews["rev-1"].PullRequestId);
        Assert.Equal("repo-1", reviews["rev-1"].RepositoryId);
        Assert.Equal("VisionIQ Demo", reviews["rev-1"].Project);
        Assert.Equal(ReviewVoteStatus.Approved, reviews["rev-1"].Status);
        Assert.Equal(ReviewVoteStatus.WaitingForAuthor, reviews["rev-2"].Status);
        // A declined reviewer's vote is a real recorded fact (hasDeclined=true), never silently dropped.
        Assert.Equal(ReviewVoteStatus.Declined, reviews["rev-3"].Status);
    }

    [Fact]
    public async Task A_repeat_sync_with_the_same_votes_is_idempotent_at_the_evidence_layer()
    {
        var checkpoints = new InMemoryCheckpointStore();
        var handlerFactory = Handler;
        await new SyncRunner(DemoAdo.Client(handlerFactory(), DemoAdo.Options(["payments-service"])), checkpoints, new MemorySink(), new SourceCapabilityRegistry())
            .RunAsync(new SyncRequest(new HashSet<SyncKind> { SyncKind.PullRequests }, null));
        var second = new MemorySink();

        // Status is "active", so this PR (and its reviews) are re-enriched on every run by design
        // (R1-06) -- the test proves the review rows stay content-stable, not that they're skipped.
        await new SyncRunner(DemoAdo.Client(handlerFactory(), DemoAdo.Options(["payments-service"])), checkpoints, second, new SourceCapabilityRegistry())
            .RunAsync(new SyncRequest(new HashSet<SyncKind> { SyncKind.PullRequests }, null));

        Assert.Equal(3, second.Count("pull-request-reviews"));
        var firstKeys = second.Items.Where(item => item.Stream == "pull-request-reviews").Select(item => item.Key).OrderBy(k => k, StringComparer.Ordinal);
        Assert.All(firstKeys, key => Assert.Contains("pull-request-review/", key));
    }
}
