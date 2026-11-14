using VisionIQ.Api.Features.Pickup;
using static VisionIQ.Api.Tests.PickupTestData;

namespace VisionIQ.Api.Tests;

/// <summary>S10-01: which ticket changes start a run (v1.11 §3.5, §8.1).</summary>
public sealed class PickupPolicyTests
{
    [Fact]
    public void Assigned_defect_moving_into_the_pickup_state_is_eligible()
    {
        var decision = Policy.Evaluate(Change());

        Assert.True(decision.Eligible);
        Assert.Equal(TicketKind.Defect, decision.Kind);
        Assert.Equal("payments-core", decision.Project!.ProjectId);
    }

    [Fact]
    public void Enhancement_types_and_states_are_per_project()
    {
        var decision = Policy.Evaluate(Change(type: "Feature", project: "Mobile", before: "New", after: "Committed", assigned: false));

        Assert.True(decision.Eligible);
        Assert.Equal(TicketKind.Enhancement, decision.Kind);
    }

    [Fact]
    public void A_newly_created_item_already_in_the_pickup_state_is_eligible()
    {
        Assert.True(Policy.Evaluate(Change(before: null, assignedBefore: false)).Eligible);
    }

    [Fact]
    public void Assigning_an_item_that_is_already_in_progress_is_its_pickup()
    {
        // Moved to In Progress unassigned (ignored), then assigned while still In Progress.
        Assert.Equal("unassigned", Policy.Evaluate(Change(assigned: false, assignedBefore: false)).Reason);
        Assert.True(Policy.Evaluate(Change(before: "In Progress", assigned: true, assignedBefore: false)).Eligible);
        // Reassigning to someone else is not a new pickup.
        Assert.Equal("no_pickup_transition", Policy.Evaluate(Change(before: "In Progress", assignedBefore: true)).Reason);
    }

    [Fact]
    public void Projects_that_do_not_require_an_assignee_pick_up_on_the_state_change_alone()
    {
        Assert.True(Policy.Evaluate(Change(type: "Feature", project: "Mobile", before: "New", after: "Active", assigned: false)).Eligible);
        Assert.Equal("no_pickup_transition",
            Policy.Evaluate(Change(type: "Feature", project: "Mobile", before: "Active", after: "Active", assigned: true, assignedBefore: false)).Reason);
    }

    [Theory]
    [InlineData("organization_not_allowed")]
    [InlineData("project_not_configured")]
    [InlineData("type_not_eligible")]
    [InlineData("no_pickup_transition")]
    [InlineData("unassigned")]
    [InlineData("bot_update")]
    [InlineData("excluded_tag")]
    [InlineData("work_item_not_allowlisted")]
    public void Each_rule_has_its_own_reason(string reason)
    {
        var change = reason switch
        {
            "organization_not_allowed" => Change(organization: "https://dev.azure.com/someone-else/"),
            "project_not_configured" => Change(project: "Unknown"),
            "type_not_eligible" => Change(type: "Task"),
            "work_item_not_allowlisted" => Change() with { WorkItemId = 4712 },
            // An edit while already in progress, e.g. a comment, must not start another run.
            "no_pickup_transition" => Change(before: "In Progress"),
            "unassigned" => Change(assigned: false),
            "bot_update" => Change(actor: Bot.ToUpperInvariant()),
            _ => Change(tags: ["VisionIQ-Skip"]),
        };

        // The allowlist names #4711 only; without one, #4712 would be picked up like any other item.
        var policy = reason == "work_item_not_allowlisted" ? Policy with { AllowedWorkItemIds = new HashSet<int> { 4711 } } : Policy;

        var decision = policy.Evaluate(change);

        Assert.False(decision.Eligible);
        Assert.Equal(reason, decision.Reason);
    }

    [Fact]
    public void An_allowlist_limits_pickup_to_the_chosen_work_items_and_keeps_every_other_rule()
    {
        var pilot = Policy with { AllowedWorkItemIds = new HashSet<int> { 4711, 4800 } };

        Assert.True(pilot.Evaluate(Change()).Eligible); // #4711 moved New -> In Progress
        Assert.Equal("work_item_not_allowlisted", pilot.Evaluate(Change() with { WorkItemId = 4712 }).Reason);
        // Being on the list is not enough: it must still be the change that makes the item actionable.
        Assert.Equal("no_pickup_transition", pilot.Evaluate(Change(before: "In Progress")).Reason);
        Assert.Equal("unassigned", pilot.Evaluate(Change(assigned: false)).Reason);
        Assert.Equal("bot_update", pilot.Evaluate(Change(actor: Bot)).Reason);
        Assert.True(Policy.Evaluate(Change() with { WorkItemId = 4712 }).Eligible); // no list: no restriction
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("4711", "4711")]
    [InlineData(" 4712 , 4711,4711 ", "4711,4712")]
    [InlineData("12;7", "7,12")]
    public void The_allowlist_setting_is_a_list_of_work_item_numbers(string? value, string? expected)
    {
        var ids = PickupOptions.AllowedWorkItemIds(value);

        Assert.Equal(expected, ids is null ? null : string.Join(',', ids));
    }

    [Theory]
    [InlineData("4711,")]
    [InlineData("4711,abc")]
    [InlineData("#4711")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("4711 4712")]
    [InlineData("47.11")]
    public void A_malformed_allowlist_stops_start_up_instead_of_picking_up_everything(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => PickupOptions.AllowedWorkItemIds(value));

        Assert.Contains("PICKUP_ALLOWED_WORK_ITEM_IDS", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() =>
            PickupOptions.AllowedWorkItemIds(string.Join(',', Enumerable.Range(1, PickupOptions.MaximumAllowedWorkItems + 1))));
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("Done")]
    [InlineData(null)]
    public void Leaving_or_missing_the_pickup_state_is_not_a_pickup(string? after)
    {
        Assert.Equal("no_pickup_transition", Policy.Evaluate(Change(before: "In Progress", after: after)).Reason);
        Assert.Equal("no_pickup_transition", Policy.Evaluate(Change(before: "New", after: after)).Reason);
    }

    [Fact]
    public void Matching_ignores_case_and_trailing_slashes()
    {
        var decision = Policy.Evaluate(Change(type: "bug", project: "PAYMENTS CORE", before: "new", after: "in progress",
            organization: "https://DEV.azure.com/contoso"));

        Assert.True(decision.Eligible);
    }

    [Fact]
    public void Defaults_apply_when_optional_values_are_left_out()
    {
        var mobile = Policy.For("mobile")!;

        Assert.Equal("Mobile", mobile.ProjectId);
        Assert.Equal("main", mobile.Branch);
        Assert.Null(mobile.BaseCommitSha);
        Assert.Empty(mobile.ExcludedTags);
    }

    [Fact]
    public void The_documented_sample_webhook_is_picked_up_by_the_example_policy()
    {
        var root = Path.Combine(EventContractTests.ContractsDirectory, "..", "..");
        var policy = PickupPolicy.Parse(File.ReadAllText(Path.Combine(root, "config", "pickup-policy.example.json")));
        var sample = File.ReadAllBytes(Path.Combine(root, "docs", "features", "BRF-01-ticket-pickup", "sample-workitem-updated.json"));

        var delivery = AdoServiceHook.Parse(sample, out var problem);

        Assert.Null(problem);
        Assert.True(policy.Evaluate(delivery!.Change).Eligible);
    }

    [Theory]
    [InlineData("""{"projects": []}""", "policy_revision is required")]
    [InlineData("""{"policy_revision": "1", "projects": []}""", "projects needs at least one entry")]
    [InlineData("""{"policy_revision": "1", "projects": [{"project": "Payments Core", "defect_types": ["Bug"], "pickup_states": ["Doing"], "repository_id": "r", "security_scope_id": "s"}]}""", "project_id is required")]
    [InlineData("""{"policy_revision": "1", "projects": [{"project": "P", "defect_types": ["Bug"], "repository_id": "r", "security_scope_id": "s"}]}""", "pickup_states is required")]
    [InlineData("""{"policy_revision": "1", "projects": [{"project": "P", "pickup_states": ["Doing"], "repository_id": "r", "security_scope_id": "s"}]}""", "at least one defect_types or enhancement_types")]
    [InlineData("""{"policy_revision": "1", "projects": [{"project": "P", "defect_types": ["Bug"], "enhancement_types": ["bug"], "pickup_states": ["Doing"], "repository_id": "r", "security_scope_id": "s"}]}""", "both a defect and an enhancement")]
    [InlineData("""{"policy_revision": "1", "projects": [{"project": "P", "defect_types": ["Bug"], "pickup_states": ["Doing"], "repository_id": "r", "security_scope_id": "s", "base_commit_sha": "main"}]}""", "full lowercase commit SHA")]
    [InlineData("""{"policy_revision": "1", "organization_url": "http://ado.local", "projects": []}""", "https URL")]
    [InlineData("""{"policy_revision": "1", "projects": [], "unexpected": true}""", "not valid JSON")]
    public void Invalid_policies_are_refused_with_a_clear_message(string json, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(() => PickupPolicy.Parse(json));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scope_may_name_a_project_with_spaces()
    {
        // The stored access list says "ado-project:{org}/{project}"; ADO project names often contain spaces.
        const string Scope = "ado-project:https://dev.azure.com/WLMTS/Genoa Kiosk";
        var policy = PickupPolicy.Parse($$"""{"policy_revision": "1", "projects": [{"project": "Genoa Kiosk", "project_id": "genoa-kiosk", "defect_types": ["Bug"], "pickup_states": ["Doing"], "repository_id": "r", "security_scope_id": "{{Scope}}"}]}""");

        Assert.Equal(Scope, Assert.Single(policy.Projects).SecurityScopeId);
    }

    [Theory]
    [InlineData(" ado-project:x/P")]
    [InlineData("ado-project:x/P ")]
    [InlineData("ado-project:x/Genoa  Kiosk")]
    [InlineData("ado-project:x/Genoa\tKiosk")]
    public void A_scope_with_stray_whitespace_is_refused(string scope)
    {
        var json = $$"""{"policy_revision": "1", "projects": [{"project": "P", "project_id": "p", "defect_types": ["Bug"], "pickup_states": ["Doing"], "repository_id": "r", "security_scope_id": {{System.Text.Json.JsonSerializer.Serialize(scope)}}}]}""";

        var error = Assert.Throws<InvalidOperationException>(() => PickupPolicy.Parse(json));

        Assert.Contains("security_scope_id", error.Message, StringComparison.Ordinal);
    }
}
