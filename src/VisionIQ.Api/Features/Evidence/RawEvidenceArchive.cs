using System.Security.Cryptography;
using Google;
using Google.Cloud.Storage.V1;

namespace VisionIQ.Api.Features.Evidence;

public interface IRawEvidenceArchive
{
    Task<string> StoreAsync(
        string itemKey,
        string revisionId,
        byte[] rawJson,
        string contentSha256,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Uses the runtime's Google Cloud identity. No key files or secret tokens
/// are expected in the repository. Object names are content-addressed.
/// </summary>
public sealed class CloudStorageEvidenceArchive(string? bucketName) : IRawEvidenceArchive
{
    // M-5: one thread-safe client per archive instead of credential discovery on every upload.
    private readonly Lazy<StorageClient> Client = new(StorageClient.Create, LazyThreadSafetyMode.ExecutionAndPublication);

    public async Task<string> StoreAsync(
        string itemKey,
        string revisionId,
        byte[] rawJson,
        string contentSha256,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucketName))
        {
            throw new InvalidOperationException(
                "GCS_EVIDENCE_BUCKET must be configured before importing evidence.");
        }

        // Our archive strategy: the object name is derived from content (SHA256 of the raw JSON).
        // This means identical evidence always maps to the same file, regardless of when or
        // how many times we try to store it. We also incorporate the revision ID as a hash
        // in the path for namespace separation and easier debugging.
        var revisionHash = Convert.ToHexStringLower(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(revisionId)));
        var name = $"raw/v1/{itemKey}/{revisionHash}/{contentSha256}.json";
        using var stream = new MemoryStream(rawJson, writable: false);
        var client = Client.Value;

        try
        {
            // IfGenerationMatch = 0 means "only upload if this object does NOT exist yet".
            // This prevents accidental overwrites and is safe for idempotent replays:
            // if the object already exists, we're done (conflict = success for immutable data).
            await client.UploadObjectAsync(
                bucketName,
                name,
                "application/json",
                stream,
                new UploadObjectOptions { IfGenerationMatch = 0 },
                cancellationToken: cancellationToken);
        }
        catch (GoogleApiException exception)
            when (exception.HttpStatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            // The immutable content-addressed object already exists. A distinct
            // content hash gets a different object name, never an overwrite.
            // This is expected on replays and retries.
        }

        return $"gs://{bucketName}/{name}";
    }
}
