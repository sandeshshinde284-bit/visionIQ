using Npgsql;
using VisionIQ.Api.Configuration;

namespace VisionIQ.Api.Features.Backfill;

/// <summary>Allowed synchronization modes for audited source runs.</summary>
public enum SourceSyncMode
{
    Full,
    Delta,
    Reconcile,
}

/// <summary>Never represent an unreachable source as an empty successful result.</summary>
public enum SourceSyncState
{
    Synced,
    Partial,
    NotSynced,
}

/// <summary>
/// Sanitized durable metrics for a single source stream; no source title,
/// description, user identity or raw payload is persisted in this audit.
/// </summary>
public sealed record SourceSyncAudit(
    Guid RunId,
    string SourceId,
    SourceSyncMode Mode,
    SourceSyncState Status,
    int RecordsReceived,
    int RecordsStored,
    int RecordsQuarantined,
    int? ExpectedCount,
    string? Cursor,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt)
{
    /// <summary>Rejects invalid counts, times and unbounded source identifiers.</summary>
    public void Validate()
    {
        if (RunId == Guid.Empty || string.IsNullOrWhiteSpace(SourceId) ||
            SourceId.Length > 256 || RecordsReceived < 0 || RecordsStored < 0 ||
            RecordsQuarantined < 0 || ExpectedCount < 0 ||
            StartedAt > CompletedAt || Cursor is { Length: > 4096 })
        {
            throw new ArgumentException("Source synchronization audit is invalid.");
        }
    }
}

/// <summary>Provides durable per-stream synchronization reports for operational reconciliation.</summary>
public interface ISourceSyncRunStore
{
    /// <summary>Saves one source stream result; replaying the run ID is idempotent.</summary>
    Task RecordAsync(SourceSyncAudit audit, CancellationToken cancellationToken = default);
}

/// <summary>
/// Writes source sync reports to the existing evidence.source_sync_runs table
/// under the restricted API identity, using the FND-03 Alembic-owned schema.
/// </summary>
public sealed class PostgresSourceSyncRunStore : ISourceSyncRunStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource source;

    /// <summary>Creates a pooled PostgreSQL client with the limited API database role.</summary>
    public PostgresSourceSyncRunStore(DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host, Port = options.Port, Database = options.Name,
            Username = options.User, Password = options.Password, Timeout = 10,
        };
        builder["SSL Mode"] = options.SslMode;
        source = NpgsqlDataSource.Create(builder.ConnectionString);
    }

    /// <summary>
    /// Records status and counts without untrusted free text. A duplicate
    /// correlation ID is ignored only if the first transaction committed.
    /// </summary>
    public async Task RecordAsync(
        SourceSyncAudit audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        audit.Validate();
        const string sql = """
            INSERT INTO evidence.source_sync_runs (
                run_id, source_id, mode, status, records_received,
                records_stored, records_quarantined, expected_count,
                result_cursor, started_at, completed_at
            ) VALUES (
                @id, @source, @mode, @status, @received,
                @stored, @quarantined, @expected, @cursor,
                @started, @completed
            )
            ON CONFLICT (run_id) DO NOTHING
            """;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", audit.RunId);
        command.Parameters.AddWithValue("source", audit.SourceId);
        command.Parameters.AddWithValue("mode", audit.Mode.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("status", audit.Status switch
        {
            SourceSyncState.Synced => "synced",
            SourceSyncState.Partial => "partial",
            SourceSyncState.NotSynced => "not_synced",
            _ => throw new ArgumentOutOfRangeException(nameof(audit), "Unrecognized sync status."),
        });
        command.Parameters.AddWithValue("received", audit.RecordsReceived);
        command.Parameters.AddWithValue("stored", audit.RecordsStored);
        command.Parameters.AddWithValue("quarantined", audit.RecordsQuarantined);
        command.Parameters.AddWithValue("expected", (object?)audit.ExpectedCount ?? DBNull.Value);
        command.Parameters.AddWithValue("cursor", (object?)audit.Cursor ?? DBNull.Value);
        command.Parameters.AddWithValue("started", audit.StartedAt.ToUniversalTime());
        command.Parameters.AddWithValue("completed", audit.CompletedAt.ToUniversalTime());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Closes the pooled Npgsql data source.</summary>
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
