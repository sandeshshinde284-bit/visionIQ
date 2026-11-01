using VisionIQ.Api.Features.Evidence;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Checkpoints;
using VisionIQ.Ingestor.Cli;
using VisionIQ.Ingestor.Sources;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Tests;

/// <summary>G-1 (LRN-05): failed items go to a quarantine list instead of stopping the run.</summary>
public sealed class SyncQuarantineTests
{
    private const string Item = """{"id":1,"rev":1,"fields":{"System.WorkItemType":"Bug","System.State":"New","System.Title":"t","System.ChangedDate":"2026-01-01T00:00:00Z","System.CreatedDate":"2026-01-01T00:00:00Z"},"relations":[]}""";
    private const string Item2 = """{"id":2,"rev":1,"fields":{"System.WorkItemType":"Bug","System.State":"New","System.Title":"t","System.ChangedDate":"2026-01-02T00:00:00Z","System.CreatedDate":"2026-01-01T00:00:00Z"},"relations":[]}""";

    /// <summary>WIQL returns items 1 and 2; the batch returns only the given items (2 omitted = unreadable).</summary>
    private static FakeAdoHandler Server(params string[] items) => new FakeAdoHandler()
        .On(HttpMethod.Post, "/_apis/wit/wiql", _ => FakeAdoHandler.Ok("""{"workItems":[{"id":1},{"id":2}]}"""))
        .On(HttpMethod.Post, "/_apis/wit/workitemsbatch", _ => FakeAdoHandler.Ok("{\"value\":[" + string.Join(",", items) + "]}"));

    private static readonly SyncRequest WorkItems = new(new HashSet<SyncKind> { SyncKind.WorkItems }, null, IncludeRevisions: false);

    [Fact]
    public async Task A_failed_item_is_listed_and_the_run_continues()
    {
        var quarantine = new InMemorySyncQuarantine();
        var checkpoints = new InMemoryCheckpointStore();
        var sink = new MemorySink();

        var report = await new SyncRunner(DemoAdo.Client(Server(Item)), checkpoints, sink, new SourceCapabilityRegistry(), quarantine: quarantine).RunAsync(WorkItems);

        var stream = Assert.Single(report.Streams);
        Assert.Equal((StreamStatus.Partial, 1, 1), (stream.Status, stream.Records, stream.Quarantined));
        var (item, attempts, resolved) = Assert.Single(quarantine.Entries);
        Assert.Equal(("work_item", "2", "not_readable", 1), (item.ItemKind, item.NativeId, item.ReasonCode, attempts));
        Assert.Null(resolved);
        Assert.Contains("missing", item.Reason, StringComparison.Ordinal);
        Assert.Single(sink.Items, i => i.Stream == "sync-quarantine");
        Assert.Null(await checkpoints.GetAsync("contoso/ado", "VisionIQ Demo/work-items")); // retried next run
        var audit = Assert.Single(SourceSyncAuditProjector.Project(report, VisionIQ.Api.Features.Backfill.SourceSyncMode.Delta));
        Assert.Equal((2, 1, 1), (audit.RecordsReceived, audit.RecordsStored, audit.RecordsQuarantined));
    }

    [Fact]
    public async Task A_repeat_failure_counts_an_attempt_and_a_later_success_resolves_it()
    {
        var quarantine = new InMemorySyncQuarantine();

        await new SyncRunner(DemoAdo.Client(Server(Item)), new InMemoryCheckpointStore(), new MemorySink(), new SourceCapabilityRegistry(), quarantine: quarantine).RunAsync(WorkItems);
        await new SyncRunner(DemoAdo.Client(Server(Item)), new InMemoryCheckpointStore(), new MemorySink(), new SourceCapabilityRegistry(), quarantine: quarantine).RunAsync(WorkItems);
        Assert.Equal(2, Assert.Single(quarantine.Entries).Attempts);

        var report = await new SyncRunner(DemoAdo.Client(Server(Item, Item2)), new InMemoryCheckpointStore(), new MemorySink(), new SourceCapabilityRegistry(), quarantine: quarantine).RunAsync(WorkItems);

        Assert.Equal(StreamStatus.Synced, Assert.Single(report.Streams).Status);
        Assert.NotNull(Assert.Single(quarantine.Entries).Resolved);
        Assert.Empty(await quarantine.OpenAsync("contoso/ado", "VisionIQ Demo/work-items", "work_item"));
    }

    private sealed class NoArchive : IRawEvidenceArchive
    {
        public Task<string> StoreAsync(string itemKey, string revisionId, byte[] rawJson, string contentSha256, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("quarantine records are not evidence");
    }

    private sealed class NoStore : IVersionedEvidenceStore
    {
        public Task<EvidenceImportResult> SaveAsync(ArchivedEvidence evidence, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("quarantine records are not evidence");
    }

    [Fact]
    public async Task The_evidence_sink_skips_quarantine_records()
    {
        var sink = new CanonicalEvidenceSink(new EvidenceIngestionService(new NoArchive(), new NoStore()), new Uri("https://dev.azure.com/contoso"), "contoso");

        await sink.WriteAsync("sync-quarantine", "k", new QuarantinedItem("contoso/ado", "s", "work_item", "2", "not_readable", "missing", DateTimeOffset.UtcNow));
    }
}
