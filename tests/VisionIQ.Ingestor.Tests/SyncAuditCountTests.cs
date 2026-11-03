using VisionIQ.Api.Features.Backfill;
using VisionIQ.Ingestor.Checkpoints;
using VisionIQ.Ingestor.Cli;
using VisionIQ.Ingestor.Sources;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Tests;

/// <summary>M-12: the audit row distinguishes items received from items stored.</summary>
public sealed class SyncAuditCountTests
{
    private const string Item = """{"id":1,"rev":1,"fields":{"System.WorkItemType":"Bug","System.State":"New","System.Title":"t","System.ChangedDate":"2026-01-01T00:00:00Z","System.CreatedDate":"2026-01-01T00:00:00Z"},"relations":[]}""";

    [Fact]
    public async Task An_omitted_work_item_is_counted_as_received_but_not_stored()
    {
        var handler = new FakeAdoHandler()
            .On(HttpMethod.Post, "/_apis/wit/wiql", _ => FakeAdoHandler.Ok("""{"workItems":[{"id":1},{"id":2}]}"""))
            .On(HttpMethod.Post, "/_apis/wit/workitemsbatch", _ => FakeAdoHandler.Ok("{\"value\":[" + Item + "]}"));
        var runner = new SyncRunner(DemoAdo.Client(handler), new InMemoryCheckpointStore(), new MemorySink(), new SourceCapabilityRegistry());

        var report = await runner.RunAsync(new SyncRequest(new HashSet<SyncKind> { SyncKind.WorkItems }, null, IncludeRevisions: false));
        var audit = Assert.Single(SourceSyncAuditProjector.Project(report, SourceSyncMode.Delta));

        Assert.Equal(2, audit.RecordsReceived);
        Assert.Equal(1, audit.RecordsStored);
        Assert.Equal(SourceSyncState.Partial, audit.Status);
    }
}

/// <summary>N-18: the audit source ID is a stored key; its bytes must never change.</summary>
public sealed class StableAuditIdTests
{
    [Fact]
    public void Audit_source_id_matches_the_independent_reference() =>
        Assert.Equal("sha256:d9c3102de28bd18b6815a0eb651deb2df2b96a73f58947618d0dbe2160f7e5f9",
            VisionIQ.Ingestor.Cli.SourceSyncAuditProjector.StableSourceId("contoso", "WorkItems", "Payments"));
}
