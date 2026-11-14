using Npgsql;
using NpgsqlTypes;
using VisionIQ.Api.Configuration;

namespace VisionIQ.Api.Features.TestLinks;

/// <summary>
/// One work item's current declared test associations (ADO "Tested By") and, for a Test Case, its automated
/// test identity, as stated by one source revision. A declared association is NOT proof that a test passed,
/// covers the affected code, or prevents recurrence.
/// </summary>
public sealed record TestLinkSnapshot(
    string SourceInstance,
    string ProjectId,
    string WorkItemId,
    IReadOnlyCollection<string> TestCaseIds,
    string? AutomatedTestName,
    string? AutomatedTestStorage,
    string EvidenceRef,
    DateTimeOffset SourceAssertedAt);

public interface ITestLinkStore
{
    /// <summary>
    /// Replaces the work item's current test links with the snapshot's. An older snapshot never removes or
    /// overwrites what a newer source revision stated.
    /// </summary>
    Task ReplaceAsync(TestLinkSnapshot snapshot, CancellationToken cancellationToken = default);
}

public sealed class PostgresTestLinkStore : ITestLinkStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource source;

    public PostgresTestLinkStore(DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host,
            Port = options.Port,
            Database = options.Name,
            Username = options.User,
            Password = options.Password,
            Timeout = 10,
        };
        builder["SSL Mode"] = options.SslMode;
        source = NpgsqlDataSource.Create(builder.ConnectionString);
    }

    public async Task ReplaceAsync(TestLinkSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Apply only a snapshot at least as new as the newest one already applied for this work item;
        // otherwise an older replay could resurrect a link that a newer revision removed.
        await using (var guard = new NpgsqlCommand("""
            INSERT INTO evidence.test_link_sources (source_instance, project_id, work_item_id, source_asserted_at)
            VALUES (@source, @project, @item, @at)
            ON CONFLICT (source_instance, project_id, work_item_id) DO UPDATE SET
                source_asserted_at = EXCLUDED.source_asserted_at
            WHERE evidence.test_link_sources.source_asserted_at <= EXCLUDED.source_asserted_at
            RETURNING 1
            """, connection, transaction))
        {
            AddKey(guard, snapshot);
            if (await guard.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return;
            }
        }

        // Links no longer stated by this (newer or equal) revision are removed; newer rows are never touched.
        await using (var delete = new NpgsqlCommand("""
            DELETE FROM evidence.tested_by_links
            WHERE source_instance = @source AND project_id = @project AND work_item_id = @item
              AND NOT (test_case_id = ANY(@ids)) AND source_asserted_at <= @at
            """, connection, transaction))
        {
            AddKey(delete, snapshot);
            delete.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = snapshot.TestCaseIds.ToArray() });
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var testCase in snapshot.TestCaseIds.Distinct(StringComparer.Ordinal))
        {
            await using var upsert = new NpgsqlCommand("""
                INSERT INTO evidence.tested_by_links
                    (source_instance, project_id, work_item_id, test_case_id, evidence_ref, source_asserted_at)
                VALUES (@source, @project, @item, @test, @ref, @at)
                ON CONFLICT (source_instance, project_id, work_item_id, test_case_id) DO UPDATE SET
                    evidence_ref = EXCLUDED.evidence_ref,
                    source_asserted_at = EXCLUDED.source_asserted_at,
                    observed_at = now()
                WHERE evidence.tested_by_links.source_asserted_at <= EXCLUDED.source_asserted_at
                """, connection, transaction);
            AddKey(upsert, snapshot);
            upsert.Parameters.AddWithValue("test", testCase);
            upsert.Parameters.AddWithValue("ref", snapshot.EvidenceRef);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var automation = new NpgsqlCommand(snapshot.AutomatedTestName is null
            ? """
              DELETE FROM evidence.test_case_automation
              WHERE source_instance = @source AND project_id = @project AND test_case_id = @item
                AND source_asserted_at <= @at
              """
            : """
              INSERT INTO evidence.test_case_automation
                  (source_instance, project_id, test_case_id, automated_test_name,
                   automated_test_storage, evidence_ref, source_asserted_at)
              VALUES (@source, @project, @item, @name, @storage, @ref, @at)
              ON CONFLICT (source_instance, project_id, test_case_id) DO UPDATE SET
                  automated_test_name = EXCLUDED.automated_test_name,
                  automated_test_storage = EXCLUDED.automated_test_storage,
                  evidence_ref = EXCLUDED.evidence_ref,
                  source_asserted_at = EXCLUDED.source_asserted_at,
                  observed_at = now()
              WHERE evidence.test_case_automation.source_asserted_at <= EXCLUDED.source_asserted_at
              """, connection, transaction))
        {
            AddKey(automation, snapshot);
            if (snapshot.AutomatedTestName is { } name)
            {
                automation.Parameters.AddWithValue("name", name.Trim());
                automation.Parameters.Add(new NpgsqlParameter("storage", NpgsqlDbType.Text) { Value = (object?)snapshot.AutomatedTestStorage ?? DBNull.Value });
                automation.Parameters.AddWithValue("ref", snapshot.EvidenceRef);
            }

            await automation.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static void AddKey(NpgsqlCommand command, TestLinkSnapshot snapshot)
    {
        command.Parameters.AddWithValue("source", snapshot.SourceInstance);
        command.Parameters.AddWithValue("project", snapshot.ProjectId);
        command.Parameters.AddWithValue("item", snapshot.WorkItemId);
        command.Parameters.AddWithValue("at", snapshot.SourceAssertedAt);
    }

    public ValueTask DisposeAsync() => source.DisposeAsync();
}
