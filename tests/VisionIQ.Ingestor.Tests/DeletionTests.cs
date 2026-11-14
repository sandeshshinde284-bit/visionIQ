using System.Text.Json;
using VisionIQ.Api.Features.Backfill;
using VisionIQ.Api.Features.Evidence;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Checkpoints;
using VisionIQ.Ingestor.Cli;
using VisionIQ.Ingestor.Sources;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Tests;

/// <summary>N-2: work items deleted in ADO are found and hidden; deletion events are confirmed, never trusted.</summary>
public sealed class DeletionTests
{
    private const string RecycleBin = """{"count":2,"value":[{"id":1234,"url":"x"},{"id":5,"url":"y"}]}""";

    [Fact]
    public async Task The_recycle_bin_lists_deleted_work_item_ids()
    {
        var handler = new FakeAdoHandler().On(HttpMethod.Get, "/_apis/wit/recyclebin", _ => FakeAdoHandler.Ok(RecycleBin));

        Assert.Equal(new[] { 1234, 5 }, await new AdoWorkItemSource(DemoAdo.Client(handler)).GetRecycleBinIdsAsync(DemoAdo.Project));
    }

    [Fact]
    public async Task Each_sync_records_deletions_from_the_recycle_bin()
    {
        var sink = new MemorySink();
        var server = DemoAdo.Server().On(HttpMethod.Get, "/_apis/wit/recyclebin", _ => FakeAdoHandler.Ok(RecycleBin));

        await new SyncRunner(DemoAdo.Client(server), new InMemoryCheckpointStore(), sink, new SourceCapabilityRegistry())
            .RunAsync(new SyncRequest(new HashSet<SyncKind> { SyncKind.WorkItems }, null, IncludeRevisions: false));

        Assert.Equal(new[] { 1234, 5 }, sink.Items.Where(i => i.Stream == "work-item-deletions").Select(i => ((WorkItemDeletion)i.Record).Id));
    }

    [Fact]
    public async Task An_unreadable_recycle_bin_is_noted_and_does_not_fail_the_sync()
    {
        var report = await new SyncRunner(DemoAdo.Client(DemoAdo.Server()), new InMemoryCheckpointStore(), new MemorySink(), new SourceCapabilityRegistry())
            .RunAsync(new SyncRequest(new HashSet<SyncKind> { SyncKind.WorkItems }, null, IncludeRevisions: false));

        var stream = Assert.Single(report.Streams);
        Assert.Equal(StreamStatus.Synced, stream.Status);
        Assert.Contains(stream.Notes, note => note.Contains("Recycle Bin", StringComparison.Ordinal));
    }

    internal sealed class Tombstones : IEvidenceTombstones
    {
        public List<(string Key, string Reason)> Calls { get; } = [];

        /// <summary>Review R1-03 tests: item keys to return as "stored under a different project".</summary>
        public List<string> OtherProjectItemKeys { get; } = [];

        public Task<bool> TombstoneAsync(string itemKey, DateTimeOffset observedAt, string reason, CancellationToken cancellationToken = default)
        {
            Calls.Add((itemKey, reason));
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<string>> FindOtherProjectItemKeysAsync(
            string provider, string sourceInstance, string kind, string externalId,
            string currentProjectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(OtherProjectItemKeys);
    }

    private sealed class NoArchive : IRawEvidenceArchive
    {
        public Task<string> StoreAsync(string itemKey, string revisionId, byte[] rawJson, string contentSha256, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("a deletion must not archive anything");
    }

    private sealed class NoStore : IVersionedEvidenceStore
    {
        public Task<EvidenceImportResult> SaveAsync(ArchivedEvidence evidence, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("a deletion must not store a revision");
    }

    [Fact]
    public async Task A_deletion_hides_the_current_item_and_its_revision_history()
    {
        var tombstones = new Tombstones();
        var sink = new CanonicalEvidenceSink(new EvidenceIngestionService(new NoArchive(), new NoStore()),
            new Uri("https://dev.azure.com/contoso"), "contoso")
        { Tombstones = tombstones };

        await sink.WriteAsync("work-item-deletions", "k", new WorkItemDeletion(1234, "Payments", DateTimeOffset.UtcNow, "deleted_in_source"));

        string Key(string kind) => new EvidenceIdentity("azure_devops", "https://dev.azure.com/contoso", "Payments", null, kind, "1234").ItemKey();
        Assert.Equal(new[] { (Key("work_item_current"), "deleted_in_source"), (Key("work_item"), "deleted_in_source") }, tombstones.Calls);
    }

    [Fact]
    public void A_deleted_event_is_parsed_as_a_deletion_to_confirm()
    {
        const string Deleted = """
            {"id":"evt-9","eventType":"workitem.deleted","resource":{"id":1234,"rev":10,
              "url":"https://dev.azure.com/contoso/_apis/wit/workItems/1234","fields":{"System.TeamProject":"VisionIQ Demo"}}}
            """;
        using var document = JsonDocument.Parse(Deleted);

        Assert.Equal(ServiceHookOutcome.Accepted, AdoServiceHook.Parse(document.RootElement, new Uri("https://dev.azure.com/contoso"), ["VisionIQ Demo"], "contoso", out var change));
        Assert.Equal("work_item_deleted", change!.ProviderKind);
    }

    private sealed class Reader(bool exists) : IAdoChangedWorkItemReader
    {
        public Task<ReadOutcome> ReadAsync(string project, int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(exists
                ? new ReadOutcome(new WorkItemRecord(id, 10, "Bug", "Active", "t", [], DateTimeOffset.UtcNow), null)
                : new ReadOutcome(null, "missing"));
    }

    private sealed class Capture : ICanonicalEvidenceSink
    {
        public List<(string Stream, object Record)> Writes { get; } = [];

        public Task WriteAsync(string stream, string key, object record, CancellationToken cancellationToken = default)
        {
            Writes.Add((stream, record));
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false, 1)] // ADO no longer returns it: hide it
    [InlineData(true, 0)]  // still there (e.g. already restored): do nothing
    public async Task A_deletion_event_is_confirmed_with_ado_before_anything_is_hidden(bool stillExists, int deletions)
    {
        var capture = new Capture();
        var handler = new AdoWorkItemChangeHandler(DemoAdo.Options(), new Reader(stillExists), capture);

        await handler.ApplyAsync(new SourceChangeNotification("contoso/ado/VisionIQ Demo", "evt-9", "work_item_deleted", "1234", "10"));

        Assert.Equal(deletions, capture.Writes.Count(w => w.Stream == "work-item-deletions"));
        Assert.DoesNotContain(capture.Writes, w => w.Stream == "work-items");
    }
}
