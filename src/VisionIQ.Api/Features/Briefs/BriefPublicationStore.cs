using Npgsql;
using NpgsqlTypes;

namespace VisionIQ.Api.Features.Briefs;

/// <summary>A publication claimed for delivery, with what is needed to render and post it.</summary>
public sealed record PendingPublication(
    BriefTarget Target, int Attempt, DateTimeOffset PickedUpAt, string? BriefJson);

/// <summary>The delivery queue of briefs: one row per run in <c>workflow.brief_publications</c>.</summary>
public interface IBriefPublicationStore
{
    /// <summary>Leases due publications so no other worker delivers them at the same time.</summary>
    Task<IReadOnlyList<PendingPublication>> ClaimAsync(int batchSize, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Marks the run's comment as delivered and returns the pickup-to-comment time.</summary>
    Task<TimeSpan?> MarkPublishedAsync(
        Guid runId, BriefPublishMode mode, string commentId, string commentSha256, string? commentBody,
        CancellationToken cancellationToken);

    Task DeferAsync(Guid runId, TimeSpan delay, CancellationToken cancellationToken);

    Task FailAsync(Guid runId, string reason, CancellationToken cancellationToken);
}

public sealed class PostgresBriefPublicationStore(NpgsqlDataSource dataSource) : IBriefPublicationStore
{
    /// <summary>A run answered only because the pipeline is switched off gets no ticket comment.</summary>
    public const string PipelineDisabled = "analysis_pipeline_disabled";

    /// <summary>
    /// Queues the run's comment in the transaction that stores its result, so a result is never stored without its
    /// publication (or the reverse). The pickup time is fixed here: time-to-brief starts at the pickup (BRF-07).
    /// </summary>
    public static async Task EnqueueAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId, Guid resultId, string resultState,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO workflow.brief_publications (run_id, result_id, result_state, picked_up_at) " +
            "SELECT r.run_id, $2, $3, coalesce(" +
            "  (SELECT min(p.received_at) FROM workflow.ticket_pickups p WHERE p.run_id = r.run_id), r.requested_at) " +
            "FROM workflow.analysis_runs r WHERE r.run_id = $1 ON CONFLICT (run_id) DO NOTHING",
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = runId });
        command.Parameters.Add(new NpgsqlParameter { Value = resultId });
        command.Parameters.Add(new NpgsqlParameter { Value = resultState });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PendingPublication>> ClaimAsync(
        int batchSize, TimeSpan lease, CancellationToken cancellationToken)
    {
        // The brief itself lives in the knowledge schema (written by Intelligence); the API only reads it.
        await using var command = dataSource.CreateCommand(
            "WITH due AS (" +
            "  SELECT run_id FROM workflow.brief_publications " +
            "  WHERE status = 'pending' AND available_at <= now() " +
            "  ORDER BY available_at LIMIT $1 FOR UPDATE SKIP LOCKED), " +
            "claimed AS (" +
            "  UPDATE workflow.brief_publications p " +
            "  SET available_at = now() + $2, attempt_count = p.attempt_count + 1 " +
            "  FROM due WHERE p.run_id = due.run_id " +
            "  RETURNING p.run_id, p.result_id, p.result_state, p.attempt_count, p.picked_up_at) " +
            "SELECT c.run_id, c.result_id, c.result_state, c.attempt_count, c.picked_up_at, " +
            "       r.project_id, r.ticket_id, r.ticket_revision, res.reason_codes, b.document::text " +
            "FROM claimed c " +
            "JOIN workflow.analysis_runs r ON r.run_id = c.run_id " +
            "JOIN workflow.analysis_results res ON res.run_id = c.run_id " +
            "LEFT JOIN knowledge.briefs b ON b.brief_id = c.result_id AND b.run_id = c.run_id");
        command.Parameters.Add(new NpgsqlParameter { Value = batchSize });
        command.Parameters.Add(new NpgsqlParameter { Value = lease });
        var pending = new List<PendingPublication>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            pending.Add(new PendingPublication(
                new BriefTarget(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetString(5), reader.GetString(6), reader.GetInt32(7),
                    reader.GetString(2), reader.GetFieldValue<string[]>(8)),
                reader.GetInt32(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                await reader.IsDBNullAsync(9, cancellationToken) ? null : reader.GetString(9)));
        }

        return pending;
    }

    public async Task<TimeSpan?> MarkPublishedAsync(
        Guid runId, BriefPublishMode mode, string commentId, string commentSha256, string? commentBody,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE workflow.brief_publications SET status = 'published', mode = $2, comment_id = $3, " +
            "comment_sha256 = $4, comment_body = $5, reason = NULL, published_at = now(), " +
            "time_to_brief_ms = greatest(0, (extract(epoch FROM now() - picked_up_at) * 1000)::bigint) " +
            "WHERE run_id = $1 AND status = 'pending' RETURNING time_to_brief_ms");
        command.Parameters.Add(new NpgsqlParameter { Value = runId });
        command.Parameters.Add(new NpgsqlParameter { Value = mode == BriefPublishMode.Ado ? "ado" : "record" });
        command.Parameters.Add(new NpgsqlParameter { Value = commentId });
        command.Parameters.Add(new NpgsqlParameter { Value = commentSha256 });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)commentBody ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        return await command.ExecuteScalarAsync(cancellationToken) is long milliseconds
            ? TimeSpan.FromMilliseconds(milliseconds)
            : null;
    }

    public async Task DeferAsync(Guid runId, TimeSpan delay, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE workflow.brief_publications SET available_at = now() + $2 WHERE run_id = $1 AND status = 'pending'");
        command.Parameters.Add(new NpgsqlParameter { Value = runId });
        command.Parameters.Add(new NpgsqlParameter { Value = delay });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task FailAsync(Guid runId, string reason, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE workflow.brief_publications SET status = 'failed', reason = $2 WHERE run_id = $1 AND status = 'pending'");
        command.Parameters.Add(new NpgsqlParameter { Value = runId });
        command.Parameters.Add(new NpgsqlParameter { Value = reason });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
