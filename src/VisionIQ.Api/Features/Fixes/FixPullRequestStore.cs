using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace VisionIQ.Api.Features.Fixes;

/// <summary>A fix result claimed for delivery, with the stored result it refers to.</summary>
/// <param name="RepositoryId">The repository the API recorded for the run; the stored result must name the same.</param>
/// <param name="BaseSha">The baseline commit the API recorded for the run.</param>
public sealed record PendingFixPullRequest(
    Guid RunId, Guid ResultId, string ProjectId, string TicketId, int Attempt, string? FixJson,
    string? RepositoryId = null, string? BaseSha = null);

/// <summary>A pull request VisionIQ opened: the commit its results apply to and the head last seen on it.</summary>
public sealed record OpenedFixPullRequest(
    Guid RunId, string ProjectId, string RepositoryId, string PullRequestId, string VerifiedHeadSha,
    string CurrentHeadSha, string? FixJson);

/// <summary>What was (or, in record mode, would have been) sent to open the pull request.</summary>
public sealed record FixPullRequestRecord(
    FixPullRequestMode Mode, string RepositoryId, string SourceBranch, PublishedPullRequest PullRequest,
    IReadOnlyList<FixCheck> Checks, string RequestSha256, string? RequestBody);

/// <summary>The delivery queue of fix results: one row per deep run in <c>workflow.fix_pull_requests</c>.</summary>
public interface IFixPullRequestStore
{
    /// <summary>Leases due rows so no other worker delivers them at the same time.</summary>
    Task<IReadOnlyList<PendingFixPullRequest>> ClaimAsync(int batchSize, TimeSpan lease, CancellationToken cancellationToken);

    Task MarkPublishedAsync(Guid runId, FixPullRequestRecord record, CancellationToken cancellationToken);

    /// <summary>The result has no pull request to open (not verified, blocked, needs review).</summary>
    Task SkipAsync(Guid runId, string reason, CancellationToken cancellationToken);

    Task DeferAsync(Guid runId, TimeSpan delay, CancellationToken cancellationToken);

    Task FailAsync(Guid runId, string reason, CancellationToken cancellationToken);

    /// <summary>The pull request VisionIQ opened in one of the named repositories, if any.</summary>
    Task<OpenedFixPullRequest?> FindOpenedAsync(
        string pullRequestId, IReadOnlyList<string> repositoryNames, CancellationToken cancellationToken);

    /// <summary>Records the pull request's new head and the checks that now apply to it (S11-17).</summary>
    Task MarkHeadAsync(Guid runId, string headSha, IReadOnlyList<FixCheck> checks, CancellationToken cancellationToken);
}

public sealed class PostgresFixPullRequestStore(NpgsqlDataSource dataSource) : IFixPullRequestStore
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>
    /// Queues the run's pull request in the transaction that stores its result, so a result is never stored
    /// without its delivery row (or the reverse).
    /// </summary>
    public static async Task EnqueueAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId, Guid resultId, string resultState,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO workflow.fix_pull_requests (run_id, result_id, result_state) VALUES ($1, $2, $3) " +
            "ON CONFLICT (run_id) DO NOTHING",
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = runId });
        command.Parameters.Add(new NpgsqlParameter { Value = resultId });
        command.Parameters.Add(new NpgsqlParameter { Value = resultState });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PendingFixPullRequest>> ClaimAsync(
        int batchSize, TimeSpan lease, CancellationToken cancellationToken)
    {
        // The fix result itself lives in the knowledge schema (written by Intelligence); the API only reads it.
        await using var command = dataSource.CreateCommand(
            "WITH due AS (" +
            "  SELECT run_id FROM workflow.fix_pull_requests " +
            "  WHERE status = 'pending' AND available_at <= now() " +
            "  ORDER BY available_at LIMIT $1 FOR UPDATE SKIP LOCKED), " +
            "claimed AS (" +
            "  UPDATE workflow.fix_pull_requests p " +
            "  SET available_at = now() + $2, attempt_count = p.attempt_count + 1 " +
            "  FROM due WHERE p.run_id = due.run_id " +
            "  RETURNING p.run_id, p.result_id, p.attempt_count) " +
            "SELECT c.run_id, c.result_id, c.attempt_count, r.project_id, r.ticket_id, f.document::text, " +
            "       r.repository_id, r.base_commit_sha " +
            "FROM claimed c " +
            "JOIN workflow.analysis_runs r ON r.run_id = c.run_id " +
            "LEFT JOIN knowledge.fix_runs f ON f.fix_id = c.result_id AND f.run_id = c.run_id");
        command.Parameters.Add(new NpgsqlParameter { Value = batchSize });
        command.Parameters.Add(new NpgsqlParameter { Value = lease });
        var pending = new List<PendingFixPullRequest>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            pending.Add(new PendingFixPullRequest(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(3), reader.GetString(4), reader.GetInt32(2),
                await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7)));
        }

        return pending;
    }

    public async Task MarkPublishedAsync(Guid runId, FixPullRequestRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var command = dataSource.CreateCommand(
            "UPDATE workflow.fix_pull_requests SET status = 'published', mode = $2, repository_id = $3, " +
            "pull_request_id = $4, source_branch = $5, verified_head_sha = $6, current_head_sha = $6, checks = $7, " +
            "request_sha256 = $8, request_body = $9, reason = NULL, published_at = now() " +
            "WHERE run_id = $1 AND status = 'pending'");
        command.Parameters.Add(new NpgsqlParameter { Value = runId });
        command.Parameters.Add(new NpgsqlParameter { Value = record.Mode == FixPullRequestMode.Ado ? "ado" : "record" });
        command.Parameters.Add(new NpgsqlParameter { Value = record.RepositoryId });
        command.Parameters.Add(new NpgsqlParameter { Value = record.PullRequest.PullRequestId });
        command.Parameters.Add(new NpgsqlParameter { Value = record.SourceBranch });
        command.Parameters.Add(new NpgsqlParameter { Value = record.PullRequest.HeadSha });
        command.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(record.Checks, Json), NpgsqlDbType = NpgsqlDbType.Jsonb });
        command.Parameters.Add(new NpgsqlParameter { Value = record.RequestSha256 });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)record.RequestBody ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task SkipAsync(Guid runId, string reason, CancellationToken cancellationToken) =>
        EndAsync(runId, "skipped", reason, cancellationToken);

    public Task FailAsync(Guid runId, string reason, CancellationToken cancellationToken) =>
        EndAsync(runId, "failed", reason, cancellationToken);

    public async Task DeferAsync(Guid runId, TimeSpan delay, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE workflow.fix_pull_requests SET available_at = now() + $2 WHERE run_id = $1 AND status = 'pending'");
        command.Parameters.Add(new NpgsqlParameter { Value = runId });
        command.Parameters.Add(new NpgsqlParameter { Value = delay });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<OpenedFixPullRequest?> FindOpenedAsync(
        string pullRequestId, IReadOnlyList<string> repositoryNames, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT p.run_id, r.project_id, p.repository_id, p.pull_request_id, p.verified_head_sha, " +
            "       p.current_head_sha, f.document::text " +
            "FROM workflow.fix_pull_requests p " +
            "JOIN workflow.analysis_runs r ON r.run_id = p.run_id " +
            "LEFT JOIN knowledge.fix_runs f ON f.fix_id = p.result_id AND f.run_id = p.run_id " +
            "WHERE p.status = 'published' AND p.mode = 'ado' AND p.pull_request_id = $1 AND p.repository_id = ANY($2) " +
            "ORDER BY p.published_at DESC LIMIT 1");
        command.Parameters.Add(new NpgsqlParameter { Value = pullRequestId });
        command.Parameters.Add(new NpgsqlParameter { Value = repositoryNames.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new OpenedFixPullRequest(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetString(6));
    }

    public async Task MarkHeadAsync(
        Guid runId, string headSha, IReadOnlyList<FixCheck> checks, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE workflow.fix_pull_requests SET current_head_sha = $2, checks = $3, head_changed_at = now() " +
            "WHERE run_id = $1 AND status = 'published'");
        command.Parameters.Add(new NpgsqlParameter { Value = runId });
        command.Parameters.Add(new NpgsqlParameter { Value = headSha });
        command.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(checks, Json), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EndAsync(Guid runId, string status, string reason, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE workflow.fix_pull_requests SET status = $2, reason = $3 WHERE run_id = $1 AND status = 'pending'");
        command.Parameters.Add(new NpgsqlParameter { Value = runId });
        command.Parameters.Add(new NpgsqlParameter { Value = status });
        command.Parameters.Add(new NpgsqlParameter { Value = reason });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
