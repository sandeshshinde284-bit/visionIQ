using Npgsql;
using VisionIQ.Api.Features.Events;

namespace VisionIQ.Api.Features.Fixes;

/// <summary>
/// DEF-01 trigger (<c>FIX_AUTO_START=true</c>): when the brief of a DEFECT ticket has been answered, ask
/// Intelligence for a fix run on the same ticket revision and the same pinned baseline commit. The new run and its
/// analysis.requested event (mode "deep") are written in the transaction that stores the brief's result, so a
/// brief never loses its fix run and a redelivered result never starts a second one. Enhancement tickets and
/// runs that were not started by ticket pickup get no fix run.
/// </summary>
public static class FixRunStarter
{
    private const int MaxPolicyRevisionLength = 64;
    private const string Suffix = ".fix";

    /// <summary>Returns the new run's id, or null when no fix run applies or one already exists.</summary>
    public static async Task<Guid?> StartAfterBriefAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, EventEnvelope completed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completed);
        NewAnalysisRun? run = null;
        await using (var command = new NpgsqlCommand(
            // The security scope is not a column of the run; it is in the request event the run was created with.
            "SELECT r.tenant_id, r.project_id, r.ticket_id, r.ticket_revision, r.repository_id, r.base_commit_sha, " +
            "       r.trigger_source, r.policy_revision, " +
            "       (SELECT o.payload -> 'data' ->> 'security_scope_id' FROM workflow.event_outbox o " +
            "        WHERE o.event_type = 'analysis.requested' AND o.payload ->> 'run_id' = r.run_id::text LIMIT 1) " +
            "FROM workflow.analysis_runs r " +
            "WHERE r.run_id = $1 AND r.analysis_mode = 'brief' AND r.ticket_kind = 'defect' " +
            "  AND r.policy_revision IS NOT NULL",
            connection,
            transaction))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = completed.RunId!.Value });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken) && !await reader.IsDBNullAsync(8, cancellationToken))
            {
                run = new NewAnalysisRun(
                    Guid.NewGuid(),
                    reader.GetString(0),
                    new AnalysisRequestedV1(
                        reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4),
                        reader.GetString(5), AnalysisMode.Deep, reader.GetString(8)),
                    completed.TraceId,
                    await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetString(6),
                    "defect",
                    FixPolicyRevision(reader.GetString(7)));
            }
        }

        if (run is null)
        {
            return null;
        }

        // Null when a fix run already exists for this pickup: one fix run per ticket revision and policy.
        return await AnalysisRuns.CreateAsync(connection, transaction, run, cancellationToken) is null ? null : run.RunId;
    }

    /// <summary>The fix run's place in the one-run-per-pickup index: the brief's policy revision plus ".fix".</summary>
    public static string FixPolicyRevision(string briefPolicyRevision)
    {
        ArgumentNullException.ThrowIfNull(briefPolicyRevision);
        var room = MaxPolicyRevisionLength - Suffix.Length;
        return (briefPolicyRevision.Length <= room ? briefPolicyRevision : briefPolicyRevision[..room]) + Suffix;
    }
}
