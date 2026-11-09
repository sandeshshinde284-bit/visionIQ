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
    public async Task A_confluence_page_stores_only_a_space_candidate_and_no_raw_text()
    {
        var store = new RecordingStore();
        var sink = Sink(store);
        var page = new ConfluencePageSnapshot(
            "contoso.atlassian.net", "ENG", "1003", 4,
            "Private design", DateTimeOffset.UtcNow)
        {
            Text = "Sensitive architecture details must not be indexed",
            Url = "https://contoso.atlassian.net/wiki/spaces/ENG/pages/1003",
        };
        await sink.WriteAsync(
            "confluence-pages", "contoso.atlassian.net/confluence/ENG/page/1003@4", page);

        var archived = Assert.Single(store.Rows);
        Assert.Equal("design_page", archived.Envelope.Identity.Kind);
        // LRN-02: storage never grants access by itself. Only a space-level candidate is stored;
        // reading would require a per-user authorization check, which is currently removed (no
        // registered Confluence verifier), so Confluence evidence fails closed at read time.
        Assert.Equal(new[] { "confluence-space:https://contoso.atlassian.net/ENG" }, archived.Envelope.Access.PrincipalIds);
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
        // Environment classification is part of the safe metadata (UNKNOWN: demo project has no rules).
        Assert.Equal("UNKNOWN", Meta("work_item_current").GetProperty("environment").GetString());
        Assert.Equal("not_configured", Meta("work_item_current").GetProperty("environmentReason").GetString());
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

    [Fact]
    public async Task A_reviewer_vote_is_archived_as_its_own_review_kind_with_only_status_and_vote_redacted()
    {
        var store = new RecordingStore();
        var sink = Sink(store);
        var observed = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        var review = new PullRequestReviewRecord("rev-1", 99, "repo-1", "payments-service", "VisionIQ Demo", 10, true, false, observed);

        await sink.WriteAsync("pull-request-reviews", "contoso/ado/VisionIQ Demo/pull-request-review/99:rev-1@approved@10", review);

        var row = Assert.Single(store.Rows);
        Assert.Equal("review", row.Envelope.Identity.Kind);
        Assert.Equal("99:rev-1", row.Envelope.Identity.ExternalId);
        Assert.True(row.Envelope.Access.Verified);
        Assert.Equal(new[] { "ado-project:https://dev.azure.com/contoso/VisionIQ Demo" }, row.Envelope.Access.PrincipalIds);
        Assert.StartsWith("gs://", row.RawSnapshotUri);
        using var redacted = System.Text.Json.JsonDocument.Parse(row.RedactedJson);
        Assert.Equal("review", redacted.RootElement.GetProperty("kind").GetString());
        Assert.Equal("approved", redacted.RootElement.GetProperty("status").GetString());
        Assert.Equal(10, redacted.RootElement.GetProperty("vote").GetInt32());
        // Reviewer identity is a principal/audit fact, not indexed source text, but it must never
        // leak into the redacted metadata that downstream search would read.
        Assert.DoesNotContain("rev-1", row.RedactedJson, StringComparison.Ordinal);
    }

    private static CanonicalEvidenceSink Sink(RecordingStore store, IEvidenceTombstones? tombstones = null) =>
        new(new EvidenceIngestionService(new FakeArchive(), store),
            new Uri("https://dev.azure.com/contoso"), "contoso",
            new Uri("https://contoso.atlassian.net/wiki"))
        {
            Tombstones = tombstones,
        };

    [Fact]
    public async Task A_work_item_observed_under_a_new_project_tombstones_its_old_projects_copy()
    {
        // Review R1-03: a work item moved from project Alpha to project Beta stops matching Alpha's
        // WIQL filter, so Alpha's copy is never otherwise revisited. Observing it under Beta must
        // tombstone Alpha's stale copy (both the current item and its revision history), the same way
        // an actual ADO deletion is tombstoned (N-2).
        var store = new RecordingStore();
        var tombstones = new DeletionTests.Tombstones();
        tombstones.OtherProjectItemKeys.Add("stale-current-key");
        var sink = Sink(store, tombstones);
        var item = new WorkItemRecord(123, 2, "Bug", "Active", "Moved item", [], DateTimeOffset.UtcNow)
        {
            Project = "Beta",
        };

        await sink.WriteAsync("work-items", "contoso/ado/Beta/work-item/123", item);

        Assert.Contains(tombstones.Calls, call => call.Key == "stale-current-key" && call.Reason == "missing_in_source");
    }

    [Fact]
    public async Task No_cross_project_lookup_happens_without_a_tombstones_dependency()
    {
        // The optional dependency must stay optional: nothing throws when it is null.
        var store = new RecordingStore();
        var sink = Sink(store);
        var item = new WorkItemRecord(123, 2, "Bug", "Active", "Moved item", [], DateTimeOffset.UtcNow)
        {
            Project = "Beta",
        };

        await sink.WriteAsync("work-items", "contoso/ado/Beta/work-item/123", item);

        Assert.Single(store.Rows);
    }

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
