using System.Security.Cryptography;

namespace VisionIQ.Api.Features.Evidence;

/// <summary>
/// Handles one immutable source revision. No public HTTP endpoint is exposed:
/// only a trusted source connector may call this application service.
/// </summary>
public sealed class EvidenceIngestionService(
    IRawEvidenceArchive archive,
    IVersionedEvidenceStore store)
{
    public async Task<EvidenceImportResult> ImportAsync(
        EvidenceEnvelope input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Identity);
        ArgumentNullException.ThrowIfNull(input.Access);

        var key = input.Identity.ItemKey();
        if (string.IsNullOrWhiteSpace(input.ProviderRevisionId) ||
            string.IsNullOrWhiteSpace(input.RetrievedUnderPrincipal) ||
            string.IsNullOrWhiteSpace(input.SourceUrl) ||
            !Uri.TryCreate(input.SourceUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                "Evidence requires native revision, authenticated source, and HTTPS source URL.");
        }

        if (input.RawJson is not { Length: > 0 and <= 10_485_760 })
        {
            throw new ArgumentException("Evidence raw JSON must be between 1 byte and 10 MiB.");
        }

        // Npgsql timestamptz accepts UTC. Source time and observation time are
        // separate and must never be inferred from one another.
        var utc = input with
        {
            SourceUpdatedAt = input.SourceUpdatedAt.ToUniversalTime(),
            ObservedAt = input.ObservedAt.ToUniversalTime(),
        };

        // Only explicit conservative metadata fields are stored for downstream
        // indexing. Unknown fields and free-text descriptions are excluded.
        var redacted = EvidenceNormalizer.Redact(utc.Identity.Kind, utc.RawJson);
        var sha = Convert.ToHexStringLower(SHA256.HashData(utc.RawJson));

        // Archive first. If Cloud Storage fails, no database revision is written.
        // Object names are content-addressed; a DB failure leaves a safe,
        // unreferenced object for later lifecycle cleanup/retry.
        var archiveUri = await archive.StoreAsync(
            key, utc.ProviderRevisionId, utc.RawJson, sha, cancellationToken);

        if (!archiveUri.StartsWith("gs://", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Archive must return a Cloud Storage object URI.");
        }

        return await store.SaveAsync(
            new ArchivedEvidence(
                utc,
                key,
                sha,
                archiveUri,
                redacted,
                EvidenceNormalizer.Version,
                EvidenceNormalizer.Version),
            cancellationToken);
    }
}
