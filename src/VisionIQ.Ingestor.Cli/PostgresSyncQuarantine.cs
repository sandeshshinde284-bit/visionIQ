using Npgsql;
using VisionIQ.Api.Configuration;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Cli;

/// <summary>G-1: the scheduled sync's per-item quarantine list in PostgreSQL.</summary>
public sealed class PostgresSyncQuarantine : ISyncQuarantine, IAsyncDisposable
{
    public const string OpenSql = """
        SELECT native_id FROM evidence.sync_quarantine
        WHERE source_id = @source AND scope = @scope AND item_kind = @kind AND resolved_at IS NULL
        """;

    /// <summary>A repeat failure of an open entry counts an attempt; a failure after resolution reopens it.</summary>
    public const string AddSql = """
        INSERT INTO evidence.sync_quarantine (source_id, scope, item_kind, native_id, reason_code, reason,
            attempts, first_failed_at, last_failed_at)
        VALUES (@source, @scope, @kind, @native, @code, @reason, 1, @at, @at)
        ON CONFLICT (source_id, scope, item_kind, native_id) DO UPDATE SET
            reason_code = EXCLUDED.reason_code,
            reason = EXCLUDED.reason,
            attempts = CASE WHEN evidence.sync_quarantine.resolved_at IS NULL
                            THEN evidence.sync_quarantine.attempts + 1 ELSE 1 END,
            first_failed_at = CASE WHEN evidence.sync_quarantine.resolved_at IS NULL
                                   THEN evidence.sync_quarantine.first_failed_at ELSE EXCLUDED.first_failed_at END,
            last_failed_at = GREATEST(evidence.sync_quarantine.last_failed_at, EXCLUDED.last_failed_at),
            resolved_at = NULL
        """;

    // resolved_at IS NULL here matches AddSql's open-entry check: a row already resolved is left alone so a
    // stale resolve (e.g. a retried request) can't clobber a more recent reopen.
    public const string ResolveSql = """
        UPDATE evidence.sync_quarantine SET resolved_at = @at
        WHERE source_id = @source AND scope = @scope AND item_kind = @kind AND native_id = @native
          AND resolved_at IS NULL
        """;

    private readonly NpgsqlDataSource source;

    public PostgresSyncQuarantine(DatabaseOptions options)
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

    public async Task<IReadOnlySet<string>> OpenAsync(string sourceId, string scope, string itemKind, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(OpenSql, connection);
        command.Parameters.AddWithValue("source", sourceId);
        command.Parameters.AddWithValue("scope", scope);
        command.Parameters.AddWithValue("kind", itemKind);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var open = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            open.Add(reader.GetString(0));
        }

        return open;
    }

    public async Task AddAsync(QuarantinedItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(AddSql, connection);
        command.Parameters.AddWithValue("source", item.SourceId);
        command.Parameters.AddWithValue("scope", item.Scope);
        command.Parameters.AddWithValue("kind", item.ItemKind);
        command.Parameters.AddWithValue("native", item.NativeId);
        command.Parameters.AddWithValue("code", item.ReasonCode);
        // Truncate defensively rather than relying on the column's own limit — upstream failure messages
        // (stack traces, raw API errors) are unbounded and we'd rather lose the tail than fail the insert.
        command.Parameters.AddWithValue("reason", item.Reason.Length <= 500 ? item.Reason : item.Reason[..500]);
        command.Parameters.AddWithValue("at", item.ObservedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ResolveAsync(string sourceId, string scope, string itemKind, string nativeId, DateTimeOffset resolvedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(ResolveSql, connection);
        command.Parameters.AddWithValue("source", sourceId);
        command.Parameters.AddWithValue("scope", scope);
        command.Parameters.AddWithValue("kind", itemKind);
        command.Parameters.AddWithValue("native", nativeId);
        command.Parameters.AddWithValue("at", resolvedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => source.DisposeAsync();
}
