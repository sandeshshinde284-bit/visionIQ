using Npgsql;
using VisionIQ.Api.Features.Events;

namespace VisionIQ.Api.Features.Pickup;

public enum PickupTrigger
{
    AdoWebhook,
    AdoSweep,
}

/// <summary>An eligible pickup to store; <see cref="DeliveryId"/> is unique per <see cref="Trigger"/>.</summary>
public sealed record TicketPickup(
    Guid PickupId,
    PickupTrigger Trigger,
    string DeliveryId,
    string ProjectId,
    string TicketId,
    int TicketRevision,
    TicketKind Kind,
    string WorkItemType,
    string PickupState,
    string? TriggeredBy,
    string PolicyRevision,
    string? PayloadSha256,
    string TraceId)
{
    /// <summary>The pickup for an eligible change; the webhook and the sweep build it the same way.</summary>
    public static TicketPickup From(
        TicketChange change, PickupDecision decision, string policyRevision, PickupTrigger trigger, string deliveryId,
        string? payloadSha256, string traceId)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(decision);
        if (!decision.Eligible || decision.Project is null)
        {
            throw new ArgumentException("Only an eligible change is picked up.", nameof(decision));
        }

        return new TicketPickup(
            Guid.NewGuid(), trigger, deliveryId, decision.Project.ProjectId,
            change.WorkItemId.ToString(System.Globalization.CultureInfo.InvariantCulture), change.Revision, decision.Kind,
            change.WorkItemType, change.StateAfter!, change.ChangedById, policyRevision, payloadSha256, traceId);
    }

    public static string TriggerCode(PickupTrigger trigger) => trigger == PickupTrigger.AdoWebhook ? "ado_webhook" : "ado_sweep";

    public static string KindCode(TicketKind kind) => kind == TicketKind.Defect ? "defect" : "enhancement";
}

/// <summary>A stored pickup claimed by the worker.</summary>
public sealed record PendingPickup(TicketPickup Pickup, int Attempts);

public enum RecordOutcome
{
    Recorded,
    Duplicate,
}

public enum StartOutcome
{
    Started,

    /// <summary>This ticket revision already has a run (webhook and sweep, or a redelivery).</summary>
    AlreadyStarted,

    /// <summary>A later pickup of the same ticket already has a run; this older one is out of date.</summary>
    Stale,
}

/// <summary>Durable storage for pickups (workflow.ticket_pickups) and the runs they start.</summary>
public interface ITicketPickupStore
{
    /// <summary>Stores a pickup once; a second delivery with the same id is a duplicate and changes nothing.</summary>
    Task<RecordOutcome> RecordAsync(TicketPickup pickup, CancellationToken cancellationToken);

    /// <summary>
    /// True when this ticket revision already has a stored pickup or a run, under any policy revision: the sweep
    /// recovers missed pickups, it does not re-brief tickets because the policy was edited.
    /// </summary>
    Task<bool> IsKnownAsync(string projectId, string ticketId, int revision, CancellationToken cancellationToken);

    /// <summary>Leases due pending pickups so no other worker claims them while they are processed.</summary>
    Task<IReadOnlyList<PendingPickup>> ClaimAsync(int batchSize, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the run and its analysis.requested event, and marks the pickup, in one transaction. Starts nothing
    /// when the revision already has a run, or when a later revision of the ticket does.
    /// </summary>
    Task<(StartOutcome Outcome, Guid RunId)> StartRunAsync(
        TicketPickup pickup, NewAnalysisRun run, CancellationToken cancellationToken);

    /// <summary>Puts the pickup back in the queue after a transient failure.</summary>
    Task DeferAsync(Guid pickupId, string reason, TimeSpan delay, CancellationToken cancellationToken);

    /// <summary>Takes the pickup out of the queue for a person to look at.</summary>
    Task FailAsync(Guid pickupId, string reason, CancellationToken cancellationToken);
}

public sealed class PostgresTicketPickupStore(NpgsqlDataSource dataSource) : ITicketPickupStore
{
    private const string Columns =
        "pickup_id, trigger_source, delivery_id, project_id, ticket_id, ticket_revision, ticket_kind, " +
        "work_item_type, pickup_state, triggered_by, policy_revision, payload_sha256, trace_id";

    public async Task<RecordOutcome> RecordAsync(TicketPickup pickup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pickup);
        await using var command = dataSource.CreateCommand(
            $"INSERT INTO workflow.ticket_pickups ({Columns}) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13) " +
            // A failed pickup (e.g. ADO was down for hours) is re-armed when it is delivered or swept again.
            "ON CONFLICT (trigger_source, delivery_id) DO UPDATE SET status = 'pending', reason = NULL, " +
            "attempt_count = 0, available_at = now(), processed_at = NULL WHERE ticket_pickups.status = 'failed'");
        foreach (var value in new object?[]
        {
            pickup.PickupId, TicketPickup.TriggerCode(pickup.Trigger), pickup.DeliveryId, pickup.ProjectId,
            pickup.TicketId, pickup.TicketRevision, TicketPickup.KindCode(pickup.Kind), pickup.WorkItemType,
            pickup.PickupState, pickup.TriggeredBy, pickup.PolicyRevision, pickup.PayloadSha256, pickup.TraceId,
        })
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1 ? RecordOutcome.Recorded : RecordOutcome.Duplicate;
    }

    public async Task<bool> IsKnownAsync(string projectId, string ticketId, int revision, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM workflow.ticket_pickups " +
            "               WHERE project_id = $1 AND ticket_id = $2 AND ticket_revision = $3 AND status <> 'failed') " +
            "    OR EXISTS (SELECT 1 FROM workflow.analysis_runs " +
            "               WHERE project_id = $1 AND ticket_id = $2 AND ticket_revision = $3)");
        foreach (var value in new object[] { projectId, ticketId, revision })
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<IReadOnlyList<PendingPickup>> ClaimAsync(int batchSize, TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE workflow.ticket_pickups p SET available_at = now() + make_interval(secs => $2) " +
            "FROM (SELECT pickup_id FROM workflow.ticket_pickups " +
            "      WHERE status = 'pending' AND available_at <= now() " +
            "      ORDER BY available_at LIMIT $1 FOR UPDATE SKIP LOCKED) due " +
            "WHERE p.pickup_id = due.pickup_id " +
            $"RETURNING {string.Join(", ", Columns.Split(", ").Select(c => "p." + c))}, p.attempt_count");
        command.Parameters.Add(new NpgsqlParameter { Value = batchSize });
        command.Parameters.Add(new NpgsqlParameter { Value = lease.TotalSeconds });
        var claimed = new List<PendingPickup>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add(new PendingPickup(
                new TicketPickup(
                    reader.GetGuid(0),
                    reader.GetString(1) == "ado_webhook" ? PickupTrigger.AdoWebhook : PickupTrigger.AdoSweep,
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt32(5),
                    reader.GetString(6) == "defect" ? TicketKind.Defect : TicketKind.Enhancement,
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.GetString(12)),
                reader.GetInt32(13)));
        }

        return claimed;
    }

    public async Task<(StartOutcome Outcome, Guid RunId)> StartRunAsync(
        TicketPickup pickup, NewAnalysisRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pickup);
        ArgumentNullException.ThrowIfNull(run);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // One ticket at a time across workers, so the stale check and the insert below cannot interleave.
        await using (var lockTicket = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction))
        {
            lockTicket.Parameters.Add(new NpgsqlParameter { Value = $"pickup|{run.TenantId}|{pickup.ProjectId}|{pickup.TicketId}" });
            await lockTicket.ExecuteNonQueryAsync(cancellationToken);
        }

        // A later pickup of this ticket is stored or already has a run (e.g. this webhook arrived late, or the
        // ticket left and re-entered In Progress before the worker ran): do not analyse an out-of-date revision.
        Guid? runId = await LatestRunAsync(connection, transaction, run, laterThanRevision: true, cancellationToken);
        var outcome = runId is not null || await HasLaterPickupAsync(connection, transaction, pickup, cancellationToken)
            ? StartOutcome.Stale
            : StartOutcome.Started;
        if (outcome == StartOutcome.Started &&
            await AnalysisRuns.CreateAsync(connection, transaction, run, cancellationToken) is null)
        {
            // The webhook and the sweep (or two deliveries) saw the same pickup: link to the run that exists.
            outcome = StartOutcome.AlreadyStarted;
            runId = await LatestRunAsync(connection, transaction, run, laterThanRevision: false, cancellationToken);
        }
        else if (outcome == StartOutcome.Started)
        {
            runId = run.RunId;
        }

        await using (var mark = new NpgsqlCommand(
            "UPDATE workflow.ticket_pickups SET status = $2, reason = $3, run_id = $4, " +
            "attempt_count = attempt_count + 1, processed_at = now() WHERE pickup_id = $1 AND status = 'pending'",
            connection,
            transaction))
        {
            mark.Parameters.Add(new NpgsqlParameter { Value = pickup.PickupId });
            mark.Parameters.Add(new NpgsqlParameter { Value = outcome == StartOutcome.Started ? "started" : "duplicate" });
            mark.Parameters.Add(new NpgsqlParameter
            {
                Value = outcome switch
                {
                    StartOutcome.Started => DBNull.Value,
                    StartOutcome.Stale => "stale_revision",
                    _ => "run_already_started",
                },
            });
            mark.Parameters.Add(new NpgsqlParameter { Value = (object?)runId ?? DBNull.Value });
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (outcome, runId ?? Guid.Empty);
    }

    /// <summary>The run of this revision under the run's policy, or of the latest later revision under any policy.</summary>
    private static async Task<Guid?> LatestRunAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, NewAnalysisRun run, bool laterThanRevision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT run_id FROM workflow.analysis_runs WHERE tenant_id = $1 AND project_id = $2 AND ticket_id = $3 " +
            (laterThanRevision
                ? "AND ticket_revision > $4 AND policy_revision IS NOT NULL "
                : "AND ticket_revision = $4 AND policy_revision = $5 ") +
            "ORDER BY ticket_revision DESC LIMIT 1",
            connection,
            transaction);
        foreach (var value in new object[] { run.TenantId, run.Data.ProjectId, run.Data.TicketId, run.Data.TicketRevision })
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        if (!laterThanRevision)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = run.PolicyRevision! });
        }

        return await command.ExecuteScalarAsync(cancellationToken) as Guid?;
    }

    /// <summary>True when a later revision of the same ticket has a stored pickup that has not failed.</summary>
    private static async Task<bool> HasLaterPickupAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, TicketPickup pickup, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM workflow.ticket_pickups WHERE project_id = $1 AND ticket_id = $2 " +
            "AND ticket_revision > $3 AND status <> 'failed')",
            connection,
            transaction);
        foreach (var value in new object[] { pickup.ProjectId, pickup.TicketId, pickup.TicketRevision })
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task DeferAsync(Guid pickupId, string reason, TimeSpan delay, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE workflow.ticket_pickups SET attempt_count = attempt_count + 1, reason = $2, " +
            "available_at = now() + make_interval(secs => $3) WHERE pickup_id = $1 AND status = 'pending'");
        command.Parameters.Add(new NpgsqlParameter { Value = pickupId });
        command.Parameters.Add(new NpgsqlParameter { Value = reason });
        command.Parameters.Add(new NpgsqlParameter { Value = delay.TotalSeconds });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task FailAsync(Guid pickupId, string reason, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE workflow.ticket_pickups SET attempt_count = attempt_count + 1, status = 'failed', reason = $2, " +
            "processed_at = now() WHERE pickup_id = $1 AND status = 'pending'");
        command.Parameters.Add(new NpgsqlParameter { Value = pickupId });
        command.Parameters.Add(new NpgsqlParameter { Value = reason });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
