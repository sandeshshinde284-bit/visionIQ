using Npgsql;

namespace VisionIQ.Api.Features.Events;

/// <summary>A run to create; the pickup fields stay null for runs that are not started by ticket pickup.</summary>
public sealed record NewAnalysisRun(
    Guid RunId,
    string TenantId,
    AnalysisRequestedV1 Data,
    string TraceId,
    string? TriggerSource = null,
    string? TicketKind = null,
    string? PolicyRevision = null);

/// <summary>Creates <c>workflow.analysis_runs</c> rows together with their analysis.requested event (outbox).</summary>
public static class AnalysisRuns
{
    /// <summary>
    /// Inserts the run and enqueues its analysis.requested event in the caller's transaction. Returns null, and
    /// writes nothing, when a run already exists for the same pickup (tenant, project, ticket, revision, policy).
    /// </summary>
    /// <exception cref="EventContractException">The request does not match the analysis.requested contract.</exception>
    public static async Task<EventEnvelope?> CreateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, NewAnalysisRun run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        var data = run.Data;
        var envelope = EventEnvelope.Create(
            EventContracts.AnalysisRequested, run.TenantId, correlationId: run.RunId.ToString(), run.TraceId, run.RunId,
            sourceRevision: data.TicketRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), data);
        EventContracts.Validate(envelope);

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO workflow.analysis_runs (run_id, tenant_id, project_id, ticket_id, ticket_revision, " +
            "repository_id, base_commit_sha, analysis_mode, trace_id, trigger_source, ticket_kind, policy_revision) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12) " +
            "ON CONFLICT (tenant_id, project_id, ticket_id, ticket_revision, policy_revision) " +
            "WHERE policy_revision IS NOT NULL DO NOTHING",
            connection,
            transaction))
        {
            foreach (var value in new object?[]
            {
                run.RunId, run.TenantId, data.ProjectId, data.TicketId, data.TicketRevision, data.RepositoryId,
                data.BaseCommitSha, data.AnalysisMode == AnalysisMode.Deep ? "deep" : "brief", run.TraceId,
                run.TriggerSource, run.TicketKind, run.PolicyRevision,
            })
            {
                insert.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
            }

            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return null;
            }
        }

        await EventOutbox.EnqueueAsync(connection, transaction, envelope, cancellationToken);
        return envelope;
    }
}
