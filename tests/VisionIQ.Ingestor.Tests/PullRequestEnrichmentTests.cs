using System.Net;
using System.Text.Json;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Checkpoints;
using VisionIQ.Ingestor.Sources;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Tests;

/// <summary>
/// Master design §8.12: PR evidence carries the source SHA, every iteration, the changed files, review rationale,
/// a patch fingerprint and branch-policy evidence; sync and webhook enrich identically; gaps stay visible.
/// </summary>
public sealed class PullRequestEnrichmentTests
{
    private const string Pr = """
        {"pullRequestId":42,"status":"abandoned","title":"Retry-only patch","creationDate":"2026-03-01T09:00:00Z",
         "closedDate":"2026-03-03T09:00:00Z","lastMergeSourceCommit":{"commitId":"1111111111111111111111111111111111111111"},
         "lastMergeTargetCommit":{"commitId":"2222222222222222222222222222222222222222"},
         "repository":{"id":"repo-1","name":"shop","project":{"id":"proj-guid","name":"VisionIQ Demo"}},"reviewers":[]}
        """;

    private sealed class Server
    {
        public bool ThreadsForbidden { get; init; }

        public List<string> Queries { get; } = [];

        public FakeAdoHandler Handler()
        {
            var handler = new FakeAdoHandler()
                .On(HttpMethod.Get, "/_apis/git/repositories/repo-1", _ => FakeAdoHandler.Ok("""{"id":"repo-1","name":"shop","project":{"id":"proj-guid","name":"VisionIQ Demo"}}"""))
                .On(HttpMethod.Get, "/_apis/git/repositories/repo-1/pullrequests/42", _ => FakeAdoHandler.Ok(Pr))
                .On(HttpMethod.Get, "/pullRequests/42/workitems", _ => FakeAdoHandler.Ok("""{"value":[{"id":"1234"}]}"""))
                .On(HttpMethod.Get, "/pullRequests/42/commits", _ => FakeAdoHandler.Ok("""{"value":[{"commitId":"1111111111111111111111111111111111111111"}]}"""))
                .On(HttpMethod.Get, "/pullRequests/42/iterations", _ => FakeAdoHandler.Ok("""
                    {"value":[{"id":2,"sourceRefCommit":{"commitId":"1111111111111111111111111111111111111111"},"targetRefCommit":{"commitId":"2222222222222222222222222222222222222222"},"createdDate":"2026-03-02T09:00:00Z"},
                              {"id":1,"sourceRefCommit":{"commitId":"0000000000000000000000000000000000000000"},"createdDate":"2026-03-01T09:00:00Z"}]}
                    """))
                .On(HttpMethod.Get, "/pullRequests/42/iterations/2/changes", request =>
                {
                    Queries.Add(request.RequestUri!.Query);
                    return request.RequestUri.Query.Contains("%24skip=0", StringComparison.Ordinal)
                        ? FakeAdoHandler.Ok("""{"changeEntries":[{"changeType":"edit","item":{"path":"/src/Retry.cs"}}],"nextSkip":1}""")
                        : FakeAdoHandler.Ok("""{"changeEntries":[{"changeType":"add","item":{"path":"/tests/RetryTests.cs"}}]}""");
                })
                .On(HttpMethod.Get, "/_apis/policy/evaluations", request =>
                {
                    Queries.Add(request.RequestUri!.Query);
                    return FakeAdoHandler.Ok("""
                        {"value":[{"status":"rejected","configuration":{"isBlocking":true,"type":{"displayName":"Minimum number of reviewers"}}},
                                  {"status":"approved","configuration":{"isBlocking":true,"type":{"displayName":"Build"}}}]}
                        """);
                });
            return ThreadsForbidden
                ? handler.On(HttpMethod.Get, "/pullRequests/42/threads", _ => FakeAdoHandler.Status(HttpStatusCode.Forbidden))
                : handler.On(HttpMethod.Get, "/pullRequests/42/threads", _ => FakeAdoHandler.Ok("""
                    {"value":[{"id":7,"status":"wontFix","threadContext":{"filePath":"/src/Retry.cs"},"comments":[
                                {"id":1,"author":{"id":"rev-1","displayName":"Ana","uniqueName":"ana@contoso.com"},"publishedDate":"2026-03-02T10:00:00Z","content":"Retrying without an idempotency key double-charges.","commentType":"text"},
                                {"id":2,"content":"Ana voted -10","commentType":"system"}]},
                              {"id":8,"isDeleted":true,"comments":[{"id":1,"content":"deleted","commentType":"text"}]},
                              {"id":9,"comments":[{"id":1,"content":"Policy status updated","commentType":"system"}]}]}
                    """));
        }
    }

    private static async Task<PullRequestRecord> Read(Server server) =>
        (await new AdoRepositorySource(DemoAdo.Client(server.Handler())).GetPullRequestAsync("VisionIQ Demo", "repo-1", 42))!.Value.PullRequest;

    [Fact]
    public async Task An_abandoned_pr_keeps_its_source_sha_iterations_files_rationale_fingerprint_and_policies()
    {
        var server = new Server();
        var pr = await Read(server);

        Assert.Equal(("1111111111111111111111111111111111111111", "2222222222222222222222222222222222222222"), (pr.SourceCommitId, pr.TargetCommitId));
        Assert.Equal(new[] { 1, 2 }, pr.Iterations!.Select(i => i.Id));
        Assert.Equal(new[] { new PullRequestFileChange("/src/Retry.cs", "edit"), new PullRequestFileChange("/tests/RetryTests.cs", "add") }, pr.ChangedFiles);
        var thread = Assert.Single(pr.ReviewThreads!); // deleted and system-only threads skipped
        Assert.Equal(("wontFix", "/src/Retry.cs"), (thread.Status, thread.FilePath));
        var comment = Assert.Single(thread.Comments); // system message skipped
        Assert.Equal(("rev-1", "Retrying without an idempotency key double-charges."), (comment.AuthorId, comment.Content));
        Assert.Matches("^[0-9a-f]{64}$", pr.PatchFingerprint!);
        Assert.Equal(new[] { ("Build", "approved", true), ("Minimum number of reviewers", "rejected", true) },
            pr.PolicyEvaluations!.Select(p => (p.Policy, p.Status, p.IsBlocking)));
        Assert.Empty(pr.EnrichmentGaps);
        Assert.Equal("abandoned", pr.Lifecycle);
        Assert.Contains(server.Queries, q => q.Contains("api-version=7.1-preview.1", StringComparison.Ordinal) &&
            q.Contains(Uri.EscapeDataString("vstfs:///CodeReview/CodeReviewId/proj-guid/42"), StringComparison.Ordinal));
        Assert.Contains(server.Queries, q => q.Contains("%24compareTo=0", StringComparison.Ordinal)); // cumulative vs. base
        Assert.DoesNotContain("ana@contoso.com", JsonSerializer.Serialize(pr), StringComparison.Ordinal); // identities by ID only
    }

    [Fact]
    public void The_patch_fingerprint_is_stable_and_order_independent()
    {
        var a = new PullRequestRecord(1, "r", "n", "p", "active", "t", DateTimeOffset.UnixEpoch)
        {
            ChangedFiles = [new("/a.cs", "edit"), new("/b.cs", "add")],
        };
        var b = a with { ChangedFiles = [new("/b.cs", "add"), new("/a.cs", "edit")] };
        var c = a with { ChangedFiles = [new("/a.cs", "edit")] };

        Assert.Equal(a.PatchFingerprint, b.PatchFingerprint);
        Assert.NotEqual(a.PatchFingerprint, c.PatchFingerprint);
        Assert.Null((a with { ChangedFiles = null }).PatchFingerprint); // unavailable, not "no changes"
    }

    [Fact]
    public async Task An_unreadable_part_is_recorded_as_unavailable_not_empty()
    {
        var pr = await Read(new Server { ThreadsForbidden = true });

        Assert.Null(pr.ReviewThreads);
        Assert.Equal(new[] { "review threads: HTTP 403" }, pr.EnrichmentGaps);
        Assert.NotNull(pr.ChangedFiles); // the rest is still captured
    }

    [Fact]
    public async Task Sync_and_webhook_store_identical_pr_content()
    {
        var server = new Server();
        var webhook = await Read(server);

        var sink = new MemorySink();
        var handler = server.Handler()
            .On(HttpMethod.Get, "/_apis/git/repositories", _ => FakeAdoHandler.Ok("""{"value":[{"id":"repo-1","name":"shop","project":{"id":"proj-guid","name":"VisionIQ Demo"}}]}"""))
            .On(HttpMethod.Get, "/_apis/git/repositories/repo-1/pullrequests", _ => FakeAdoHandler.Ok("{\"value\":[" + Pr + "]}"));
        await new SyncRunner(DemoAdo.Client(handler), new InMemoryCheckpointStore(), sink, new SourceCapabilityRegistry())
            .RunAsync(new SyncRequest(new HashSet<SyncKind> { SyncKind.PullRequests }, null));
        var synced = (PullRequestRecord)Assert.Single(sink.Items, i => i.Stream == "pull-requests").Record;

        // Byte-identical content means one revision, never two alternating ones.
        Assert.Equal(JsonSerializer.Serialize(webhook), JsonSerializer.Serialize(synced));
    }
}
