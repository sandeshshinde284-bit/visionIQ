// LRN-04 synthetic-only full canonical integration check.
// This is NOT the production ingestion runner. It exercises the real
// CanonicalEvidenceSink -> EvidenceIngestionService -> CloudStorageEvidenceArchive
// -> PostgresEvidenceStore -> TicketFixProjectionSink -> PostgresTicketFixLinkStore.
// Writes only when invoked with --execute and exact test-resource confirmations.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google;
using Google.Cloud.Storage.V1;
using Npgsql;
using VisionIQ.Api.Configuration;
using VisionIQ.Api.Features.Evidence;
using VisionIQ.Api.Features.TicketFixes;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Cli;

const string ApprovedBucket = "visioniq-lrn03-test-1";
const string ApprovedDatabase = "visioniq_lrn03_test";
const string ApprovedUser = "visioniq_lrn03_api";
const int ApprovedPort = 55433;

if (args.Length == 0)
{
    Console.WriteLine("DRY RUN ONLY: no database connection or GCS request was made.");
    Console.WriteLine($"Allowed target: localhost:{ApprovedPort}/{ApprovedDatabase} as {ApprovedUser}.");
    Console.WriteLine($"Allowed GCS bucket: {ApprovedBucket}. Explicit opt-in: --execute.");
    return 0;
}

if (args.Length != 1 || args[0] != "--execute")
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/Lrn04CanonicalCheck [-- --execute]");
    return 1;
}

static string Required(string variable) =>
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable))
        ? Environment.GetEnvironmentVariable(variable)!
        : throw new InvalidOperationException($"Missing required environment variable: {variable}");

static string Sha256Hex(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));

var sourceScope = "";
var itemKey = "";
var objectName = "";
var wroteOrAttempted = false;
var bucket = "";
var connectionString = "";
var rawExpected = Array.Empty<byte>();
var testPassed = false;
var cleanupPassed = true;

try
{
    var host = Required("DB_HOST");
    var portText = Required("DB_PORT");
    var name = Required("DB_NAME");
    var user = Required("DB_API_USER");
    var password = Required("DB_API_PASSWORD");
    bucket = Required("GCS_EVIDENCE_BUCKET");
    var confirmed = Required("LRN04_CANONICAL_BUCKET_CONFIRM");

    if (host is not ("127.0.0.1" or "localhost") ||
        portText != ApprovedPort.ToString(CultureInfo.InvariantCulture) ||
        name != ApprovedDatabase || user != ApprovedUser ||
        bucket != ApprovedBucket || confirmed != ApprovedBucket)
    {
        throw new InvalidOperationException(
            "SAFETY STOP: target is not the approved isolated PostgreSQL database and GCS bucket.");
    }

    var builder = new NpgsqlConnectionStringBuilder
    {
        Host = host,
        Port = ApprovedPort,
        Database = name,
        Username = user,
        Password = password,
        SslMode = SslMode.Disable,
        Timeout = 10,
    };
    connectionString = builder.ConnectionString;
    var options = new DatabaseOptions(host, ApprovedPort, name, user, password, "disable");

    // Verify the actual server identity BEFORE sending any cloud payload or database write.
    await using (var guardConnection = new NpgsqlConnection(connectionString))
    {
        await guardConnection.OpenAsync();
        await using var guardCommand = new NpgsqlCommand(
            "SELECT current_database(), current_user", guardConnection);
        await using var guardReader = await guardCommand.ExecuteReaderAsync();
        if (!await guardReader.ReadAsync() ||
            guardReader.GetString(0) != ApprovedDatabase ||
            guardReader.GetString(1) != ApprovedUser)
        {
            throw new InvalidOperationException("SAFETY STOP: PostgreSQL session identity mismatch.");
        }
    }
    Console.WriteLine("PASS: isolated PostgreSQL connection identity verified.");

    // Unique synthetic source, repository, project and immutable raw PR. No live ADO data.
    var runId = Guid.NewGuid().ToString("N");
    var organizationUrl = new Uri($"https://lrn04-canonical-{runId}.invalid/");
    sourceScope = organizationUrl.AbsoluteUri.TrimEnd('/');
    var project = "LRN04_SYNTHETIC_" + runId;
    var repository = "synthetic-repo-" + runId;
    var syntheticSha = new string('a', 40);
    var created = new DateTimeOffset(2026, 11, 4, 7, 0, 0, TimeSpan.Zero);
    var pr = new PullRequestRecord(
        900001, repository, "synthetic-test-repo", project, "completed",
        "SYNTHETIC-SECRET-TITLE-NOT-FOR-INDEX", created)
    {
        ClosedDate = created.AddHours(1),
        WorkItemIds = [900002],
        CommitIds = [syntheticSha],
        MergeCommitId = syntheticSha,
        Description = "SYNTHETIC-SECRET-DESCRIPTION-NOT-FOR-INDEX",
    };

    // Compute the exact canonical identity and raw GCS object name ahead of writing.
    var nativeRevision = pr.Status + "@" + pr.LastActivity.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    var providerRevision = CanonicalEvidenceSink.ContentRevision(nativeRevision, pr);
    itemKey = new EvidenceIdentity("azure_devops", sourceScope, project, repository,
        "pull_request", pr.Id.ToString(CultureInfo.InvariantCulture)).ItemKey();
    rawExpected = JsonSerializer.SerializeToUtf8Bytes(pr, pr.GetType(),
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var sha = Sha256Hex(rawExpected);
    objectName = "raw/v1/" + itemKey + "/" +
        Sha256Hex(Encoding.UTF8.GetBytes(providerRevision)) + "/" + sha + ".json";
    var expectedUri = "gs://" + bucket + "/" + objectName;
    var expectedRef = "evidence:" + itemKey + ":" + providerRevision;

    // Preserve the production order: real GCS archive, real evidence table,
    // then real ticket/fix association projection.
    await using var evidenceStore = new PostgresEvidenceStore(options);
    await using var linkStore = new PostgresTicketFixLinkStore(options);
    var canonical = new CanonicalEvidenceSink(
        new EvidenceIngestionService(new CloudStorageEvidenceArchive(bucket), evidenceStore),
        organizationUrl, "synthetic-tenant");
    var pipeline = new TicketFixProjectionSink(canonical, linkStore, organizationUrl);

    // Set the flag before calling GCS, so partially successful uploads are cleaned up.
    wroteOrAttempted = true;
    await pipeline.WriteAsync("pull-requests", "synthetic/pr/900001", pr);
    await VerifyDatabaseAsync(connectionString, sourceScope, project, itemKey,
        providerRevision, expectedUri, expectedRef, sha, syntheticSha);
    Console.WriteLine("PASS: GCS archive -> canonical PostgreSQL revision -> LRN-04 link persisted and cross-referenced.");

    var storage = StorageClient.Create();
    var originalObject = await storage.GetObjectAsync(bucket, objectName);
    var firstGeneration = originalObject.Generation;
    using (var download = new MemoryStream())
    {
        await storage.DownloadObjectAsync(bucket, objectName, download);
        if (!download.ToArray().SequenceEqual(rawExpected) || Sha256Hex(download.ToArray()) != sha)
        {
            throw new InvalidOperationException("Archived GCS bytes do not match the canonical source record.");
        }
    }
    Console.WriteLine("PASS: GCS archived bytes and SHA-256 match synthetic PR JSON.");

    await pipeline.WriteAsync("pull-requests", "synthetic/pr/900001", pr);
    await VerifyDatabaseAsync(connectionString, sourceScope, project, itemKey,
        providerRevision, expectedUri, expectedRef, sha, syntheticSha);
    var replayObject = await storage.GetObjectAsync(bucket, objectName);
    if (firstGeneration != replayObject.Generation)
    {
        throw new InvalidOperationException("GCS generation changed during idempotent replay.");
    }
    Console.WriteLine("PASS: replay preserved one canonical revision, one association and the same GCS generation.");
    testPassed = true;
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAIL ({error.GetType().Name}): {error.Message}");
}
finally
{
    if (wroteOrAttempted)
    {
        // The run uses a random synthetic source and precomputed unique item/object identity.
        // No broad schema, prefix or bucket cleanup; exact keys only.
        try
        {
            await CleanupDatabaseAsync(connectionString, sourceScope, itemKey);
            Console.WriteLine("CLEANUP: temporary ticket-fix and canonical evidence DB rows removed.");
        }
        catch (Exception error)
        {
            cleanupPassed = false;
            Console.Error.WriteLine($"CLEANUP FAILED (PostgreSQL): {error.Message}");
            Console.Error.WriteLine("Retain the console output. Do not manually delete unrelated database records.");
        }

        if (cleanupPassed)
        {
            try
            {
                var storage = StorageClient.Create();
                try
                {
                    var target = await storage.GetObjectAsync(bucket, objectName);
                    // A never-before-existing random object name is expected. Verify bytes
                    // before deletion so only this synthetic upload can be removed.
                    using var download = new MemoryStream();
                    await storage.DownloadObjectAsync(bucket, objectName, download);
                    if (!download.ToArray().SequenceEqual(rawExpected))
                    {
                        throw new InvalidOperationException("Unexpected GCS bytes; refuse automatic deletion.");
                    }
                    await storage.DeleteObjectAsync(bucket, objectName,
                        new DeleteObjectOptions { IfGenerationMatch = target.Generation });
                    Console.WriteLine("CLEANUP: one synthetic GCS object removed using generation precondition.");
                }
                catch (GoogleApiException error) when (error.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    Console.WriteLine("CLEANUP: synthetic GCS object was not created.");
                }
            }
            catch (Exception error)
            {
                cleanupPassed = false;
                Console.Error.WriteLine($"CLEANUP FAILED (GCS): {error.Message}");
                Console.Error.WriteLine("Synthetic object path: " + objectName);
                Console.Error.WriteLine("Use approved cleanup procedures. Do not delete by prefix.");
            }
        }
        else
        {
            Console.Error.WriteLine("CLEANUP: GCS object retained because database cleanup failed. Do not orphan DB references.");
            Console.Error.WriteLine("Synthetic object path: " + objectName);
        }
    }
}

Console.WriteLine($"RESULT: canonical_pipeline={(testPassed ? "PASS" : "FAIL")}; cleanup={(cleanupPassed ? "PASS" : "FAIL")}");
return testPassed && cleanupPassed ? 0 : 1;

static async Task VerifyDatabaseAsync(
    string connectionString, string source, string project, string itemKey,
    string revision, string archiveUri, string expectedRef, string sha, string commitSha)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    await using (var evidenceCommand = new NpgsqlCommand("""
        SELECT i.provider, i.source_instance, i.project_id, i.kind,
               r.provider_revision_id, r.content_sha256, r.raw_snapshot_uri,
               r.redacted_payload::text, r.acl_verified
        FROM evidence.source_items i
        JOIN evidence.source_revisions r ON r.item_key = i.item_key
        WHERE i.item_key = @key
        """, connection))
    {
        evidenceCommand.Parameters.AddWithValue("key", itemKey);
        await using var row = await evidenceCommand.ExecuteReaderAsync();
        if (!await row.ReadAsync() ||
            row.GetString(0) != "azure_devops" || row.GetString(1) != source ||
            row.GetString(2) != project || row.GetString(3) != "pull_request" ||
            row.GetString(4) != revision || row.GetString(5).TrimEnd() != sha ||
            row.GetString(6) != archiveUri || !row.GetBoolean(8))
        {
            throw new InvalidOperationException("Canonical evidence row or provenance is incorrect.");
        }
        var redacted = row.GetString(7);
        if (redacted.Contains("SECRET", StringComparison.OrdinalIgnoreCase) ||
            !redacted.Contains("completed", StringComparison.OrdinalIgnoreCase) ||
            await row.ReadAsync())
        {
            throw new InvalidOperationException("Canonical metadata redaction or revision idempotence failed.");
        }
    }

    await using var linkCommand = new NpgsqlCommand("""
        SELECT ticket_id, pull_request_id, commit_sha, source_classification,
               review_status, source_evidence_refs::text
        FROM evidence.ticket_fix_links
        WHERE source_instance = @source AND project_id = @project
        """, connection);
    linkCommand.Parameters.AddWithValue("source", source);
    linkCommand.Parameters.AddWithValue("project", project);
    await using var linkRows = await linkCommand.ExecuteReaderAsync();
    if (!await linkRows.ReadAsync() ||
        linkRows.GetString(0) != "900002" || linkRows.GetString(1) != "900001" ||
        !string.Equals(linkRows.GetString(2), commitSha, StringComparison.OrdinalIgnoreCase) ||
        linkRows.GetString(3) != "explicit" || !linkRows.IsDBNull(4))
    {
        throw new InvalidOperationException("LRN-04 association has wrong IDs, classification, or review status.");
    }
    using var refs = JsonDocument.Parse(linkRows.GetString(5));
    if (!refs.RootElement.EnumerateArray().Any(x => x.GetString() == expectedRef) ||
        await linkRows.ReadAsync())
    {
        throw new InvalidOperationException("LRN-04 association did not cite the exact canonical revision or was duplicated.");
    }
}

static async Task CleanupDatabaseAsync(string connectionString, string source, string itemKey)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var transaction = await connection.BeginTransactionAsync();

    async Task Delete(string sql, string param, string value)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(param, value);
        await command.ExecuteNonQueryAsync();
    }

    await Delete("""
        DELETE FROM evidence.ticket_fix_reviews
        WHERE association_key IN
          (SELECT association_key FROM evidence.ticket_fix_links WHERE source_instance = @source)
        """, "source", source);
    await Delete("DELETE FROM evidence.ticket_fix_links WHERE source_instance = @source", "source", source);
    await Delete("DELETE FROM evidence.ticket_fix_revert_observations WHERE source_instance = @source", "source", source);
    await Delete("DELETE FROM evidence.source_revisions WHERE item_key = @key", "key", itemKey);
    await Delete("DELETE FROM evidence.source_items WHERE item_key = @key", "key", itemKey);
    await transaction.CommitAsync();

    // Read-only postcondition: this unique run has no remaining scoped link or evidence.
    await using var remaining = new NpgsqlCommand("""
        SELECT
          (SELECT COUNT(*) FROM evidence.ticket_fix_links WHERE source_instance = @source) +
          (SELECT COUNT(*) FROM evidence.ticket_fix_revert_observations WHERE source_instance = @source) +
          (SELECT COUNT(*) FROM evidence.source_items WHERE item_key = @key) +
          (SELECT COUNT(*) FROM evidence.source_revisions WHERE item_key = @key)
        """, connection);
    remaining.Parameters.AddWithValue("source", source);
    remaining.Parameters.AddWithValue("key", itemKey);
    var count = (long)(await remaining.ExecuteScalarAsync() ?? -1L);
    if (count != 0L)
    {
        throw new InvalidOperationException($"Cleanup verification found {count} synthetic scoped rows remaining.");
    }
}
