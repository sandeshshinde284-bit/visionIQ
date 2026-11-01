using VisionIQ.Api.Features.Evidence;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Cli;
using VisionIQ.Ingestor.Confluence;
using VisionIQ.Ingestor.Links;

namespace VisionIQ.Ingestor.Tests;

public sealed class CanonicalEvidenceSinkTests
{
    [Fact]
    public async Task Work_item_history_is_archived_to_one_canonical_item_with_two_revisions()
    {
        var store = new RecordingStore();
        var sink = Sink(store);
        var timestamp = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        var key = "contoso/ado/VisionIQ Demo/work-item/1234";

        await sink.WriteAsync("work-item-revisions", key + "@1",
            new WorkItemRevisionRecord(1234, 1, timestamp) { State = "Active", Title = "secret-details" });
        await sink.WriteAsync("work-item-revisions", key + "@2",
            new WorkItemRevisionRecord(1234, 2, timestamp.AddDays(1)) { State = "Closed" });

        Assert.Equal(2, store.Rows.Count);
        Assert.Single(store.Rows.Select(row => row.ItemKey).Distinct(StringComparer.Ordinal));
        Assert.Equal(new[] { "1", "2" }, store.Rows.Select(row => row.Envelope.ProviderRevisionId));
        Assert.All(store.Rows, row =>
        {
            Assert.True(row.Envelope.Access.Verified);
            Assert.Equal(new[] { "ado-project:https://dev.azure.com/contoso/VisionIQ Demo" }, row.Envelope.Access.PrincipalIds);
            Assert.Equal("work_item", row.Envelope.Identity.Kind);
            Assert.StartsWith("gs://", row.RawSnapshotUri);
            Assert.DoesNotContain("secret-details", row.RedactedJson, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Restricted_confluence_page_is_archived_but_never_granted_to_a_viewer()
    {
        var store = new RecordingStore();
        var sink = Sink(store);
        var acl = new ConfluencePageAcl(
            "1003", "ENG", true,
            [new RestrictionLevel("1000", ["acc-lead"], ["ENG-LEADS"])]);
        var page = new ConfluencePageSnapshot(
            "contoso.atlassian.net", "ENG", "1003", 4,
            "Private design", DateTimeOffset.UtcNow)
        {
            Acl = acl,
            Text = "Sensitive architecture details must not be indexed",
            Url = "https://contoso.atlassian.net/wiki/spaces/ENG/pages/1003",
        };
        await sink.WriteAsync(
            "confluence-pages", "contoso.atlassian.net/confluence/ENG/page/1003@4", page);

        var archived = Assert.Single(store.Rows);
        Assert.Equal("design_page", archived.Envelope.Identity.Kind);
        Assert.False(archived.Envelope.Access.Verified);
        Assert.Equal("[]", archived.Envelope.Access.Json());
        Assert.DoesNotContain("Sensitive architecture", archived.RedactedJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_named_work_item_in_two_projects_has_distinct_source_identities()
    {
        var store = new RecordingStore();
        var sink = Sink(store);
        var time = DateTimeOffset.UtcNow;
        await sink.WriteAsync("work-items", "contoso/ado/Alpha/work-item/123@1",
            new WorkItemRecord(123, 1, "Bug", "Active", "Example", [], time)
            { Project = "Alpha" });
        await sink.WriteAsync("work-items", "contoso/ado/Beta/work-item/123@1",
            new WorkItemRecord(123, 1, "Bug", "Active", "Example", [], time)
            { Project = "Beta" });
        Assert.Equal(2, store.Rows.Select(row => row.ItemKey).Distinct().Count());
    }

    [Fact]
    public async Task Project_scoped_links_keep_provenance_and_are_readable_only_by_their_project()
    {
        var store = new RecordingStore();
        var sink = Sink(store);
        var link = new SourceLink(EntityKind.WorkItem, "123", EntityKind.Commit,
            "abcdef", "work_item_artifact", "linked by ADO");
        await sink.WriteAsync("links", "contoso/ado/Alpha/link/" + link.Key, link);
        var evidence = Assert.Single(store.Rows);
        Assert.Equal("Alpha", evidence.Envelope.Identity.ProjectId);
        Assert.Equal("declared_link", evidence.Envelope.Identity.Kind);
        // B-5: ADO evidence is verified at project level, scoped to exactly its own project.
        Assert.True(evidence.Envelope.Access.Verified);
        Assert.Equal(new[] { "ado-project:https://dev.azure.com/contoso/Alpha" }, evidence.Envelope.Access.PrincipalIds);
    }

    [Fact]
    public async Task Redacted_metadata_keeps_safe_fields_for_every_ado_kind_and_never_free_text()
    {
        // B-13: field names must match what the sink actually serializes (camelCase records).
        var synced = new MemorySink();
        await new VisionIQ.Ingestor.Sync.SyncRunner(
            DemoAdo.Client(DemoAdo.Server()), new VisionIQ.Ingestor.Checkpoints.InMemoryCheckpointStore(),
            synced, new VisionIQ.Ingestor.Sources.SourceCapabilityRegistry())
            .RunAsync(new VisionIQ.Ingestor.Sync.SyncRequest(VisionIQ.Ingestor.Sync.SyncRequest.AllKinds, null));
        var store = new RecordingStore();
        var sink = Sink(store);
        foreach (var (stream, key, record) in synced.Items)
        {
            await sink.WriteAsync(stream, key, record);
        }

        System.Text.Json.JsonElement Meta(string kind) =>
            System.Text.Json.JsonDocument.Parse(store.Rows.First(row => row.Envelope.Identity.Kind == kind).RedactedJson).RootElement;

        Assert.Equal("Bug", Meta("work_item_current").GetProperty("workItemType").GetString());
        Assert.Equal("Resolved", Meta("work_item_current").GetProperty("state").GetString());
        Assert.True(Meta("work_item").TryGetProperty("state", out _));
        Assert.Equal("completed", Meta("pull_request").GetProperty("status").GetString());
        Assert.Equal("failed", Meta("ci_run").GetProperty("result").GetString());
        Assert.Equal("work_item_artifact", Meta("declared_link").GetProperty("linkType").GetString());
        Assert.All(store.Rows, row =>
        {
            Assert.DoesNotContain("Retry loop", row.RedactedJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("retrying", row.RedactedJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("payments-service", row.RedactedJson, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_link_carries_its_source_record_time_not_the_ingestion_time()
    {
        // m-10: the link was stated by the work item revision changed at this time.
        var changed = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var item = new WorkItemRecord(123, 7, "Bug", "Closed", "Title", [], changed)
        {
            Project = "Alpha",
            Relations = [new WorkItemRelation("ArtifactLink", "vstfs:///Git/PullRequestId/p%2Fr%2F42", "Pull Request")],
        };
        var store = new RecordingStore();
        var link = Assert.Single(VisionIQ.Ingestor.Links.LinkExtractor.FromWorkItem(item));

        await Sink(store).WriteAsync("links", "contoso/ado/Alpha/link/" + link.Key, link);

        Assert.Equal(changed, link.SourceTimestamp);
        Assert.Equal(changed, Assert.Single(store.Rows).Envelope.SourceUpdatedAt);
    }

    private static CanonicalEvidenceSink Sink(RecordingStore store) =>
        new(new EvidenceIngestionService(new FakeArchive(), store),
            new Uri("https://dev.azure.com/contoso"), "contoso",
            new Uri("https://contoso.atlassian.net/wiki"));

    private sealed class RecordingStore : IVersionedEvidenceStore
    {
        public List<ArchivedEvidence> Rows { get; } = [];

        public Task<EvidenceImportResult> SaveAsync(
            ArchivedEvidence evidence, CancellationToken cancellationToken = default)
        {
            Rows.Add(evidence);
            return Task.FromResult(
                new EvidenceImportResult(evidence.ItemKey,
                    evidence.Envelope.ProviderRevisionId, EvidenceImportStatus.Created));
        }
    }

    private sealed class FakeArchive : IRawEvidenceArchive
    {
        public Task<string> StoreAsync(
            string itemKey, string revisionId, byte[] rawJson,
            string contentSha256, CancellationToken cancellationToken = default)
            => Task.FromResult($"gs://isolated-test/{itemKey}/{contentSha256}");
    }
}
