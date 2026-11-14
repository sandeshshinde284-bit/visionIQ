using System.Security.Cryptography;
using System.Text;
using Google.Cloud.Storage.V1;
using VisionIQ.Api.Features.Evidence;

namespace VisionIQ.Ingestor.Tests;

/// <summary>
/// Opt-in real GCS integration. Requires LRN03_TEST_GCS=1 and GCS_EVIDENCE_BUCKET.
/// Uses only a unique synthetic object and removes only that object on success.
/// Does not use the production ingestion data or PostgreSQL.
/// </summary>
public sealed class Lrn03GcsArchiveIntegrationTests
{
    // Reported as SKIPPED unless LRN03_TEST_GCS=1 (previously it threw and failed every plain run).
    [OptInFact("LRN03_TEST_GCS")]
    public async Task Real_cloud_storage_archive_is_immutable_idempotent_and_exact()
    {
        var bucket = Environment.GetEnvironmentVariable("GCS_EVIDENCE_BUCKET");
        if (string.IsNullOrWhiteSpace(bucket))
        {
            throw new InvalidOperationException("GCS_EVIDENCE_BUCKET must be set to an approved isolated test bucket.");
        }

        var itemKey = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes("lrn03-integration-" + Guid.NewGuid().ToString("N"))));
        const string revisionId = "lrn03-synthetic-revision-1";
        var raw = Encoding.UTF8.GetBytes("""{"state":"Closed","description":"SYNTHETIC-NONPUBLIC-DO-NOT-INDEX"}""");
        var sha = Convert.ToHexStringLower(SHA256.HashData(raw));
        var archive = new CloudStorageEvidenceArchive(bucket);
        var client = StorageClient.Create();
        string? objectName = null;
        long? firstGeneration = null;
        try
        {
            var uri = await archive.StoreAsync(itemKey, revisionId, raw, sha);
            var prefix = "gs://" + bucket + "/";
            Assert.StartsWith(prefix, uri, StringComparison.Ordinal);
            objectName = uri[prefix.Length..];
            Assert.Contains("/" + sha + ".json", objectName, StringComparison.Ordinal);

            var first = await client.GetObjectAsync(bucket, objectName);
            firstGeneration = first.Generation;
            using (var downloaded = new MemoryStream())
            {
                await client.DownloadObjectAsync(bucket, objectName, downloaded);
                Assert.Equal(raw, downloaded.ToArray());
                Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(downloaded.ToArray())));
            }

            var secondUri = await archive.StoreAsync(itemKey, revisionId, raw, sha);
            Assert.Equal(uri, secondUri);
            var second = await client.GetObjectAsync(bucket, objectName);
            Assert.Equal(firstGeneration, second.Generation);

            // A different content hash must be a different object name; never overwrite first.
            // Do not intentionally upload a second object here: this integration test keeps
            // its cloud footprint to exactly one disposable object.
        }
        finally
        {
            if (objectName is not null && firstGeneration.HasValue)
            {
                await client.DeleteObjectAsync(bucket, objectName, new DeleteObjectOptions
                {
                    IfGenerationMatch = firstGeneration.Value
                });
            }
        }
    }
}
