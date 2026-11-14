using System.Text.Json;
using VisionIQ.Api.Features.TestLinks;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Cli;
using VisionIQ.Ingestor.Links;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Tests;

/// <summary>LRN-07: ADO "Tested By" associations and Test Case automation are captured as declared evidence.</summary>
public sealed class TestLinkProjectionTests
{
    private static readonly Uri Organization = new("https://dev.azure.com/contoso");
    private static readonly DateTimeOffset Changed = new(2026, 5, 6, 7, 8, 9, TimeSpan.Zero);

    private static WorkItemRecord Bug(params WorkItemRelation[] relations) =>
        new(1234, 9, "Bug", "Resolved", "Retry never stops", [], Changed) { Project = "Payments", Relations = relations };

    private static WorkItemRelation Relation(string rel, int target) =>
        new(rel, $"https://dev.azure.com/contoso/_apis/wit/workItems/{target}", null);

    [Fact]
    public void Ado_tested_by_relations_are_their_own_link_type()
    {
        var links = LinkExtractor.FromWorkItem(Bug(Relation(LinkExtractor.TestedByForward, 77), Relation("System.LinkTypes.Related", 88)));

        Assert.Equal("tested_by", Assert.Single(links, link => link.ToId == "77").LinkType);
        Assert.Equal("work_item_relation", Assert.Single(links, link => link.ToId == "88").LinkType);
    }

    [Fact]
    public void Test_case_automation_fields_are_parsed_and_omitted_when_absent()
    {
        const string TestCase = """
        {"id":77,"rev":2,"fields":{"System.WorkItemType":"Test Case","System.State":"Ready","System.Title":"t",
          "System.ChangedDate":"2026-01-01T00:00:00Z","System.CreatedDate":"2026-01-01T00:00:00Z",
          "Microsoft.VSTS.TCM.AutomatedTestName":"Shop.Tests.CartTests.Retry_stops",
          "Microsoft.VSTS.TCM.AutomatedTestStorage":"Shop.Tests.dll"}}
        """;

        var record = AdoWorkItemReader.ParseWorkItem(TestCase).Record!;

        Assert.Equal(("Shop.Tests.CartTests.Retry_stops", "Shop.Tests.dll"), (record.AutomatedTestName, record.AutomatedTestStorage));
        Assert.DoesNotContain("automatedTest", JsonSerializer.Serialize(Bug(), new JsonSerializerOptions(JsonSerializerDefaults.Web)), StringComparison.Ordinal);
    }

    private sealed class RecordingStore : ITestLinkStore
    {
        public List<TestLinkSnapshot> Snapshots { get; } = [];

        public Task ReplaceAsync(TestLinkSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Snapshots.Add(snapshot);
            return Task.CompletedTask;
        }
    }

    private sealed class Evidence : ICanonicalEvidenceSink
    {
        public List<string> Streams { get; } = [];

        public Task WriteAsync(string stream, string key, object record, CancellationToken cancellationToken = default)
        {
            Streams.Add(stream);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Each_work_item_writes_evidence_first_then_its_current_test_links()
    {
        var evidence = new Evidence();
        var store = new RecordingStore();
        var sink = new TestLinkProjectionSink(evidence, store, Organization);
        var bug = Bug(Relation(LinkExtractor.TestedByForward, 77), Relation(LinkExtractor.TestedByForward, 78));

        await sink.WriteAsync("work-items", "k", bug);
        await sink.WriteAsync("pull-requests", "k2", new object());

        var snapshot = Assert.Single(store.Snapshots);
        Assert.Equal(new[] { "work-items", "pull-requests" }, evidence.Streams);
        Assert.Equal(("https://dev.azure.com/contoso", "Payments", "1234", Changed), (snapshot.SourceInstance, snapshot.ProjectId, snapshot.WorkItemId, snapshot.SourceAssertedAt));
        Assert.Equal(new[] { "77", "78" }, snapshot.TestCaseIds);
        Assert.Null(snapshot.AutomatedTestName);
    }

    [Fact]
    public void A_work_item_with_no_test_links_still_produces_an_empty_snapshot_so_removals_apply()
    {
        var snapshot = TestLinkProjectionSink.Snapshot("https://dev.azure.com/contoso", Bug());

        Assert.Empty(snapshot.TestCaseIds);
    }

    [Fact]
    public void The_snapshot_cites_exactly_the_stored_current_work_item_revision()
    {
        var bug = Bug(Relation(LinkExtractor.TestedByForward, 77));
        var snapshot = TestLinkProjectionSink.Snapshot("https://dev.azure.com/contoso", bug);

        Assert.EndsWith(":" + CanonicalEvidenceSink.ContentRevision("9", bug), snapshot.EvidenceRef, StringComparison.Ordinal);
        Assert.True(VisionIQ.Api.Features.Graph.TestLinkGraphMaterializer.TryParseReference(snapshot.EvidenceRef, out _, out var revision));
        Assert.StartsWith("9#", revision);
    }
}
