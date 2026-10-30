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

        var revisionHash = Convert.ToHexStringLower(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(revisionId)));
        var name = $"raw/v1/{itemKey}/{revisionHash}/{contentSha256}.json";
        using var stream = new MemoryStream(rawJson, writable: false);
        var client = StorageClient.Create();

        try
        {
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
        }

        return $"gs://{bucketName}/{name}";
    }
}
