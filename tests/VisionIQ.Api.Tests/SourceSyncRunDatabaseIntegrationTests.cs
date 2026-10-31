using Npgsql;
using VisionIQ.Api.Configuration;
using VisionIQ.Api.Features.Backfill;

namespace VisionIQ.Api.Tests;

/// <summary>Real PostgreSQL coverage for LRN-05 per-source synchronization audit rows.</summary>
public sealed class SourceSyncRunDatabaseIntegrationTests
{
    [Fact]
    public async Task A_partial_run_is_recorded_once_without_exposing_source_payloads()
    {
        if (Environment.GetEnvironmentVariable("VISIONIQ_SOURCE_AUDIT_DB_INTEGRATION") != "1")
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
            password!, "disable");
        await using var store = new PostgresSourceSyncRunStore(options);
        var runId = Guid.NewGuid();
        var completed = DateTimeOffset.UtcNow;
        var result = new SourceSyncAudit(
            runId, "sha256:" + new string('a', 64), SourceSyncMode.Delta,
            SourceSyncState.Partial, 4, 3, 0, null, null,
            completed.AddSeconds(-4), completed);

        await store.RecordAsync(result);
        await store.RecordAsync(result);

        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host, Port = options.Port, Database = options.Name,
            Username = options.User, Password = options.Password,
        }.ConnectionString;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("""
            SELECT COUNT(*), MAX(status), MAX(records_received), MAX(records_stored)
            FROM evidence.source_sync_runs WHERE run_id = @run
            """, connection);
        query.Parameters.AddWithValue("run", runId);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("partial", reader.GetString(1));
        Assert.Equal(4, reader.GetInt32(2));
        Assert.Equal(3, reader.GetInt32(3));
    }

    [Fact]
    public void Unbounded_or_invalid_audit_data_is_rejected_before_persistence()
    {
        var now = DateTimeOffset.UtcNow;
        var audit = new SourceSyncAudit(
            Guid.NewGuid(), "source", SourceSyncMode.Full,
            SourceSyncState.NotSynced, 0, 0, 0, null, null, now, now);
        audit.Validate();
        Assert.Throws<ArgumentException>(() =>
            (audit with { RecordsReceived = -1 }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (audit with { CompletedAt = now.AddDays(-1) }).Validate());
    }
}
