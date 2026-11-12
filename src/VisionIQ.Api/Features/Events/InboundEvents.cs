using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using VisionIQ.Api.Features.Briefs;
using VisionIQ.Api.Features.Fixes;

namespace VisionIQ.Api.Features.Events;

public enum InboundOutcome
{
    Processed,
    Duplicate,
}

/// <summary>Applies one validated event exactly once.</summary>
public interface IInboundEventProcessor
{
    /// <exception cref="EventContractException">The event is valid but not consumed by this service.</exception>
    /// <exception cref="EventRejectedException">The event conflicts with stored state; retrying cannot fix it.</exception>
    Task<InboundOutcome> ProcessAsync(EventEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>
/// A valid event that can never be applied (unknown run, another tenant's run, a run that already has a result).
/// The push endpoint rejects it so it ends in the dead-letter topic instead of being retried as an outage.
/// </summary>
public sealed class EventRejectedException : Exception
{
    public EventRejectedException()
        : this("rejected")
    {
    }

    public EventRejectedException(string reason)
        : base(reason)
    {
        Reason = reason;
    }

    public EventRejectedException(string reason, Exception innerException)
        : base(reason, innerException)
    {
        Reason = reason;
    }

    /// <summary>A fixed reason code; never event data.</summary>
    public string Reason { get; } = "rejected";
}

/// <summary>
/// Idempotent inbox (S03-07): the inbox row and the handler's writes commit in one transaction, so an event is
/// applied exactly once or not at all (and Pub/Sub redelivers it). A concurrent redelivery blocks on the
/// primary key until the first attempt commits, then sees the row and reports a duplicate. A rejected event keeps
/// its inbox row (with the reason) but none of the handler's writes. When brief publishing is on (BRF-05), the
/// run's ticket comment is queued in the same transaction as its result. The result of a deep run is a fix,
/// not a brief: it is queued for a draft pull request instead (DEF-05), and never for a ticket comment.
/// </summary>
public sealed class PostgresInboundEventProcessor(
    NpgsqlDataSource dataSource, ILogger<PostgresInboundEventProcessor> logger, BriefPublishOptions publishing,
    FixPullRequestOptions? fixes = null)
    : IInboundEventProcessor
{
    private readonly FixPullRequestOptions fixes = fixes ?? FixPullRequestOptions.Disabled;

    public async Task<InboundOutcome> ProcessAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if ((envelope.EventType, envelope.SchemaVersion) != (EventContracts.AnalysisCompleted, 1))
        {
            throw new EventContractException($"{envelope.EventType} v{envelope.SchemaVersion} is not consumed here");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var inbox = new NpgsqlCommand(
            "INSERT INTO workflow.event_inbox (event_id, event_type, schema_version, trace_id) " +
            "VALUES ($1, $2, $3, $4) ON CONFLICT (event_id) DO NOTHING",
            connection,
            transaction))
        {
            inbox.Parameters.Add(new NpgsqlParameter { Value = envelope.EventId });
            inbox.Parameters.Add(new NpgsqlParameter { Value = envelope.EventType });
            inbox.Parameters.Add(new NpgsqlParameter { Value = envelope.SchemaVersion });
            inbox.Parameters.Add(new NpgsqlParameter { Value = envelope.TraceId });
            if (await inbox.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return InboundOutcome.Duplicate;
            }
        }

        // Savepoint: a rejection undoes the handler's writes but keeps the inbox row, so a replay of this event
        // (e.g. from the dead-letter topic) is a duplicate instead of running the handler again.
        await transaction.SaveAsync("apply", cancellationToken);
        try
        {
            await ApplyAnalysisCompletedAsync(connection, transaction, envelope, cancellationToken);
        }
        catch (EventRejectedException rejected)
        {
            await transaction.RollbackAsync("apply", cancellationToken);
            await using (var reason = new NpgsqlCommand(
                "UPDATE workflow.event_inbox SET rejected_reason = $2 WHERE event_id = $1", connection, transaction))
            {
                reason.Parameters.Add(new NpgsqlParameter { Value = envelope.EventId });
                reason.Parameters.Add(new NpgsqlParameter { Value = rejected.Reason });
                await reason.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            throw;
        }

        await transaction.CommitAsync(cancellationToken);
        return InboundOutcome.Processed;
    }

    private async Task ApplyAnalysisCompletedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, EventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        string mode;
        await using (var run = new NpgsqlCommand(
            "SELECT tenant_id, status, analysis_mode FROM workflow.analysis_runs WHERE run_id = $1 FOR UPDATE",
            connection,
            transaction))
        {
            run.Parameters.Add(new NpgsqlParameter { Value = envelope.RunId!.Value });
            await using var reader = await run.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new EventRejectedException("unknown_run");
            }

            if (!string.Equals(reader.GetString(0), envelope.TenantId, StringComparison.Ordinal))
            {
                throw new EventRejectedException("tenant_mismatch");
            }

            // Redeliveries of the same event stop at the inbox, so a stored result here came from a different event.
            if (reader.GetString(1) == "completed")
            {
                throw new EventRejectedException("run_already_completed");
            }

            mode = reader.GetString(2);
        }

        var result = envelope.ReadData<AnalysisCompletedV1>();
        await using var command = new NpgsqlCommand(
            "WITH stored AS (" +
            "  INSERT INTO workflow.analysis_results " +
            "    (result_id, run_id, result_state, evidence_ids, advice_ids, reason_codes, event_id) " +
            "  VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING run_id) " +
            "UPDATE workflow.analysis_runs SET status = 'completed', completed_at = now() " +
            "WHERE run_id IN (SELECT run_id FROM stored)",
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = result.ResultId });
        command.Parameters.Add(new NpgsqlParameter { Value = envelope.RunId!.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.SerializeToElement(result.ResultState, EventEnvelope.Json).GetString()! });
        command.Parameters.Add(new NpgsqlParameter { Value = result.EvidenceIds.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = result.AdviceIds.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = result.ReasonCodes.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = envelope.EventId });
        await command.ExecuteNonQueryAsync(cancellationToken);
        var state = JsonSerializer.SerializeToElement(result.ResultState, EventEnvelope.Json).GetString()!;
        if (mode == "deep")
        {
            if (fixes.Mode != FixPullRequestMode.Off)
            {
                await PostgresFixPullRequestStore.EnqueueAsync(
                    connection, transaction, envelope.RunId!.Value, result.ResultId, state, cancellationToken);
                FixLog.Queued(logger, result.ResultId, envelope.RunId!.Value, state);
            }
        }
        else
        {
            if (publishing.Mode != BriefPublishMode.Off &&
                !result.ReasonCodes.Contains(PostgresBriefPublicationStore.PipelineDisabled, StringComparer.Ordinal))
            {
                await PostgresBriefPublicationStore.EnqueueAsync(
                    connection, transaction, envelope.RunId!.Value, result.ResultId, state, cancellationToken);
            }

            if (fixes.AutoStart && result.ResultState != ResultState.Blocked)
            {
                if (await FixRunStarter.StartAfterBriefAsync(connection, transaction, envelope, cancellationToken) is { } fixRun)
                {
                    FixLog.FixRunRequested(logger, fixRun, envelope.RunId!.Value);
                }
                else
                {
                    FixLog.NoFixRun(logger, envelope.RunId!.Value);
                }
            }
        }

        EventLog.AnalysisCompleted(logger, result.ResultState);
    }
}
