using VisionIQ.Api.Features.Events;
using VisionIQ.Api.Features.Pickup;

namespace VisionIQ.Api.Tests;

/// <summary>
/// In-memory <see cref="ITicketPickupStore"/> that keeps the same uniqueness rules as the database: one pickup per
/// (trigger, delivery id) and one run per (project, ticket, revision, policy revision).
/// </summary>
internal sealed class FakePickupStore : ITicketPickupStore
{
    public List<TicketPickup> Pickups { get; } = [];

    public Dictionary<Guid, (string Status, string? Reason, int Attempts, TimeSpan? Delay)> State { get; } = [];

    public List<NewAnalysisRun> Runs { get; } = [];

    public Exception? Failure { get; set; }

    public Task<RecordOutcome> RecordAsync(TicketPickup pickup, CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        var stored = Pickups.FirstOrDefault(p => p.Trigger == pickup.Trigger && p.DeliveryId == pickup.DeliveryId);
        if (stored is not null)
        {
            if (State[stored.PickupId].Status != "failed")
            {
                return Task.FromResult(RecordOutcome.Duplicate);
            }

            State[stored.PickupId] = ("pending", null, 0, null); // re-armed
            return Task.FromResult(RecordOutcome.Recorded);
        }

        Pickups.Add(pickup);
        State[pickup.PickupId] = ("pending", null, 0, null);
        return Task.FromResult(RecordOutcome.Recorded);
    }

    public Task<bool> IsKnownAsync(string projectId, string ticketId, int revision, CancellationToken cancellationToken) =>
        Task.FromResult(
            Pickups.Any(p => (p.ProjectId, p.TicketId, p.TicketRevision) == (projectId, ticketId, revision) && State[p.PickupId].Status != "failed") ||
            Runs.Any(r => (r.Data.ProjectId, r.Data.TicketId, r.Data.TicketRevision) == (projectId, ticketId, revision)));

    public Task<IReadOnlyList<PendingPickup>> ClaimAsync(int batchSize, TimeSpan lease, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PendingPickup>>(Pickups
            .Where(p => State[p.PickupId].Status == "pending")
            .Take(batchSize)
            .Select(p => new PendingPickup(p, State[p.PickupId].Attempts))
            .ToList());

    public Task<(StartOutcome Outcome, Guid RunId)> StartRunAsync(TicketPickup pickup, NewAnalysisRun run, CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        EventContracts.Validate(EventEnvelope.Create(
            EventContracts.AnalysisRequested, run.TenantId, run.RunId.ToString(), run.TraceId, run.RunId, "1", run.Data));
        var attempts = State[pickup.PickupId].Attempts + 1;
        var newer = Runs.Where(r =>
                (r.TenantId, r.Data.ProjectId, r.Data.TicketId) == (run.TenantId, run.Data.ProjectId, run.Data.TicketId) &&
                r.Data.TicketRevision > run.Data.TicketRevision && r.PolicyRevision is not null)
            .MaxBy(r => r.Data.TicketRevision);
        var laterPickup = Pickups.Any(p =>
            (p.ProjectId, p.TicketId) == (pickup.ProjectId, pickup.TicketId) &&
            p.TicketRevision > pickup.TicketRevision && State[p.PickupId].Status != "failed");
        if (newer is not null || laterPickup)
        {
            State[pickup.PickupId] = ("duplicate", "stale_revision", attempts, null);
            return Task.FromResult((StartOutcome.Stale, newer?.RunId ?? Guid.Empty));
        }

        var existing = Runs.FirstOrDefault(r =>
            (r.TenantId, r.Data.ProjectId, r.Data.TicketId, r.Data.TicketRevision, r.PolicyRevision) ==
            (run.TenantId, run.Data.ProjectId, run.Data.TicketId, run.Data.TicketRevision, run.PolicyRevision));
        if (existing is not null)
        {
            State[pickup.PickupId] = ("duplicate", "run_already_started", attempts, null);
            return Task.FromResult((StartOutcome.AlreadyStarted, existing.RunId));
        }

        Runs.Add(run);
        State[pickup.PickupId] = ("started", null, attempts, null);
        return Task.FromResult((StartOutcome.Started, run.RunId));
    }

    public Task DeferAsync(Guid pickupId, string reason, TimeSpan delay, CancellationToken cancellationToken)
    {
        State[pickupId] = ("pending", reason, State[pickupId].Attempts + 1, delay);
        return Task.CompletedTask;
    }

    public Task FailAsync(Guid pickupId, string reason, CancellationToken cancellationToken)
    {
        State[pickupId] = ("failed", reason, State[pickupId].Attempts + 1, null);
        return Task.CompletedTask;
    }
}
