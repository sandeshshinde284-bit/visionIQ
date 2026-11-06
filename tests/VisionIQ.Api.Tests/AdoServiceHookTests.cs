using System.Text;
using System.Text.Json.Nodes;
using VisionIQ.Api.Features.Pickup;
using static VisionIQ.Api.Tests.PickupTestData;

namespace VisionIQ.Api.Tests;

/// <summary>S04-02: reading ADO "Work item created/updated" service-hook bodies.</summary>
public sealed class AdoServiceHookTests
{
    [Fact]
    public void Updated_body_yields_the_state_transition_and_everything_the_rules_need()
    {
        var delivery = Parse(Updated())!;

        Assert.Equal("8b6e2c3a-0000-4000-8000-000000000001", delivery.EventId);
        var change = delivery.Change;
        Assert.Equal((Organization, "Payments Core", 4711, 7, "Bug"), (change.OrganizationUrl, change.Project, change.WorkItemId, change.Revision, change.WorkItemType));
        Assert.Equal(("New", "In Progress"), (change.StateBefore, change.StateAfter));
        Assert.True(change.Assigned);
        Assert.Equal(["backend", "payments"], change.Tags);
        Assert.Equal(("a1b2c3", "dev@contoso.example"), (change.ChangedById, change.ChangedByName));
        Assert.True(Policy.Evaluate(change).Eligible);
    }

    [Fact]
    public void An_update_without_a_state_change_is_not_a_transition()
    {
        var change = Parse(Updated(oldState: "In Progress"))!.Change;

        Assert.Equal(("In Progress", "In Progress"), (change.StateBefore, change.StateAfter));
        Assert.Equal("no_pickup_transition", Policy.Evaluate(change).Reason);
    }

    [Fact]
    public void Created_body_has_no_previous_state_and_reads_older_identity_strings()
    {
        var body = new JsonObject
        {
            ["id"] = "evt-created",
            ["eventType"] = "workitem.created",
            ["resource"] = new JsonObject
            {
                ["id"] = 4800,
                ["rev"] = 1,
                ["fields"] = new JsonObject
                {
                    ["System.TeamProject"] = "Payments Core",
                    ["System.WorkItemType"] = "User Story",
                    ["System.State"] = "In Progress",
                    ["System.AssignedTo"] = "Owner <owner@contoso.example>",
                    ["System.ChangedBy"] = "VisionIQ Bot <visioniq-bot@contoso.example>",
                },
            },
            ["resourceContainers"] = new JsonObject { ["account"] = new JsonObject { ["baseUrl"] = Organization } },
        };

        var change = Parse(body)!.Change;

        Assert.Equal((4800, 1, (string?)null, "In Progress"), (change.WorkItemId, change.Revision, change.StateBefore, change.StateAfter));
        Assert.True(change.Assigned);
        Assert.Equal("visioniq-bot@contoso.example", change.ChangedByName);
        Assert.Equal("bot_update", Policy.Evaluate(change).Reason);
    }

    [Fact]
    public void Unassigned_items_are_read_as_unassigned()
    {
        Assert.False(Parse(Updated(assigned: false))!.Change.Assigned);
    }

    [Fact]
    public void An_assignment_while_in_progress_is_read_as_a_pickup()
    {
        var body = Updated(oldState: "In Progress");
        // ADO lists a newly set field with newValue only.
        body["resource"]!["fields"]!["System.AssignedTo"] = new JsonObject { ["newValue"] = "Owner <owner@contoso.example>" };

        var change = Parse(body)!.Change;

        Assert.Equal((false, true), (change.AssignedBefore, change.Assigned));
        Assert.True(Policy.Evaluate(change).Eligible);
    }

    [Fact]
    public void A_null_revisedBy_falls_back_to_ChangedBy_so_bot_updates_are_still_caught()
    {
        var body = Updated();
        body["resource"]!["revisedBy"] = null;
        body["resource"]!["revision"]!["fields"]!["System.ChangedBy"] = new JsonObject { ["id"] = "b0t", ["uniqueName"] = Bot };

        var change = Parse(body)!.Change;

        Assert.Equal(("b0t", Bot), (change.ChangedById, change.ChangedByName));
        Assert.Equal("bot_update", Policy.Evaluate(change).Reason);
    }

    [Fact]
    public void Unchanged_assignment_keeps_its_current_value_as_the_previous_one()
    {
        Assert.True(Parse(Updated())!.Change.AssignedBefore);
    }

    [Theory]
    [InlineData("not json", "body_not_json")]
    [InlineData("[]", "not_a_service_hook")]
    [InlineData("""{"eventType": "workitem.updated", "resource": {}}""", "not_a_service_hook")]
    [InlineData("""{"id": "e", "eventType": "git.push", "resource": {}}""", "unsupported_event")]
    [InlineData("""{"id": "e", "eventType": "workitem.updated", "resource": {"workItemId": 1, "rev": 2}}""", "resource_details_missing")]
    [InlineData("""{"id": "e", "eventType": "workitem.updated", "resource": {"workItemId": 1, "revision": {"rev": 2, "fields": {}}}}""", "resource_details_missing")]
    public void Unusable_bodies_get_a_reason_code(string body, string reason)
    {
        Assert.Null(AdoServiceHook.Parse(Encoding.UTF8.GetBytes(body), out var problem));
        Assert.Equal(reason, problem);
    }

    private static ServiceHookDelivery? Parse(JsonNode body)
    {
        var delivery = AdoServiceHook.Parse(Encoding.UTF8.GetBytes(body.ToJsonString()), out var problem);
        Assert.True(delivery is not null, problem);
        return delivery;
    }
}
