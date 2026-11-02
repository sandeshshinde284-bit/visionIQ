using System.Security.Cryptography;
using System.Text;
using Npgsql;
using VisionIQ.Api.Configuration;
using VisionIQ.Api.Features.Evidence;

namespace VisionIQ.Ingestor.Tests;

/// <summary>
/// Opt-in REAL PostgreSQL integration test. Uses synthetic records and an in-memory
/// archive stub, so this validates PostgresEvidenceStore but NOT real GCS.
/// Requires LRN03_TEST_DB=1 and the isolated DB host/port/name/user/password env.
/// </summary>
public sealed class Lrn03PostgresIntegrationTests
{
    private sealed class SyntheticArchive : IRawEvidenceArchive
    {
        public Task<string> StoreAsync(string itemKey, string revisionId, byte[] rawJson, string contentSha256, CancellationToken cancellationToken = default)
            => Task.FromResult("gs://synthetic-lrn03-test/" + itemKey + "/" + contentSha256);
    }

    [Fact]
    public async Task Two_revisions_and_replays_stay_one_item_two_revisions()
    {
        if (Environment.GetEnvironmentVariable("LRN03_TEST_DB") != "1")
        {
            // Explicit opt-in: never connect to a real DB during ordinary regression tests.
            return;
        }

        var host = Environment.GetEnvironmentVariable("DB_HOST");
        var port = Environment.GetEnvironmentVariable("DB_PORT");
        var db = Environment.GetEnvironmentVariable("DB_LOCAL_NAME");
        var user = Environment.GetEnvironmentVariable("DB_API_USER");
        var password = Environment.GetEnvironmentVariable("DB_API_PASSWORD");

        if (host != "127.0.0.1" || port != "55433" || db != "visioniq_lrn03_test" ||
            user != "visioniq_lrn03_api" || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("LRN03 test requires the explicitly isolated database and API role.");
        }

        var options = new DatabaseOptions(host, 55433, db, user, password, "disable");
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = host, Port = 55433, Database = db, Username = user, Password = password,
            SslMode = Npgsql.SslMode.Disable
        }.ConnectionString;

        var identity = new EvidenceIdentity(
            "azure_devops", "https://dev.azure.com/lrn03-synthetic", "synthetic-project",
            null, "work_item", "lrn03-test-" + Guid.NewGuid().ToString("N"));
        var key = identity.ItemKey();

        EvidenceEnvelope Envelope(string revision, string status, int day)
        {
            var observed = new DateTimeOffset(2026, 11, day, 12, 0, 0, TimeSpan.Zero);
            return new EvidenceEnvelope(
                identity, revision, observed, observed,
                "https://dev.azure.com/lrn03-synthetic/synthetic-project/_workitems/edit/42",
                "synthetic-test-connector",
                Encoding.UTF8.GetBytes("{\"state\":\"" + status + "\",\"description\":\"private-not-indexed\"}"),
                new EvidenceAccess(true, ["synthetic-project"]));
        }

        await using var store = new PostgresEvidenceStore(options);
        var service = new EvidenceIngestionService(new SyntheticArchive(), store);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        try
        {
            var first = await service.ImportAsync(Envelope("1", "Open", 1));
            var second = await service.ImportAsync(Envelope("2", "Closed", 2));
            var repeat1 = await service.ImportAsync(Envelope("1", "Open", 1));
            var repeat2 = await service.ImportAsync(Envelope("2", "Closed", 2));
            var conflict = await service.ImportAsync(Envelope("2", "Changed", 2));

            Assert.Equal(EvidenceImportStatus.Created, first.Status);
            Assert.Equal(EvidenceImportStatus.NewRevision, second.Status);
            Assert.Equal(EvidenceImportStatus.Unchanged, repeat1.Status);
            Assert.Equal(EvidenceImportStatus.Unchanged, repeat2.Status);
            Assert.Equal(EvidenceImportStatus.Conflict, conflict.Status);

            await using var query = new NpgsqlCommand("""
                SELECT
                  (SELECT count(*) FROM evidence.source_items WHERE item_key = @key),
                  (SELECT count(*) FROM evidence.source_revisions WHERE item_key = @key),
                  (SELECT latest_revision_id FROM evidence.source_items WHERE item_key = @key),
                  (SELECT count(*) FROM evidence.source_revisions
                   WHERE item_key = @key AND acl_verified AND acl_principals = '["synthetic-project"]'::jsonb
                     AND raw_snapshot_uri LIKE 'gs://synthetic-lrn03-test/%'
                     AND redacted_payload::text NOT LIKE '%private-not-indexed%')
                """, connection);
            query.Parameters.AddWithValue("key", key);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(2L, reader.GetInt64(1));
            Assert.Equal("2", reader.GetString(2));
            Assert.Equal(2L, reader.GetInt64(3));
        }
        finally
        {
            // Delete ONLY synthetic per-test rows; never modify other evidence.
            await using var removeRevisions = new NpgsqlCommand(
                "DELETE FROM evidence.source_revisions WHERE item_key = @key", connection);
            removeRevisions.Parameters.AddWithValue("key", key);
            await removeRevisions.ExecuteNonQueryAsync();
            await using var removeItem = new NpgsqlCommand(
                "DELETE FROM evidence.source_items WHERE item_key = @key", connection);
            removeItem.Parameters.AddWithValue("key", key);
            await removeItem.ExecuteNonQueryAsync();
        }
    }
}
