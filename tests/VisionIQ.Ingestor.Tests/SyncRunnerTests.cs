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
    public async Task A_second_run_resumes_from_checkpoints_without_duplicates()
    {
        var checkpoints = new InMemoryCheckpointStore();
        await Runner(DemoAdo.Server(), checkpoints, new MemorySink()).RunAsync(All());
        var second = new MemorySink();

        var report = await Runner(DemoAdo.Server(), checkpoints, second).RunAsync(All());

        Assert.True(report.AllSynced);
        Assert.Empty(second.Items);
        var saved = await checkpoints.GetAsync("contoso/ado", "VisionIQ Demo/work-items");
        Assert.Equal("2026-10-21T09:00:00.0000000Z|1240", saved!.Cursor);
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
