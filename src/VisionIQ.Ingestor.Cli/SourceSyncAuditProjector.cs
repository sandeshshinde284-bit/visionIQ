using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using VisionIQ.Api.Features.Backfill;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Cli;

/// <summary>
/// Converts actual LRN-01/02 synchronization outcomes into durable LRN-05
/// audit rows. Provider project names are hashed, not copied into audit keys.
/// </summary>
public static class SourceSyncAuditProjector
{
    /// <summary>
    /// Builds a sanitized row for each attempted stream; no successful source
    /// records are inferred when the provider returned NOT_SYNCED.
    /// </summary>
    public static IReadOnlyList<SourceSyncAudit> Project(
        SyncReport report, SourceSyncMode mode)
    {
        ArgumentNullException.ThrowIfNull(report);
        var finish = report.FinishedAt ?? DateTimeOffset.UtcNow;
        return report.Streams.Select(stream =>
            new SourceSyncAudit(
                Guid.NewGuid(),
                StableSourceId(report.Tenant, stream.Stream.ToString(), stream.Scope),
                mode,
                stream.Status switch
                {
                    StreamStatus.Synced => SourceSyncState.Synced,
                    StreamStatus.Partial => SourceSyncState.Partial,
                    StreamStatus.NotSynced => SourceSyncState.NotSynced,
                    _ => throw new ArgumentOutOfRangeException(nameof(report)),
                },
                stream.Records,
                stream.Records,
                0,
                null,
                null,
                report.StartedAt,
                finish)).ToArray();
    }

    /// <summary>
    /// Generates a collision-resistant per-stream identity using length-prefixed
    /// UTF-8 values; avoids exposing private project or space names in metrics.
    /// </summary>
    public static string StableSourceId(
        string tenant, string stream, string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        using var bytes = new MemoryStream();
        foreach (var value in new[] { tenant, stream, scope })
        {
            var encoded = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, encoded.Length);
            bytes.Write(length);
            bytes.Write(encoded);
        }

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }

    /// <summary>
    /// Stores all attempted stream outcomes in the existing PostgreSQL audit table.
    /// A failed write is propagated rather than pretending reconciliation succeeded.
    /// </summary>
    public static async Task SaveAsync(
        SyncReport report,
        SourceSyncMode mode,
        ISourceSyncRunStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        foreach (var audit in Project(report, mode))
        {
            await store.RecordAsync(audit, cancellationToken).ConfigureAwait(false);
        }
    }
}
