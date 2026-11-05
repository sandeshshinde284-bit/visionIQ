using System.Text;
using System.Text.Json;
using VisionIQ.Api.Features.Evidence;

namespace VisionIQ.Api.Tests;

public sealed class EvidenceIngestionTests
{
    private static EvidenceEnvelope Envelope(
        string revision = "7",
        string payload = """{"state":"Active","title":"SECRET TEXT","token":"private"}""")
        => new(
            new EvidenceIdentity("azure_devops", "org-one", "project-one", "repo-one",
                "work_item", "123"),
            revision,
            new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.Zero),
            "https://dev.azure.com/org-one/project-one/_workitems/edit/123",
            "connector-identity",
            Encoding.UTF8.GetBytes(payload),
            new EvidenceAccess(true, ["group-readers"]));

    [Fact]
    public void Canonical_source_key_is_stable_and_scope_sensitive()
    {
        var one = Envelope().Identity;
        Assert.Equal(one.ItemKey(), one.ItemKey());
        Assert.Equal(64, one.ItemKey().Length);
        Assert.NotEqual(one.ItemKey(), (one with { ProjectId = "project-two" }).ItemKey());
        Assert.NotEqual(one.ItemKey(), (one with { RepositoryId = null }).ItemKey());
        Assert.NotEqual(one.ItemKey(), (one with { ExternalId = "0123" }).ItemKey());
        Assert.NotEqual(
            new EvidenceIdentity("ab", "c", "d", null, "x", "y").ItemKey(),
            new EvidenceIdentity("a", "bc", "d", null, "x", "y").ItemKey());
    }

    [Fact]
    public void Unknown_access_is_fail_closed()
    {
        var access = new EvidenceAccess(false, ["admin", "reader"]);
        Assert.Equal("[]", access.Json());
        Assert.Equal("""["alpha","zeta"]""",
            new EvidenceAccess(true, ["zeta", "alpha", "alpha"]).Json());
    }

    [Fact]
    public void Normalizer_excludes_free_text_and_unreviewed_properties()
    {
        var payload = Encoding.UTF8.GetBytes(
            """{"state":"Active","title":"USER SECRET","token":"very-secret","result":"unused"}""");
        var normalized = EvidenceNormalizer.Redact("work_item", payload);
        using var json = JsonDocument.Parse(normalized);
        Assert.Equal("work_item", json.RootElement.GetProperty("kind").GetString());
        Assert.Equal("Active", json.RootElement.GetProperty("state").GetString());
        Assert.False(json.RootElement.TryGetProperty("title", out _));
        Assert.DoesNotContain("very-secret", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("USER SECRET", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void Current_work_item_keeps_defect_environment_classification_but_not_raw_source_text()
    {
        // Review D-01: the PROD/UAT/TEST classification was computed by the ingestor but dropped
        // by the allowlist, so canonical metadata could never be filtered by environment.
        var payload = Encoding.UTF8.GetBytes(
            """{"state":"Closed","workItemType":"Bug","environment":"PROD","environmentStatus":"verified","environmentSource":"field","environmentReason":"matched","environmentRaw":"Production - EU customer Acme"}""");
        var normalized = EvidenceNormalizer.Redact("work_item_current", payload);
        using var json = JsonDocument.Parse(normalized);
        Assert.Equal("PROD", json.RootElement.GetProperty("environment").GetString());
        Assert.Equal("verified", json.RootElement.GetProperty("environmentStatus").GetString());
        Assert.Equal("field", json.RootElement.GetProperty("environmentSource").GetString());
        Assert.Equal("matched", json.RootElement.GetProperty("environmentReason").GetString());
        Assert.False(json.RootElement.TryGetProperty("environmentRaw", out _));
        Assert.DoesNotContain("Acme", normalized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"environment":"CONFLICT","environmentSource":"field,labels","environmentReason":"sources_disagree"}""", "CONFLICT", "field,labels")]
    [InlineData("""{"environment":"Prod EU","environmentSource":"field,secret","environmentReason":"customer said so"}""", null, null)]
    public void Environment_fields_accept_only_the_classifier_vocabulary(string raw, string? environment, string? source)
    {
        using var json = JsonDocument.Parse(EvidenceNormalizer.Redact("work_item_current", Encoding.UTF8.GetBytes(raw)));
        Assert.Equal(environment, json.RootElement.TryGetProperty("environment", out var e) ? e.GetString() : null);
        Assert.Equal(source, json.RootElement.TryGetProperty("environmentSource", out var s) ? s.GetString() : null);
        Assert.Equal(environment is null, !json.RootElement.TryGetProperty("environmentReason", out _));
    }

    [Fact]
    public void Unknown_kind_keeps_no_source_fields()
    {
        var normalized = EvidenceNormalizer.Redact(
            "unknown_new_kind", Encoding.UTF8.GetBytes("""{"secret":"hidden"}"""));
        Assert.Equal("""{"kind":"unknown_new_kind"}""", normalized);
    }

    [Fact]
    public async Task Import_archives_before_writing_sanitized_metadata()
    {
        var archive = new FakeArchive();
        var store = new FakeStore();
        var service = new EvidenceIngestionService(archive, store);
        var result = await service.ImportAsync(Envelope());

        Assert.Equal(EvidenceImportStatus.Created, result.Status);
        Assert.True(archive.Uploaded);
        Assert.True(store.Saved);
        Assert.True(store.ArchivedAfterUpload);
        Assert.DoesNotContain("SECRET TEXT", store.SavedEvidence!.RedactedJson);
        Assert.DoesNotContain("private", store.SavedEvidence.RedactedJson);
        Assert.Equal("metadata-only-v1", store.SavedEvidence.NormalizerVersion);
        Assert.StartsWith("gs://", store.SavedEvidence.RawSnapshotUri);
    }

    [Fact]
    public async Task An_archive_failure_does_not_write_a_database_revision()
    {
        var store = new FakeStore();
        var archive = new FakeArchive { Throw = true };
        var service = new EvidenceIngestionService(archive, store);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ImportAsync(Envelope()));
        Assert.False(store.Saved);
    }

    [Fact]
    public async Task An_invalid_source_url_is_rejected_before_archive()
    {
        var archive = new FakeArchive();
        var store = new FakeStore();
        var service = new EvidenceIngestionService(archive, store);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ImportAsync(Envelope() with { SourceUrl = "file:///tmp/private" }));
        Assert.False(archive.Uploaded);
        Assert.False(store.Saved);
    }

    private sealed class FakeArchive : IRawEvidenceArchive
    {
        public bool Uploaded { get; private set; }
        public bool Throw { get; init; }

        public Task<string> StoreAsync(
            string itemKey, string revisionId, byte[] rawJson, string contentSha256,
            CancellationToken cancellationToken = default)
        {
            if (Throw)
            {
                throw new InvalidOperationException("Simulated archive failure");
            }

            Uploaded = true;
            return Task.FromResult($"gs://approved-bucket/{itemKey}/{revisionId}/{contentSha256}");
        }
    }

    private sealed class FakeStore : IVersionedEvidenceStore
    {
        public bool Saved { get; private set; }
        public bool ArchivedAfterUpload { get; private set; }
        public ArchivedEvidence? SavedEvidence { get; private set; }

        public Task<EvidenceImportResult> SaveAsync(
            ArchivedEvidence evidence, CancellationToken cancellationToken = default)
        {
            Saved = true;
            ArchivedAfterUpload = evidence.RawSnapshotUri.StartsWith("gs://");
            SavedEvidence = evidence;
            return Task.FromResult(new EvidenceImportResult(
                evidence.ItemKey, evidence.Envelope.ProviderRevisionId,
                EvidenceImportStatus.Created));
        }
    }
}
