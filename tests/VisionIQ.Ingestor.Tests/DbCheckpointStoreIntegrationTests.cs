using Npgsql;
using VisionIQ.Ingestor.Checkpoints;

namespace VisionIQ.Ingestor.Tests;

/// <summary>
/// Opt-in PostgreSQL integration test for the real <see cref="DbCheckpointStore"/> (LRN-01's
/// monotonic checkpoint guard). Run only against a disposable database, never the shared
/// development or cloud database. Writes go through the restricted API role, matching production.
/// </summary>
public sealed class DbCheckpointStoreIntegrationTests
{
    private static (string Host, int Port, string Db, string User, string Password) ApiCredentials()
    {
        var password = Environment.GetEnvironmentVariable("DB_API_PASSWORD");
        Assert.False(string.IsNullOrWhiteSpace(password));
        return (
            Environment.GetEnvironmentVariable("DB_HOST") ?? "127.0.0.1",
            int.TryParse(Environment.GetEnvironmentVariable("DB_PORT"), out var port) ? port : 5432,
            Environment.GetEnvironmentVariable("DB_NAME") ?? "visioniq",
            Environment.GetEnvironmentVariable("DB_API_USER") ?? "visioniq_api",
            password!);
    }

    private static DbCheckpointStore CreateStore((string Host, int Port, string Db, string User, string Password) c)
    {
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = c.Host, Port = c.Port, Database = c.Db, Username = c.User, Password = c.Password,
        }.ConnectionString;
        return new DbCheckpointStore(() => new NpgsqlConnection(connectionString));
    }

    private static async Task CleanupAsync((string Host, int Port, string Db, string User, string Password) c, string sourceId)
    {
        // Cleanup uses the admin role: deleting test rows is not something the restricted API
        // role needs to do in production, so it is not exercised as the API role here either.
        var adminPassword = Environment.GetEnvironmentVariable("DB_LOCAL_PASSWORD");
        Assert.False(string.IsNullOrWhiteSpace(adminPassword));
        await using var db = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
        {
            Host = c.Host,
            Port = c.Port,
            Database = c.Db,
            Username = Environment.GetEnvironmentVariable("DB_LOCAL_USER") ?? "visioniq",
            Password = adminPassword,
        }.ConnectionString);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM evidence.connector_checkpoints WHERE source_id = @source_id", db);
        cmd.Parameters.AddWithValue("source_id", sourceId);
        await cmd.ExecuteNonQueryAsync();
    }

    [DatabaseFact("VISIONIQ_CHECKPOINT_DB_INTEGRATION")]
    public async Task Save_then_advance_then_reject_a_backward_write_and_keep_the_furthest_value()
    {
        var credentials = ApiCredentials();
        var sourceId = "lrn01-checkpoint-" + Guid.NewGuid().ToString("N");
        const string scope = "work-items";
        var store = CreateStore(credentials);

        try
        {
            // 1. Initial save.
            await store.SaveAsync(new SyncCheckpoint(sourceId, scope, "000100", DateTimeOffset.UtcNow));
            Assert.Equal("000100", (await store.GetAsync(sourceId, scope))!.Cursor);

            // 2. Forward advancement.
            await store.SaveAsync(new SyncCheckpoint(sourceId, scope, "000200", DateTimeOffset.UtcNow));
            Assert.Equal("000200", (await store.GetAsync(sourceId, scope))!.Cursor);

            // 3. Attempt to overwrite 000200 with 000150 (backward) must be rejected loudly.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.SaveAsync(new SyncCheckpoint(sourceId, scope, "000150", DateTimeOffset.UtcNow)));

            // 4. Read after rejection: still 000200, not silently regressed.
            Assert.Equal("000200", (await store.GetAsync(sourceId, scope))!.Cursor);
        }
        finally
        {
            await CleanupAsync(credentials, sourceId);
        }
    }

    [DatabaseFact("VISIONIQ_CHECKPOINT_DB_INTEGRATION")]
    public async Task Equal_cursor_write_succeeds_idempotently_matching_the_documented_contract()
    {
        // UpsertSql's WHERE guard is "cursor_value <= EXCLUDED.cursor_value": an equal-value
        // re-save is NOT a regression, so it is accepted (affected = 1), not rejected. This is
        // the correct behavior for a retried run that made no further progress since its last
        // checkpoint save -- rejecting it would turn a harmless retry into a loud, false failure.
        // Confirmed here against the real SQL rather than assumed from the comment alone.
        var credentials = ApiCredentials();
        var sourceId = "lrn01-checkpoint-" + Guid.NewGuid().ToString("N");
        const string scope = "pull-requests";
        var store = CreateStore(credentials);

        try
        {
            var firstSave = DateTimeOffset.UtcNow;
            await store.SaveAsync(new SyncCheckpoint(sourceId, scope, "000200", firstSave));

            var secondSave = firstSave.AddSeconds(5);
            await store.SaveAsync(new SyncCheckpoint(sourceId, scope, "000200", secondSave));

            var persisted = await store.GetAsync(sourceId, scope);
            Assert.Equal("000200", persisted!.Cursor);
            // updated_at still advances on an equal-cursor re-save: it is a real write, not a no-op.
            Assert.Equal(secondSave, persisted.UpdatedAt, TimeSpan.FromSeconds(1));
        }
        finally
        {
            await CleanupAsync(credentials, sourceId);
        }
    }

    [DatabaseFact("VISIONIQ_CHECKPOINT_DB_INTEGRATION")]
    public async Task Concurrent_saves_never_let_the_checkpoint_regress()
    {
        var credentials = ApiCredentials();
        var sourceId = "lrn01-checkpoint-" + Guid.NewGuid().ToString("N");
        const string scope = "commits";
        var store = CreateStore(credentials);

        try
        {
            await store.SaveAsync(new SyncCheckpoint(sourceId, scope, "000100", DateTimeOffset.UtcNow));

            // Fire several overlapping saves -- as a real scheduled sync firing again before the
            // previous run finished would -- at different cursor values, including some BEHIND
            // the baseline already saved above.
            var candidateCursors = new[] { "000050", "000300", "000150", "000500", "000090", "000400" };
            var tasks = candidateCursors.Select(cursor => Task.Run(async () =>
            {
                try
                {
                    await store.SaveAsync(new SyncCheckpoint(sourceId, scope, cursor, DateTimeOffset.UtcNow));
                    return (Cursor: cursor, Rejected: false);
                }
                catch (InvalidOperationException)
                {
                    return (Cursor: cursor, Rejected: true);
                }
            })).ToArray();

            var outcomes = await Task.WhenAll(tasks);

            var finalCursor = (await store.GetAsync(sourceId, scope))!.Cursor;
            // Regardless of execution order, the database-level WHERE guard converges the final
            // state to the highest value anyone submitted -- the cursor never regresses below it.
            var highestSubmitted = candidateCursors.Append("000100").Max();
            Assert.Equal(highestSubmitted, finalCursor);

            // At least one of the lower candidates must actually have been rejected (not just
            // superseded later): proves the guard fired for real, not that every write happened
            // to arrive in already-increasing order.
            Assert.Contains(outcomes, o => o.Rejected);
        }
        finally
        {
            await CleanupAsync(credentials, sourceId);
        }
    }
}
