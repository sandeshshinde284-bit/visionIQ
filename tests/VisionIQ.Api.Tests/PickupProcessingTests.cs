using Microsoft.Extensions.Logging.Abstractions;
using VisionIQ.Api.Features.Events;
using VisionIQ.Api.Features.Pickup;
using static VisionIQ.Api.Tests.PickupTestData;

namespace VisionIQ.Api.Tests;

/// <summary>BRF-01 worker and S15-14 reconciliation sweep, with in-memory store and ADO.</summary>
public sealed class PickupProcessingTests
{
    private static readonly EventingOptions Eventing = new("visioniq-test", null, false, TimeSpan.FromSeconds(2));
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_stored_pickup_starts_exactly_one_brief_run()
    {
        var store = new FakePickupStore();
        await store.RecordAsync(Pickup(), default);

        var claimed = await Processor(store).ProcessPendingAsync();
        await Processor(store).ProcessPendingAsync(); // nothing left to do

        Assert.Equal(1, claimed);
        var run = Assert.Single(store.Runs);
        Assert.Equal(new AnalysisRequestedV1("payments-core", "4711", 7, "payments-api", Sha, AnalysisMode.Brief, "project:payments-core"), run.Data);
        Assert.Equal(("ado_webhook", "defect", "test.1"), (run.TriggerSource, run.TicketKind, run.PolicyRevision));
        Assert.Equal("started", store.State.Values.Single().Status);
    }

    [Fact]
    public async Task Webhook_and_sweep_for_the_same_revision_share_one_run()
    {
        var store = new FakePickupStore();
        await store.RecordAsync(Pickup(), default);
        await store.RecordAsync(Pickup(trigger: PickupTrigger.AdoSweep, deliveryId: "payments-core:4711:7:test.1"), default);

        await Processor(store).ProcessPendingAsync();

        Assert.Single(store.Runs);
        Assert.Equal(["duplicate", "started"], store.State.Values.Select(s => s.Status).Order());
    }

    [Fact]
    public async Task The_branch_head_is_resolved_when_no_commit_is_pinned()
    {
        var store = new FakePickupStore();
        await store.RecordAsync(Pickup(project: "Mobile"), default);
        var commits = new FakeAdo { Head = "fedcba9876543210fedcba9876543210fedcba98" };

        await Processor(store, commits).ProcessPendingAsync();

        Assert.Equal("fedcba9876543210fedcba9876543210fedcba98", Assert.Single(store.Runs).Data.BaseCommitSha);
        Assert.Equal(["Mobile"], commits.Resolved);
    }

    [Fact]
    public async Task An_unresolvable_commit_is_retried_with_backoff_then_failed()
    {
        var store = new FakePickupStore();
        var pickup = Pickup(project: "Mobile");
        await store.RecordAsync(pickup, default);
        var processor = Processor(store, new FakeAdo { Error = new HttpRequestException("ADO down") });

        await processor.ProcessPendingAsync();
        Assert.Equal(("pending", "base_commit_unresolved", 1), Status(store, pickup));
        Assert.Equal(TimeSpan.FromSeconds(15), store.State[pickup.PickupId].Delay);

        for (var i = 1; i < PickupProcessor.MaxAttempts; i++)
        {
            await processor.ProcessPendingAsync();
        }

        Assert.Equal(("failed", "base_commit_unresolved", PickupProcessor.MaxAttempts), Status(store, pickup));
        Assert.Empty(store.Runs);
    }

    [Fact]
    public async Task Without_a_pinned_commit_or_ADO_the_pickup_cannot_start()
    {
        var store = new FakePickupStore();
        var pickup = Pickup(project: "Mobile");
        await store.RecordAsync(pickup, default);

        await Processor(store, commits: null).ProcessPendingAsync();

        Assert.Equal(("pending", "base_commit_unresolved", 1), Status(store, pickup));
    }

    [Fact]
    public async Task A_pickup_for_a_project_removed_from_the_policy_fails()
    {
        var store = new FakePickupStore();
        var pickup = Pickup() with { ProjectId = "retired" };
        await store.RecordAsync(pickup, default);

        await Processor(store).ProcessPendingAsync();

        Assert.Equal(("failed", "project_not_configured", 1), Status(store, pickup));
    }

    [Fact]
    public void Retry_delays_back_off_and_stop()
    {
        var delays = Enumerable.Range(0, PickupProcessor.MaxAttempts).Select(PickupProcessor.RetryDelay).ToList();

        Assert.Equal([15, 30, 60, 120], delays.Take(4).Select(d => d!.Value.TotalSeconds));
        Assert.Equal(TimeSpan.FromMinutes(15), delays.Max());
        Assert.Null(delays[^1]);
        Assert.Single(delays, d => d is null);
    }

    [Fact]
    public async Task Sweep_recovers_a_dropped_webhook_once()
    {
        var store = new FakePickupStore();
        var ado = new FakeAdo { Pickups = [Change()] };
        var sweep = Sweep(store, ado);

        Assert.Equal(1, await sweep.SweepAsync(TimeSpan.FromHours(24)));
        Assert.Equal(0, await sweep.SweepAsync(TimeSpan.FromHours(24))); // already known now

        var recovered = Assert.Single(store.Pickups);
        Assert.Equal((PickupTrigger.AdoSweep, "payments-core:4711:7:test.1"), (recovered.Trigger, recovered.DeliveryId));
        Assert.Equal(Now - TimeSpan.FromHours(24), ado.Since);
        await Processor(store).ProcessPendingAsync();
        Assert.Single(store.Runs);
    }

    [Fact]
    public async Task Sweep_leaves_pickups_the_webhook_already_delivered()
    {
        var store = new FakePickupStore();
        await store.RecordAsync(Pickup(), default);

        Assert.Equal(0, await Sweep(store, new FakeAdo { Pickups = [Change()] }).SweepAsync(TimeSpan.FromHours(1)));
        Assert.Single(store.Pickups);
    }

    [Fact]
    public async Task Sweep_applies_the_same_rules_as_the_webhook()
    {
        var store = new FakePickupStore();
        var ado = new FakeAdo { Pickups = [Change(actor: Bot), Change(assigned: false), Change(tags: ["visioniq-skip"])] };

        Assert.Equal(0, await Sweep(store, ado).SweepAsync(TimeSpan.FromHours(1)));
        Assert.Empty(store.Pickups);
    }

    [Fact]
    public async Task One_project_failing_does_not_stop_the_sweep_of_the_others()
    {
        var store = new FakePickupStore();
        var ado = new FakeAdo { Pickups = [Change(type: "Feature", project: "Mobile", before: "New", after: "Active")], FailFor = "Payments Core" };

        Assert.Equal(1, await Sweep(store, ado).SweepAsync(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Sweep_delivery_ids_always_fit_the_column()
    {
        Assert.Equal("p:1:2:r", PickupSweep.DeliveryId("p", "1", 2, "r"));
        Assert.StartsWith("sha256:", PickupSweep.DeliveryId(new string('p', 250), "1", 2, "r"), StringComparison.Ordinal);
        Assert.True(PickupSweep.DeliveryId(new string('p', 250), "1", 2, "r").Length <= 200);
    }

    [Fact]
    public void The_latest_change_that_made_the_item_actionable_is_the_pickup_revision()
    {
        // New -> In Progress (rev 3) -> Blocked -> In Progress (rev 6, by the bot) -> edited (rev 7)
        var history = new[] { Rev(1, "New"), Rev(3, "In Progress"), Rev(5, "Blocked"), Rev(6, "In Progress", "bot"), Rev(7, "In Progress") };

        var change = PickupTransitions.Latest(Organization, Payments, 4711, history)!;

        Assert.Equal((6, "Blocked", "In Progress", "bot"), (change.Revision, change.StateBefore, change.StateAfter, change.ChangedByName));
        Assert.Null(PickupTransitions.Latest(Organization, Payments, 4711, [Rev(1, "New"), Rev(2, "Done")]));
        var created = PickupTransitions.Latest(Organization, Payments, 4711, [Rev(1, "In Progress")])!;
        Assert.Equal((1, null, false), (created.Revision, created.StateBefore, created.AssignedBefore));
    }

    [Fact]
    public void An_assignment_after_the_state_change_is_the_pickup_revision_when_an_assignee_is_required()
    {
        // In Progress unassigned at rev 2, assigned at rev 4: the webhook would have reported rev 4 as the pickup.
        var history = new[] { Rev(1, "New", assigned: false), Rev(2, "In Progress", assigned: false), Rev(4, "In Progress") };

        var change = PickupTransitions.Latest(Organization, Payments, 4711, history)!;

        Assert.Equal((4, false, true), (change.Revision, change.AssignedBefore, change.Assigned));
        Assert.True(Policy.Evaluate(change).Eligible);
    }

    [Fact]
    public async Task A_late_pickup_of_an_older_revision_does_not_start_a_run()
    {
        var store = new FakePickupStore();
        var newer = TicketPickup.From(Change(revision: 9), Policy.Evaluate(Change(revision: 9)), Policy.Revision, PickupTrigger.AdoWebhook, "evt-9", null, "0af7651916cd43dd8448eb211c80319c");
        await store.RecordAsync(newer, default);
        await Processor(store).ProcessPendingAsync();
        var late = Pickup(deliveryId: "evt-7");
        await store.RecordAsync(late, default);

        await Processor(store).ProcessPendingAsync();

        Assert.Equal(9, Assert.Single(store.Runs).Data.TicketRevision);
        Assert.Equal(("duplicate", "stale_revision", 1), Status(store, late));
    }

    [Fact]
    public async Task An_older_pickup_queued_behind_a_newer_one_does_not_start_a_run()
    {
        // In Progress (rev 7) -> New -> In Progress (rev 9), both queued before the worker ran.
        var store = new FakePickupStore();
        var older = Pickup(deliveryId: "evt-7");
        await store.RecordAsync(older, default);
        await store.RecordAsync(TicketPickup.From(Change(revision: 9), Policy.Evaluate(Change(revision: 9)), Policy.Revision, PickupTrigger.AdoWebhook, "evt-9", null, "0af7651916cd43dd8448eb211c80319c"), default);

        await Processor(store).ProcessPendingAsync();

        Assert.Equal(9, Assert.Single(store.Runs).Data.TicketRevision);
        Assert.Equal(("duplicate", "stale_revision", 1), Status(store, older));
    }

    [Fact]
    public async Task A_failed_pickup_is_retried_when_delivered_or_swept_again()
    {
        var store = new FakePickupStore();
        var pickup = Pickup(project: "Mobile");
        await store.RecordAsync(pickup, default);
        var processor = Processor(store, new FakeAdo { Error = new HttpRequestException("ADO down") });
        for (var i = 0; i < PickupProcessor.MaxAttempts; i++)
        {
            await processor.ProcessPendingAsync();
        }

        Assert.Equal("failed", Status(store, pickup).Item1);

        // ADO retries the delivery once it is back: the pickup is pending again and starts its run.
        Assert.Equal(RecordOutcome.Recorded, await store.RecordAsync(pickup with { PickupId = Guid.NewGuid() }, default));
        await Processor(store, new FakeAdo()).ProcessPendingAsync();
        Assert.Single(store.Runs);
    }

    [Fact]
    public async Task The_sweep_recovers_a_ticket_whose_webhook_pickup_failed()
    {
        var store = new FakePickupStore();
        var failed = Pickup();
        await store.RecordAsync(failed, default);
        await store.FailAsync(failed.PickupId, "base_commit_unresolved", default);

        Assert.Equal(1, await Sweep(store, new FakeAdo { Pickups = [Change()] }).SweepAsync(TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task A_run_carries_the_policy_revision_it_was_started_under()
    {
        var store = new FakePickupStore();
        await store.RecordAsync(Pickup() with { PolicyRevision = "old.1" }, default);

        await Processor(store).ProcessPendingAsync();

        Assert.Equal(Policy.Revision, Assert.Single(store.Runs).PolicyRevision);
    }

    [Fact]
    public async Task The_branch_head_is_looked_up_once_per_project_per_batch()
    {
        var store = new FakePickupStore();
        var ado = new FakeAdo();
        await store.RecordAsync(Pickup(project: "Mobile", deliveryId: "a"), default);
        await store.RecordAsync(Pickup(project: "Mobile", deliveryId: "b") with { TicketId = "4712" }, default);

        await Processor(store, ado).ProcessPendingAsync();

        Assert.Equal(2, store.Runs.Count);
        Assert.Single(ado.Resolved);
    }

    [Fact]
    public async Task Changing_the_policy_revision_does_not_make_the_sweep_rebrief_tickets()
    {
        var store = new FakePickupStore();
        await store.RecordAsync(Pickup() with { PolicyRevision = "old.1" }, default);

        Assert.Equal(0, await Sweep(store, new FakeAdo { Pickups = [Change()] }).SweepAsync(TimeSpan.FromHours(1)));
    }

    private static WorkItemSnapshot Rev(int rev, string state, string actor = "dev", bool assigned = true) =>
        new(rev, Now.AddMinutes(rev), "Bug", state, assigned, [], "id-" + actor, actor);

    [Fact]
    public async Task With_an_allowlist_the_sweep_asks_ADO_only_about_those_items_and_recovers_no_other()
    {
        var store = new FakePickupStore();
        // Even if ADO returned another item (it should not: the query names the ids), it is not picked up.
        var ado = new FakeAdo { Pickups = [Change(), Change() with { WorkItemId = 9001 }] };
        var policy = Policy with { AllowedWorkItemIds = new HashSet<int> { 4711, 5000 } };
        var sweep = new PickupSweep(ado, store, policy, NullLogger.Instance, new FixedClock(Now));

        Assert.Equal(1, await sweep.SweepAsync(TimeSpan.FromHours(1)));

        Assert.Equal("4711", Assert.Single(store.Pickups).TicketId);
        Assert.Equal([4711, 5000], ado.Only!.Order());
        var wiql = AdoPickupSource.BuildWiql(Payments, Now, [5000, 4711]);
        Assert.Contains("[System.TeamProject] = 'Payments Core' AND [System.Id] IN (4711, 5000) AND", wiql, StringComparison.Ordinal);
        Assert.DoesNotContain("[System.Id] IN", AdoPickupSource.BuildWiql(Payments, Now), StringComparison.Ordinal);
    }

    [Fact]
    public void Sweep_query_selects_eligible_items_and_escapes_values()
    {
        var project = Payments with { Project = "O'Brien", PickupStates = ["In Progress", "Doing"] };

        var wiql = AdoPickupSource.BuildWiql(project, Now);

        Assert.Contains("[System.TeamProject] = 'O''Brien'", wiql, StringComparison.Ordinal);
        Assert.Contains("[System.WorkItemType] IN ('Bug', 'User Story')", wiql, StringComparison.Ordinal);
        Assert.Contains("[System.State] IN ('In Progress', 'Doing')", wiql, StringComparison.Ordinal);
        Assert.Contains("[System.ChangedDate] >= '2026-11-05T12:00:00.000Z'", wiql, StringComparison.Ordinal);
        Assert.EndsWith("ORDER BY [System.ChangedDate] DESC, [System.Id] ASC", wiql, StringComparison.Ordinal);
    }

    private static TicketPickup Pickup(
        PickupTrigger trigger = PickupTrigger.AdoWebhook, string deliveryId = "evt-1", string project = "Payments Core")
    {
        var change = project == "Mobile" ? Change(type: "Feature", project: "Mobile", before: "New", after: "Active") : Change();
        return TicketPickup.From(change, Policy.Evaluate(change), Policy.Revision, trigger, deliveryId, null, "0af7651916cd43dd8448eb211c80319c");
    }

    private static (string, string?, int) Status(FakePickupStore store, TicketPickup pickup) =>
        (store.State[pickup.PickupId].Status, store.State[pickup.PickupId].Reason, store.State[pickup.PickupId].Attempts);

    private static PickupProcessor Processor(FakePickupStore store, IBaseCommitResolver? commits = null) =>
        new(store, commits, Policy, Eventing, NullLogger.Instance);

    private static PickupSweep Sweep(FakePickupStore store, FakeAdo ado) =>
        new(ado, store, Policy, NullLogger.Instance, new FixedClock(Now));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeAdo : IPickupSource, IBaseCommitResolver
    {
        public List<TicketChange> Pickups { get; init; } = [];

        public string? FailFor { get; init; }

        public DateTimeOffset Since { get; private set; }

        public string Head { get; init; } = Sha;

        public Exception? Error { get; init; }

        public List<string> Resolved { get; } = [];

        public IReadOnlyCollection<int>? Only { get; private set; }

        public Task<IReadOnlyList<TicketChange>> FindPickupsAsync(
            ProjectPickupPolicy project, DateTimeOffset since, IReadOnlyCollection<int>? onlyWorkItems, CancellationToken cancellationToken)
        {
            Since = since;
            Only = onlyWorkItems;
            if (project.Project == FailFor)
            {
                throw new HttpRequestException("ADO down");
            }

            return Task.FromResult<IReadOnlyList<TicketChange>>(Pickups.Where(p => p.Project == project.Project).ToList());
        }

        public Task<string> ResolveAsync(ProjectPickupPolicy project, CancellationToken cancellationToken)
        {
            if (Error is not null)
            {
                throw Error;
            }

            Resolved.Add(project.Project);
            return Task.FromResult(Head);
        }
    }
}
