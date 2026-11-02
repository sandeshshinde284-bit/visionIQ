using System.Text;
using System.Text.Json;
using VisionIQ.Api.Features.Evidence;

namespace VisionIQ.Ingestor.Tests;

/// <summary>LRN-03: synthetic boundary tests; not a substitute for PostgreSQL/GCS integration.</summary>
public sealed class Lrn03EvidenceBoundaryTests
{
    private sealed class Archive : IRawEvidenceArchive
    {
        public int Calls { get; private set; }
        public bool Fail { get; init; }
        public Task<string> StoreAsync(string itemKey, string revisionId, byte[] rawJson, string contentSha256, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Fail)
            {
                throw new IOException("Synthetic archive failure");
            }
            return Task.FromResult("gs://test-artifact/" + itemKey);
        }
    }

    private sealed class Store : IVersionedEvidenceStore
    {
        public int Calls { get; private set; }
        public ArchivedEvidence? Last { get; private set; }
        public Task<EvidenceImportResult> SaveAsync(ArchivedEvidence evidence, CancellationToken cancellationToken = default)
        {
            Calls++;
            Last = evidence;
            return Task.FromResult(new EvidenceImportResult(evidence.ItemKey, evidence.Envelope.ProviderRevisionId, EvidenceImportStatus.Created));
        }
    }

    private static EvidenceEnvelope Envelope(string payload = """{"status":"Active","description":"private-token-123","someSecret":"secret"}""")
        => new(
            new EvidenceIdentity("azure_devops", "https://dev.azure.com/fictional", "demo-project", null, "pull_request", "42"),
            "revision-2", DateTimeOffset.Parse("2026-10-01T00:00:00+05:30"),
            DateTimeOffset.Parse("2026-10-02T00:00:00+05:30"),
            "https://dev.azure.com/fictional/demo-project/_git/demo/pullrequest/42",
            "trusted-test-connector",
            Encoding.UTF8.GetBytes(payload),
            new EvidenceAccess(true, ["approved-project"]));

    [Fact]
    public async Task Raw_copy_is_archived_before_database_revision_is_written()
    {
        var archive = new Archive();
        var store = new Store();
        var result = await new EvidenceIngestionService(archive, store).ImportAsync(Envelope());
        Assert.Equal(EvidenceImportStatus.Created, result.Status);
        Assert.Equal(1, archive.Calls);
        Assert.Equal(1, store.Calls);
        Assert.NotNull(store.Last);
        Assert.StartsWith("gs://", store.Last.RawSnapshotUri, StringComparison.Ordinal);
        Assert.DoesNotContain("private-token-123", store.Last.RedactedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("someSecret", store.Last.RedactedJson, StringComparison.Ordinal);
        using var redacted = JsonDocument.Parse(store.Last.RedactedJson);
        Assert.Equal("pull_request", redacted.RootElement.GetProperty("kind").GetString());
        Assert.Equal("revision-2", store.Last.Envelope.ProviderRevisionId);
    }

    [Fact]
    public async Task Archive_failure_prevents_any_database_write()
    {
        var archive = new Archive { Fail = true };
        var store = new Store();
        await Assert.ThrowsAsync<IOException>(() => new EvidenceIngestionService(archive, store).ImportAsync(Envelope()));
        Assert.Equal(1, archive.Calls);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public void Stable_identity_distinguishes_repository_and_provider()
    {
        var original = Envelope().Identity;
        Assert.Equal(original.ItemKey(), original.ItemKey());
        Assert.NotEqual(original.ItemKey(), (original with { RepositoryId = "repo-1" }).ItemKey());
        Assert.NotEqual(original.ItemKey(), (original with { Provider = "confluence" }).ItemKey());
    }


    [Theory]
    [InlineData("commit", """{"commentTruncated":"SYNTHETIC-CREDENTIAL-NEVER-INDEX"}""")]
    [InlineData("work_item_current", """{"state":"Active","environmentRaw":"SYNTHETIC-CREDENTIAL-NEVER-INDEX","environmentReason":"SYNTHETIC-CREDENTIAL-NEVER-INDEX","environment":"SYNTHETIC-CREDENTIAL-NEVER-INDEX"}""")]
    [InlineData("work_item", """{"state":"Active","description":"SYNTHETIC-CREDENTIAL-NEVER-INDEX"}""")]
    [InlineData("design_page", """{"status":"Published","text":"SYNTHETIC-CREDENTIAL-NEVER-INDEX"}""")]
    public void Indexable_metadata_never_contains_synthetic_sensitive_source_text(string kind, string raw)
    {
        var indexed = EvidenceNormalizer.Redact(kind, Encoding.UTF8.GetBytes(raw));
        Assert.DoesNotContain("SYNTHETIC-CREDENTIAL-NEVER-INDEX", indexed, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_kind_never_copies_unapproved_fields_into_redacted_metadata()
    {
        var output = EvidenceNormalizer.Redact("unknown-source-kind", Encoding.UTF8.GetBytes("""{"status":"Ready","token":"secret","description":"private"}"""));
        using var document = JsonDocument.Parse(output);
        Assert.Equal("unknown-source-kind", document.RootElement.GetProperty("kind").GetString());
        Assert.False(document.RootElement.TryGetProperty("status", out _));
        Assert.DoesNotContain("secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private", output, StringComparison.Ordinal);
    }
}
