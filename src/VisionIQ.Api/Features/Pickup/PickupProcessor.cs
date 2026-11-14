using VisionIQ.Api.Features.Events;

namespace VisionIQ.Api.Features.Pickup;

/// <summary>
/// Turns stored pickups into runs, off the webhook's request thread. For each pickup it resolves the base commit
/// (pinned in the policy, or the head of the configured branch in ADO), then creates the run and its
/// analysis.requested event in one transaction. A second pickup of the same ticket revision links to the existing
/// run instead of starting another.
/// </summary>
public sealed class PickupProcessor(
    ITicketPickupStore store,
    IBaseCommitResolver? commits,
    PickupPolicy policy,
    EventingOptions eventing,
    ILogger logger)
{
    /// <summary>Attempts before a pickup whose commit cannot be resolved is failed (about 1.5 hours of backoff).</summary>
    public const int MaxAttempts = 12;

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    /// <summary>Processes due pickups; returns how many were claimed.</summary>
    public async Task<int> ProcessPendingAsync(int batchSize = 20, CancellationToken cancellationToken = default)
    {
        var claimed = await store.ClaimAsync(batchSize, Lease, cancellationToken);
        // One branch-head lookup per project per batch keeps a batch well inside its lease.
        var heads = new Dictionary<string, Task<string>>(StringComparer.Ordinal);
        foreach (var (pickup, attempts) in claimed)
        {
            try
            {
                await ProcessAsync(pickup, attempts, heads, cancellationToken);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                // One pickup whose state could not even be recorded (store failure) must not hold back the
                // rest of the claimed batch: its lease expires and it is claimed again.
                PickupLog.ItemFailed(logger, pickup.ProjectId, pickup.TicketId, pickup.TicketRevision, error.GetType().Name);
            }
        }

        return claimed.Count;
    }

    /// <summary>How long to wait after a failed attempt; null once the pickup should be failed.</summary>
    public static TimeSpan? RetryDelay(int previousAttempts) =>
        previousAttempts + 1 >= MaxAttempts ? null : TimeSpan.FromSeconds(Math.Min(900, 15 * (1 << Math.Min(previousAttempts, 10))));

    private async Task ProcessAsync(
        TicketPickup pickup, int attempts, Dictionary<string, Task<string>> heads, CancellationToken cancellationToken)
    {
        // Pickups are stored with the project id the policy gave them; the policy may have changed since.
        var project = policy.Projects.FirstOrDefault(p => string.Equals(p.ProjectId, pickup.ProjectId, StringComparison.Ordinal));
        if (project is null)
        {
            await FailAsync(pickup, "project_not_configured", cancellationToken);
            return;
        }

        string baseCommit;
        try
        {
            if (project.BaseCommitSha is { } pinned)
            {
                baseCommit = pinned;
            }
            else if (commits is null)
            {
                throw new InvalidOperationException("No base_commit_sha and no ADO connection to resolve one.");
            }
            else
            {
                if (!heads.TryGetValue(project.ProjectId, out var head))
                {
                    head = commits.ResolveAsync(project, cancellationToken);
                    heads[project.ProjectId] = head;
                }

                baseCommit = await head;
            }
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            await RetryOrFailAsync(pickup, attempts, "base_commit_unresolved", error, cancellationToken);
            return;
        }

        var run = new NewAnalysisRun(
            Guid.NewGuid(),
            eventing.TenantId,
            new AnalysisRequestedV1(
                project.ProjectId, pickup.TicketId, pickup.TicketRevision, project.RepositoryId, baseCommit,
                AnalysisMode.Brief, project.SecurityScopeId, TicketPickup.KindCode(pickup.Kind)),
            pickup.TraceId,
            TicketPickup.TriggerCode(pickup.Trigger),
            TicketPickup.KindCode(pickup.Kind),
            // The run uses the current policy's repository, branch and scope, so it carries that policy's revision.
            policy.Revision);
        try
        {
            var (outcome, runId) = await store.StartRunAsync(pickup, run, cancellationToken);
            PickupLog.RunStarted(logger, pickup.ProjectId, pickup.TicketId, pickup.TicketRevision, outcome, runId);
        }
        catch (EventContractException)
        {
            await FailAsync(pickup, "contract_invalid", cancellationToken);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            await RetryOrFailAsync(pickup, attempts, "run_not_started", error, cancellationToken);
        }
    }

    private async Task RetryOrFailAsync(
        TicketPickup pickup, int attempts, string reason, Exception error, CancellationToken cancellationToken)
    {
        if (RetryDelay(attempts) is not { } delay)
        {
            await FailAsync(pickup, reason, cancellationToken);
            return;
        }

        await store.DeferAsync(pickup.PickupId, reason, delay, cancellationToken);
        PickupLog.Deferred(logger, pickup.ProjectId, pickup.TicketId, pickup.TicketRevision, reason, error.GetType().Name, attempts + 1);
    }

    private async Task FailAsync(TicketPickup pickup, string reason, CancellationToken cancellationToken)
    {
        await store.FailAsync(pickup.PickupId, reason, cancellationToken);
        PickupLog.Failed(logger, pickup.ProjectId, pickup.TicketId, pickup.TicketRevision, reason);
    }
}
