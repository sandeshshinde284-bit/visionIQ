using System.Text;
using Npgsql;
using VisionIQ.Api.Configuration;
using VisionIQ.Api.Features.Evidence;

namespace VisionIQ.Api.Tests;

/// <summary>
/// Run with VISIONIQ_EVIDENCE_DB_INTEGRATION=1 after FND-03 role bootstrap.
/// CI provisions an isolated fresh-volume PostgreSQL database for this test.
/// </summary>
public sealed class EvidenceDatabaseIntegrationTests
{
    [Fact]
    public async Task Replayed_revision_is_idempotent_and_two_revisions_remain_immutable()
    {
        if (Environment.GetEnvironmentVariable("VISIONIQ_EVIDENCE_DB_INTEGRATION") != "1")
        {
            return;
        }

        var password = Environment.GetEnvironmentVariable("DB_API_PASSWORD");
        Assert.False(string.IsNullOrWhiteSpace(password));
        var options = new DatabaseOptions(
            Environment.GetEnvironmentVariable("DB_HOST") ?? "127.0.0.1",
            5432,
            Environment.GetEnvironmentVariable("DB_NAME") ?? "visioniq",
            Environment.GetEnvironmentVariable("DB_API_USER") ?? "visioniq_api",
            password!,
            "disable");

        await using var store = new PostgresEvidenceStore(options);
        var ingest = new EvidenceIngestionService(new FakeArchive(), store);
        var itemId = $"lrn03-integration-{Guid.NewGuid():N}";
        var identity = new EvidenceIdentity(
            "azure_devops", "test-org", "test-project", null, "work_item", itemId);
        var first = new EvidenceEnvelope(
            identity, "r1",
            new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow,
            "https://dev.azure.com/test-org/test-project/_workitems/edit/1",
            "test-only-connector",
            Encoding.UTF8.GetBytes("""{"state":"Active","title":"redact my name"}"""),
            new EvidenceAccess(true, ["test-readers"]));
        var second = first with
        {
            ProviderRevisionId = "r2",
            SourceUpdatedAt = first.SourceUpdatedAt.AddDays(1),
            RawJson = Encoding.UTF8.GetBytes("""{"state":"Closed","title":"another secret"}"""),
        };

        var created = await ingest.ImportAsync(first);
        Assert.Equal(EvidenceImportStatus.Created, created.Status);
        Assert.Equal(EvidenceImportStatus.Unchanged, (await ingest.ImportAsync(first)).Status);
        Assert.Equal(EvidenceImportStatus.NewRevision, (await ingest.ImportAsync(second)).Status);
        Assert.Equal(EvidenceImportStatus.Unchanged, (await ingest.ImportAsync(second)).Status);
        var conflict = first with
        {
            RawJson = Encoding.UTF8.GetBytes("""{"state":"Removed","title":"untrusted"}"""),
        };
        Assert.Equal(EvidenceImportStatus.Conflict, (await ingest.ImportAsync(conflict)).Status);

        var cs = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host,
            Port = options.Port,
            Database = options.Name,
            Username = options.User,
            Password = options.Password,
        };
        await using var connection = new NpgsqlConnection(cs.ConnectionString);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand(
            """
            SELECT
              (SELECT count(*) FROM evidence.source_items WHERE item_key=@key),
              (SELECT count(*) FROM evidence.source_revisions WHERE item_key=@key),
              (SELECT latest_revision_id FROM evidence.source_items WHERE item_key=@key)
            """,
            connection);
        query.Parameters.AddWithValue("key", created.ItemKey);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(2L, reader.GetInt64(1));
        Assert.Equal("r2", reader.GetString(2));
    }

    private sealed class FakeArchive : IRawEvidenceArchive
    {
        public Task<string> StoreAsync(
            string itemKey, string revisionId, byte[] rawJson, string contentSha256,
            CancellationToken cancellationToken = default)
            => Task.FromResult($"gs://test-evidence/{itemKey}/{revisionId}/{contentSha256}");
    }
}
