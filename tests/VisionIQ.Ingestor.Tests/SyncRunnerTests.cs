using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Checkpoints;
using VisionIQ.Ingestor.Sources;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Tests;

public sealed class SyncRunnerTests
{
    private static SyncRequest All() => new(SyncRequest.AllKinds, null);

    private static SyncRunner Runner(FakeAdoHandler handler, ICheckpointStore checkpoints, IRecordSink sink, SourceCapabilityRegistry? registry = null) =>
        new(DemoAdo.Client(handler), checkpoints, sink, registry ?? new SourceCapabilityRegistry());

    [Fact]
    public async Task The_first_run_reads_every_stream_and_reports_counts()
    {
        var sink = new MemorySink();
        var registry = new SourceCapabilityRegistry();

        var report = await Runner(DemoAdo.Server(), new InMemoryCheckpointStore(), sink, registry).RunAsync(All());

        Assert.True(report.AllSynced);
        Assert.Equal(2, sink.Count("work-items"));
        Assert.Equal(3, sink.Count("work-item-revisions"));
        Assert.Equal(2, sink.Count("pull-requests"));
        Assert.Equal(3, sink.Count("commits"));
        Assert.Equal(1, sink.Count("builds"));
        Assert.Equal(13, sink.Count("links"));
        Assert.Equal(new[] { 2, 2, 3, 1 }, report.Streams.Select(stream => stream.Records));
        Assert.True(registry.IsSupported(SourceKind.AzureDevOpsPipelines, "builds"));
    }

    [Fact]
    public async Task Merged_sync_report_preserves_the_execution_run_id()
    {
        var request = new SyncRequest(new HashSet<SyncKind> { SyncKind.WorkItems }, null, IncludeRevisions: false);
        var sourceReport = await Runner(DemoAdo.Server(), new InMemoryCheckpointStore(), new MemorySink()).RunAsync(request);

        var merged = SyncReport.Merge([sourceReport]);

        Assert.NotEqual(Guid.Empty, merged.RunId);
        Assert.Equal(request.RunId, merged.RunId);
        Assert.Equal(sourceReport.RunId, merged.RunId);
        Assert.Equal(sourceReport.Streams.Count, merged.Streams.Count);
    }

    [Fact]
    public async Task A_second_run_resumes_from_checkpoints_without_duplicates()
    {
        var checkpoints = new InMemoryCheckpointStore();
        await Runner(DemoAdo.Server(), checkpoints, new MemorySink()).RunAsync(All());
        var second = new MemorySink();

        var report = await Runner(DemoAdo.Server(), checkpoints, second).RunAsync(All());

        Assert.True(report.AllSynced);
        // Review R1-06: the demo fixture's still-open PR 78 is re-enriched and re-written on every
        // run by design (its LastActivity never changes while active, so the cursor cannot be relied
        // on to notice a new push/vote/comment); everything else is genuinely unchanged and absent.
        var secondItem = Assert.Single(second.Items);
        Assert.Equal("pull-requests", secondItem.Stream);
        Assert.Equal(78, Assert.IsType<PullRequestRecord>(secondItem.Record).Id);
        var saved = await checkpoints.GetAsync("contoso/ado", "VisionIQ Demo/work-items");
        Assert.Equal("2026-10-21T09:00:00.0000000Z|1240", saved!.Cursor);
    }

    [Fact]
    public async Task Work_item_batch_read_is_pinned_to_the_wiql_querys_own_snapshot_time()
    {
        // Review R1-01: the batch read must ask for field values "as of" the exact instant the WIQL
        // query itself was evaluated at, so a work item changed while that query was running cannot
        // silently push the checkpoint past items the query never saw.
        var handler = new FakeAdoHandler()
            .On(System.Net.Http.HttpMethod.Post, "/_apis/wit/wiql", _ => FakeAdoHandler.Ok(
                "{\"queryType\":\"flat\",\"asOf\":\"2026-10-28T09:00:00Z\",\"workItems\":[{\"id\":1234}]}"))
            .Json(System.Net.Http.HttpMethod.Post, "/_apis/wit/workitemsbatch", "workitems-batch.json");
        var sink = new MemorySink();

        await Runner(handler, new InMemoryCheckpointStore(), sink).RunAsync(
            new SyncRequest(new HashSet<SyncKind> { SyncKind.WorkItems }, null, IncludeRevisions: false));

        var batchBody = handler.Bodies[handler.Requests.FindIndex(r => r.Contains("workitemsbatch", StringComparison.Ordinal))];
        Assert.Contains("\"asOf\":\"2026-10-28T09:00:00.000Z\"", batchBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_work_item_newer_than_the_wiql_snapshot_never_advances_the_checkpoint_past_it()
    {
        // Review R1-01 defense-in-depth: even if Azure DevOps still returned a live ChangedDate beyond
        // what "asOf" was asked for, the checkpoint itself must never move past the query's own
        // snapshot, or a sibling item between the two times would be skipped by the next run.
        const string asOf = "2026-10-28T09:00:00Z"; // the WIQL snapshot
        const string pastSnapshot = "2026-10-28T09:05:00Z"; // this item's "live" ChangedDate, after asOf
        var handler = new FakeAdoHandler()
            .On(System.Net.Http.HttpMethod.Post, "/_apis/wit/wiql", _ => FakeAdoHandler.Ok(
                "{\"queryType\":\"flat\",\"asOf\":\"" + asOf + "\",\"workItems\":[{\"id\":1234}]}"))
            .On(System.Net.Http.HttpMethod.Post, "/_apis/wit/workitemsbatch", _ => FakeAdoHandler.Ok(
                "{\"count\":1,\"value\":[{\"id\":1234,\"rev\":7,\"fields\":{"
                + "\"System.WorkItemType\":\"Bug\",\"System.State\":\"Active\","
                + "\"System.Title\":\"Still being edited\",\"System.ChangedDate\":\"" + pastSnapshot + "\"}}]}"));
        var checkpoints = new InMemoryCheckpointStore();

        await Runner(handler, checkpoints, new MemorySink()).RunAsync(
            new SyncRequest(new HashSet<SyncKind> { SyncKind.WorkItems }, null, IncludeRevisions: false));

        var saved = await checkpoints.GetAsync("contoso/ado", DemoAdo.Project + "/work-items");
        Assert.NotNull(saved);
        Assert.True(TimeCursor.Parse(saved!.Cursor)!.At <= DateTimeOffset.Parse(asOf),
            $"checkpoint {saved.Cursor} must not move past the WIQL snapshot {asOf}");
    }

    [Fact]
    public async Task A_future_dated_commit_never_permanently_stops_commit_ingestion()
    {
        // Review R1-02: CommitterDate comes from the developer's own Git client clock, not a server
        // timestamp. One bad-clock commit must not push the checkpoint into the future, or every
        // later (correctly dated) commit would be filtered out as "already seen" forever.
        var now = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        var futureCommit = new string('9', 40);
        var handler = new FakeAdoHandler()
            .Json(System.Net.Http.HttpMethod.Get, "/_apis/git/repositories", "repositories.json")
            .On(System.Net.Http.HttpMethod.Get, "/repositories/repo-1/commits", _ => FakeAdoHandler.Ok(
                "{\"count\":1,\"value\":[{\"commitId\":\"" + futureCommit + "\",\"comment\":\"Clock skew\","
                + "\"commentTruncated\":false,\"author\":{\"date\":\"2030-01-01T00:00:00Z\"},"
                + "\"committer\":{\"date\":\"2030-01-01T00:00:00Z\"}}]}"))
            .Json(System.Net.Http.HttpMethod.Get, "/changes", "commit-changes.json");
        var checkpoints = new InMemoryCheckpointStore();
        var runner = new SyncRunner(DemoAdo.Client(handler), checkpoints, new MemorySink(),
            new SourceCapabilityRegistry(), new FixedTimeProvider(now));

        await runner.RunAsync(new SyncRequest(new HashSet<SyncKind> { SyncKind.Commits }, null));

        var saved = await checkpoints.GetAsync("contoso/ado", DemoAdo.Project + "/payments-service/commits");
        Assert.NotNull(saved);
        Assert.True(TimeCursor.Parse(saved!.Cursor)!.At <= now,
            $"checkpoint {saved.Cursor} must not move past the current time {now:O}");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task An_open_pull_request_is_re_checked_on_every_run_but_a_closed_one_is_not()
    {
        // Review R1-06: LastActivity (ClosedDate ?? CreatedDate) never changes while a PR stays
        // active, so a new push/vote/comment on an open PR used to never be picked up again after
        // its creation. PR 78 in the demo fixture is "active"; PR 77 is "completed".
        var checkpoints = new InMemoryCheckpointStore();
        var request = new SyncRequest(new HashSet<SyncKind> { SyncKind.PullRequests }, null);

        static IEnumerable<int> PrIds(MemorySink sink) =>
            sink.Items.Where(item => item.Stream == "pull-requests")
                .Select(item => ((PullRequestRecord)item.Record).Id);

        var first = new MemorySink();
        await Runner(DemoAdo.Server(), checkpoints, first).RunAsync(request);
        Assert.Equal(new[] { 77, 78 }, PrIds(first).Order());

        var second = new MemorySink();
        await Runner(DemoAdo.Server(), checkpoints, second).RunAsync(request);
        var secondIds = PrIds(second).ToList();
        Assert.Contains(78, secondIds); // still open: re-checked
        Assert.DoesNotContain(77, secondIds); // closed and unchanged: not re-checked

        var third = new MemorySink();
        await Runner(DemoAdo.Server(), checkpoints, third).RunAsync(request);
        Assert.Contains(78, PrIds(third));
    }

    [Fact]
    public async Task Multiple_projects_have_independent_scopes_keys_and_checkpoints()
    {
        var options = new VisionIQ.Ingestor.Ado.AdoOptions
        {
            OrganizationUrl = new Uri(DemoAdo.Org),
            Projects = ["VisionIQ Demo", "Second Demo"],
            PersonalAccessToken = "test-token-not-real",
            MaxRetries = 2,
        };
        var checkpoints = new InMemoryCheckpointStore();
        var sink = new MemorySink();
        var request = new SyncRequest(new HashSet<SyncKind> { SyncKind.WorkItems }, null, IncludeRevisions: false);

        var first = await new SyncRunner(DemoAdo.Client(DemoAdo.Server(), options), checkpoints, sink, new SourceCapabilityRegistry()).RunAsync(request);

        Assert.True(first.AllSynced);
        Assert.Equal(2, first.Streams.Count);
        Assert.Contains(first.Streams, stream => stream.Scope == "VisionIQ Demo/work-items" && stream.Records == 2);
        Assert.Contains(first.Streams, stream => stream.Scope == "Second Demo/work-items" && stream.Records == 2);
        Assert.Equal(4, sink.Count("work-items"));
        Assert.Equal(4, sink.Items.Where(item => item.Stream == "work-items").Select(item => item.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.NotNull(await checkpoints.GetAsync("contoso/ado", "VisionIQ Demo/work-items"));
        Assert.NotNull(await checkpoints.GetAsync("contoso/ado", "Second Demo/work-items"));

        var repeat = await new SyncRunner(DemoAdo.Client(DemoAdo.Server(), options), checkpoints, sink, new SourceCapabilityRegistry()).RunAsync(request);

        Assert.True(repeat.AllSynced);
        Assert.All(repeat.Streams, stream => Assert.Equal(0, stream.Records));
        Assert.Equal(4, sink.Count("work-items"));
    }

    [Fact]
    public async Task A_source_without_access_is_not_synced_while_the_others_continue()
    {
        var registry = new SourceCapabilityRegistry();

        var report = await Runner(DemoAdo.Server(pipelinesDenied: true), new InMemoryCheckpointStore(), new MemorySink(), registry).RunAsync(All());

        var builds = report.Streams.Single(stream => stream.Stream == SyncKind.Builds);
        Assert.False(report.AllSynced);
        Assert.Equal(StreamStatus.NotSynced, builds.Status);
        Assert.Contains("Access denied (401)", builds.Reason, StringComparison.Ordinal);
        Assert.True(report.Streams.Where(stream => stream.Stream != SyncKind.Builds).All(stream => stream.Status == StreamStatus.Synced));
        Assert.False(registry.IsSupported(SourceKind.AzureDevOpsPipelines, "builds"));
    }

    [Fact]
    public async Task A_configured_repository_that_does_not_exist_is_not_synced()
    {
        var client = DemoAdo.Client(DemoAdo.Server(), DemoAdo.Options(repositories: ["payments-service", "missing-repo"]));
        var runner = new SyncRunner(client, new InMemoryCheckpointStore(), new MemorySink(), new SourceCapabilityRegistry());

        var report = await runner.RunAsync(new SyncRequest(new HashSet<SyncKind> { SyncKind.Commits }, null));

        Assert.Equal(StreamStatus.NotSynced, report.Streams.Single(stream => stream.Scope == "VisionIQ Demo/missing-repo").Status);
        Assert.Equal(StreamStatus.Synced, report.Streams.Single(stream => stream.Scope == "VisionIQ Demo/payments-service/commits").Status);
    }

    [Fact]
    public async Task A_broken_checkpoint_store_stops_the_stream_with_a_clear_reason()
    {
        var report = await Runner(DemoAdo.Server(), new BrokenCheckpointStore(), new MemorySink())
            .RunAsync(new SyncRequest(new HashSet<SyncKind> { SyncKind.WorkItems }, null));

        var result = Assert.Single(report.Streams);
        Assert.Equal(StreamStatus.NotSynced, result.Status);
        Assert.Equal(0, result.Records);
        Assert.Contains("db.manage migrate", result.Reason, StringComparison.Ordinal);
    }


    [Fact]
    public async Task An_omitted_older_work_item_is_partial_and_does_not_advance_checkpoint()
    {
        var missing = new FakeAdoHandler()
            .Json(HttpMethod.Post, "/_apis/wit/wiql", "wiql.json")
            .Json(HttpMethod.Post, "/_apis/wit/workitemsbatch", "workitems-batch-omitted-id.json")
            .Json(HttpMethod.Get, "/workItems/1240/revisions", "workitem-1240-revisions.json");
        var checkpoints = new InMemoryCheckpointStore();
        var request = new SyncRequest(new HashSet<SyncKind> { SyncKind.WorkItems }, null);
        var report = await Runner(missing, checkpoints, new MemorySink()).RunAsync(request);
        var outcome = Assert.Single(report.Streams);
        Assert.Equal(StreamStatus.Partial, outcome.Status);
        Assert.Contains("1234", outcome.Reason, StringComparison.Ordinal);
        Assert.Null(await checkpoints.GetAsync("contoso/ado", "VisionIQ Demo/work-items"));

        // When the missing source becomes available, a replay must find it.
        var resumed = new MemorySink();
        var retry = await Runner(DemoAdo.Server(), checkpoints, resumed).RunAsync(request);
        Assert.True(retry.AllSynced);
        Assert.Equal(2, resumed.Count("work-items"));
        Assert.NotNull(await checkpoints.GetAsync("contoso/ado", "VisionIQ Demo/work-items"));
    }

    private sealed class BrokenCheckpointStore : ICheckpointStore
    {
        public Task<SyncCheckpoint?> GetAsync(string sourceId, string scope, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("relation \"evidence.connector_checkpoints\" does not exist");

        public Task SaveAsync(SyncCheckpoint checkpoint, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("not reachable");
    }
}
