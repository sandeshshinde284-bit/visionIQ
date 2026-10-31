using System.Globalization;
using System.Text;
using System.Text.Json;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Checkpoints;
using VisionIQ.Ingestor.Sources;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Tests;

/// <summary>B-10: hitting the 20,000-item WIQL cap must save progress instead of re-reading the same items forever.</summary>
public sealed class WiqlCapTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset Changed(int id) => Start.AddSeconds(id);

    private static FakeAdoHandler CappedServer(int? omitId = null)
    {
        var handler = new FakeAdoHandler();
        handler.On(HttpMethod.Post, "/_apis/wit/wiql", _ =>
        {
            var ids = Enumerable.Range(1, AdoWorkItemSource.WiqlLimit).Select(id => $"{{\"id\":{id}}}");
            return FakeAdoHandler.Ok("{\"workItems\":[" + string.Join(",", ids) + "]}");
        });
        handler.On(HttpMethod.Post, "/_apis/wit/workitemsbatch", request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var items = new StringBuilder("{\"value\":[");
            var first = true;
            foreach (var id in body.RootElement.GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()))
            {
                if (id == omitId)
                {
                    continue;
                }

                items.Append(first ? string.Empty : ",");
                first = false;
                items.Append(CultureInfo.InvariantCulture,
                    $"{{\"id\":{id},\"rev\":1,\"fields\":{{\"System.WorkItemType\":\"Bug\",\"System.State\":\"New\",\"System.Title\":\"t\",\"System.ChangedDate\":\"{Changed(id):O}\",\"System.CreatedDate\":\"{Start:O}\"}},\"relations\":[]}}");
            }

            return FakeAdoHandler.Ok(items.Append("]}").ToString());
        });
        return handler;
    }

    private static SyncRequest WorkItemsOnly() =>
        new(new HashSet<SyncKind> { SyncKind.WorkItems }, null, IncludeRevisions: false);

    [Fact]
    public async Task A_capped_but_complete_batch_saves_progress_and_reports_more_to_read()
    {
        var checkpoints = new InMemoryCheckpointStore();
        var runner = new SyncRunner(DemoAdo.Client(CappedServer()), checkpoints, new MemorySink(), new SourceCapabilityRegistry());

        var report = await runner.RunAsync(WorkItemsOnly());

        var stream = Assert.Single(report.Streams);
        Assert.Equal(StreamStatus.Partial, stream.Status);
        Assert.Equal(AdoWorkItemSource.WiqlLimit, stream.Records);
        Assert.Contains("progress was saved", stream.Reason, StringComparison.Ordinal);
        var saved = await checkpoints.GetAsync("contoso/ado", "VisionIQ Demo/work-items");
        Assert.NotNull(saved);
        Assert.StartsWith(Changed(AdoWorkItemSource.WiqlLimit).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), saved!.Cursor);
    }

    [Fact]
    public async Task A_capped_batch_with_a_missing_item_does_not_advance_the_checkpoint()
    {
        var checkpoints = new InMemoryCheckpointStore();
        var runner = new SyncRunner(DemoAdo.Client(CappedServer(omitId: 50)), checkpoints, new MemorySink(), new SourceCapabilityRegistry());

        var report = await runner.RunAsync(WorkItemsOnly());

        Assert.Equal(StreamStatus.Partial, Assert.Single(report.Streams).Status);
        Assert.Null(await checkpoints.GetAsync("contoso/ado", "VisionIQ Demo/work-items"));
    }
}
