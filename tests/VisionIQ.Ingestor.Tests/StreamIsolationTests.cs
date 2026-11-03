using VisionIQ.Ingestor.Checkpoints;
using VisionIQ.Ingestor.Sources;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Tests;

/// <summary>B-4: an unexpected (non-source) failure in one stream must not stop the others.</summary>
public sealed class StreamIsolationTests
{
    /// <summary>Simulates a database/archive failure while writing one stream.</summary>
    private sealed class FailingSink(string failingStream) : IRecordSink
    {
        public MemorySink Inner { get; } = new();

        public Task WriteAsync(string stream, string key, object record, CancellationToken cancellationToken = default) =>
            stream == failingStream
                ? throw new InvalidOperationException("simulated database outage")
                : Inner.WriteAsync(stream, key, record, cancellationToken);
    }

    [Fact]
    public async Task A_sink_failure_in_one_stream_is_reported_and_the_others_still_sync()
    {
        var sink = new FailingSink("pull-requests");
        var checkpoints = new InMemoryCheckpointStore();
        var runner = new SyncRunner(DemoAdo.Client(DemoAdo.Server()), checkpoints, sink, new SourceCapabilityRegistry());

        var report = await runner.RunAsync(new SyncRequest(SyncRequest.AllKinds, null));

        var pullRequests = Assert.Single(report.Streams, stream => stream.Stream == SyncKind.PullRequests);
        Assert.NotEqual(StreamStatus.Synced, pullRequests.Status);
        Assert.Contains("simulated database outage", pullRequests.Reason, StringComparison.Ordinal);
        Assert.All(report.Streams.Where(stream => stream.Stream != SyncKind.PullRequests), stream => Assert.Equal(StreamStatus.Synced, stream.Status));
        Assert.Equal(3, sink.Inner.Count("commits"));
        Assert.Equal(1, sink.Inner.Count("builds"));
        Assert.False(report.AllSynced);
    }

    [Fact]
    public async Task A_failed_stream_does_not_advance_its_checkpoint()
    {
        var checkpoints = new InMemoryCheckpointStore();
        var runner = new SyncRunner(DemoAdo.Client(DemoAdo.Server()), checkpoints, new FailingSink("work-items"), new SourceCapabilityRegistry());

        await runner.RunAsync(new SyncRequest(SyncRequest.AllKinds, null));

        Assert.Null(await checkpoints.GetAsync("contoso/ado", "VisionIQ Demo/work-items"));
    }

    /// <summary>Injects one failure after the first work item was durably written.</summary>
    private sealed class FailOnceAfterFirstWorkItemSink : IRecordSink
    {
        private bool failed;
        public MemorySink Inner { get; } = new();

        public Task WriteAsync(string stream, string key, object record, CancellationToken cancellationToken = default)
        {
            if (!failed && stream == "work-items" && Inner.Count("work-items") == 1)
            {
                failed = true;
                throw new InvalidOperationException("injected mid-stream sink failure");
            }

            return Inner.WriteAsync(stream, key, record, cancellationToken);
        }
    }

    [Fact]
    public async Task Mid_stream_sink_failure_does_not_advance_checkpoint_and_retry_recovers_without_duplicate_keys()
    {
        var sink = new FailOnceAfterFirstWorkItemSink();
        var checkpoints = new InMemoryCheckpointStore();
        var request = new SyncRequest(new HashSet<SyncKind> { SyncKind.WorkItems }, null, IncludeRevisions: false);

        var interrupted = await new SyncRunner(DemoAdo.Client(DemoAdo.Server()), checkpoints, sink, new SourceCapabilityRegistry())
            .RunAsync(request);

        Assert.False(interrupted.AllSynced);
        var first = Assert.Single(interrupted.Streams);
        Assert.NotEqual(StreamStatus.Synced, first.Status);
        Assert.Contains("injected mid-stream sink failure", first.Reason, StringComparison.Ordinal);
        Assert.Equal(1, sink.Inner.Count("work-items"));
        Assert.Null(await checkpoints.GetAsync("contoso/ado", "VisionIQ Demo/work-items"));

        var recovered = await new SyncRunner(DemoAdo.Client(DemoAdo.Server()), checkpoints, sink, new SourceCapabilityRegistry())
            .RunAsync(request);

        Assert.True(recovered.AllSynced);
        Assert.Equal(2, Assert.Single(recovered.Streams).Records);
        Assert.Equal(2, sink.Inner.Count("work-items"));
        Assert.Equal(2, sink.Inner.Items.Where(item => item.Stream == "work-items").Select(item => item.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.NotNull(await checkpoints.GetAsync("contoso/ado", "VisionIQ Demo/work-items"));
    }

    [Fact]
    public async Task A_confluence_sink_failure_is_reported_instead_of_crashing()
    {
        var sink = new FailingSink("confluence-acl");
        var runner = new VisionIQ.Ingestor.Confluence.ConfluenceSyncRunner(
            DemoConfluence.Source(DemoConfluence.CloudServer(), DemoConfluence.CloudOptions()),
            new InMemoryCheckpointStore(), sink, new SourceCapabilityRegistry());

        var report = await runner.RunAsync(new SyncRequest(SyncRequest.AllKinds, null));

        Assert.All(report.Streams, stream => Assert.NotEqual(StreamStatus.Synced, stream.Status));
        Assert.Contains(report.Streams, stream => stream.Reason is { } reason && reason.Contains("simulated database outage", StringComparison.Ordinal));
    }
}
