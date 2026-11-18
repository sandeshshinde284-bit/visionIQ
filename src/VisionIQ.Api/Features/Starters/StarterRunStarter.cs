using Npgsql;
using VisionIQ.Api.Features.Events;

namespace VisionIQ.Api.Features.Starters;

/// <summary>What happened when a starter run was asked for.</summary>
/// <param name="RunId">The new run; null when none was created.</param>
/// <param name="Reason">Why none was created: <see cref="StarterRunStarter.NotEnhancementGuidance"/> or
/// <see cref="StarterRunStarter.AlreadyRequested"/>.</param>
public sealed record StarterStart(Guid? RunId, string? Reason = null);

/// <summary>
/// ENH-02: asks Intelligence for a starter scaffold on the ticket revision and the pinned baseline commit of an
/// enhancement's guidance run. It is the enhancement's counterpart of <c>FixRunStarter</c>: a second run with
/// analysis mode "deep" and ticket kind "enhancement", written with its analysis.requested event in the caller's
/// transaction. There is at most one starter run per guidance run, however often it is asked for: the run takes
/// the guidance run's policy revision plus ".starter" in the one-run-per-pickup index.
/// Defect tickets and runs that were not started by ticket pickup never get a starter run.
/// </summary>
public static class StarterRunStarter
{
    public const string NotEnhancementGuidance = "not_enhancement_guidance";
    public const string AlreadyRequested = "starter_already_requested";
    private const int MaxPolicyRevisionLength = 64;
    private const string Suffix = ".starter";

    public static async Task<StarterStart> StartAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid guidanceRunId, string traceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(traceId);
        NewAnalysisRun? run = null;
        await using (var command = new NpgsqlCommand(
            // The security scope is not a column of the run; it is in the request event the run was created with.
            "SELECT r.tenant_id, r.project_id, r.ticket_id, r.ticket_revision, r.repository_id, r.base_commit_sha, " +
            "       r.trigger_source, r.policy_revision, " +
            "       (SELECT o.payload -> 'data' ->> 'security_scope_id' FROM workflow.event_outbox o " +
            "        WHERE o.event_type = 'analysis.requested' AND o.payload ->> 'run_id' = r.run_id::text LIMIT 1) " +
            "FROM workflow.analysis_runs r " +
            "WHERE r.run_id = $1 AND r.analysis_mode = 'brief' AND r.ticket_kind = 'enhancement' " +
            "  AND r.policy_revision IS NOT NULL",
            connection,
            transaction))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = guidanceRunId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken) && !await reader.IsDBNullAsync(8, cancellationToken))
            {
                run = new NewAnalysisRun(
                    Guid.NewGuid(),
                    reader.GetString(0),
                    new AnalysisRequestedV1(
                        reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4),
                        reader.GetString(5), AnalysisMode.Deep, reader.GetString(8), AnalysisRequestedV1.Enhancement),
                    traceId,
                    await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetString(6),
                    AnalysisRequestedV1.Enhancement,
                    StarterPolicyRevision(reader.GetString(7)));
            }
        }

        if (run is null)
        {
            return new StarterStart(null, NotEnhancementGuidance);
        }

        // Null when a starter run already exists for this guidance run.
        return await AnalysisRuns.CreateAsync(connection, transaction, run, cancellationToken) is null
            ? new StarterStart(null, AlreadyRequested)
            : new StarterStart(run.RunId);
    }

    /// <summary>The starter run's place in the one-run-per-pickup index: the guidance run's policy revision plus ".starter".</summary>
    public static string StarterPolicyRevision(string guidancePolicyRevision)
    {
        ArgumentNullException.ThrowIfNull(guidancePolicyRevision);
        var room = MaxPolicyRevisionLength - Suffix.Length;
        return (guidancePolicyRevision.Length <= room ? guidancePolicyRevision : guidancePolicyRevision[..room]) + Suffix;
    }
}
