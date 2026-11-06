using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VisionIQ.Api.Features.Pickup;

/// <summary>
/// S15-14 reconciliation sweep (v1.11 §3.5 failure paths, §8.1). Asks ADO which eligible items entered a pickup
/// state recently, applies the same rules as the webhook, and stores a pickup for every one that has neither a
/// stored pickup nor a run yet: a dropped or failed webhook delivery is recovered, and one that did arrive is not
/// picked up twice.
/// </summary>
public sealed class PickupSweep(
    IPickupSource source,
    ITicketPickupStore store,
    PickupPolicy policy,
    ILogger logger,
    TimeProvider? clock = null)
{
    /// <summary>Sweeps every configured project; returns how many missed pickups were recovered.</summary>
    public async Task<int> SweepAsync(TimeSpan lookback, CancellationToken cancellationToken = default)
    {
        var since = (clock ?? TimeProvider.System).GetUtcNow() - lookback;
        var recovered = 0;
        foreach (var project in policy.Projects)
        {
            try
            {
                var changes = await source.FindPickupsAsync(project, since, cancellationToken);
                var count = 0;
                foreach (var change in changes)
                {
                    count += await RecoverAsync(change, cancellationToken) ? 1 : 0;
                }

                PickupLog.Swept(logger, project.Project, changes.Count, count);
                recovered += count;
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                // One project's outage must not stop the others.
                PickupLog.SweepFailed(logger, project.Project, error.GetType().Name);
            }
        }

        return recovered;
    }

    /// <summary>The sweep's delivery id: readable when short, hashed when it would not fit the column.</summary>
    public static string DeliveryId(string projectId, string ticketId, int revision, string policyRevision)
    {
        var id = string.Create(CultureInfo.InvariantCulture, $"{projectId}:{ticketId}:{revision}:{policyRevision}");
        return id.Length <= 200 ? id : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    }

    private async Task<bool> RecoverAsync(TicketChange change, CancellationToken cancellationToken)
    {
        var decision = policy.Evaluate(change);
        if (!decision.Eligible)
        {
            return false;
        }

        var ticketId = change.WorkItemId.ToString(CultureInfo.InvariantCulture);
        if (await store.IsKnownAsync(decision.Project!.ProjectId, ticketId, change.Revision, cancellationToken))
        {
            return false;
        }

        var pickup = TicketPickup.From(
            change, decision, policy.Revision, PickupTrigger.AdoSweep,
            DeliveryId(decision.Project.ProjectId, ticketId, change.Revision, policy.Revision),
            payloadSha256: null, ActivityTraceId.CreateRandom().ToHexString());
        var outcome = await store.RecordAsync(pickup, cancellationToken);
        PickupLog.Recorded(logger, pickup.ProjectId, pickup.TicketId, pickup.TicketRevision, "ado_sweep", outcome);
        return outcome == RecordOutcome.Recorded;
    }
}
