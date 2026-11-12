using System.Text.Json.Nodes;
using VisionIQ.Api.Features.Briefs;

namespace VisionIQ.Api.Tests;

/// <summary>Shared BRF-05 / BRF-06 test inputs: a stored brief as Intelligence writes it, and ADO comment events.</summary>
internal static class BriefTestData
{
    public static readonly Guid RunId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
    public static readonly Guid ResultId = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

    /// <summary>A brief shaped exactly like contracts/briefs/brief.v1.schema.json.</summary>
    public static JsonObject Brief(string state = "PUBLISHED") => new()
    {
        ["schema_version"] = 1,
        ["brief_id"] = ResultId.ToString(),
        ["run_id"] = RunId.ToString(),
        ["ticket"] = new JsonObject
        {
            ["system"] = "ado", ["project"] = "payments-core", ["id"] = "4711", ["revision"] = 7,
            ["title"] = "Bug 4711: Barcode not printed after payment retry",
        },
        ["state"] = state,
        ["reason_codes"] = new JsonArray(),
        ["snapshot_id"] = "snap-1",
        ["predicted_scope"] = new JsonArray(
            new JsonObject { ["path"] = "RetryPolicy.cs", ["basis"] = "named_in_ticket", ["confidence"] = "exact", ["evidence_ids"] = new JsonArray() },
            new JsonObject { ["path"] = "/src/Kiosk/ReceiptPrinter.cs", ["basis"] = "changed_by_past_fix", ["confidence"] = "inferred", ["evidence_ids"] = new JsonArray("E1") }),
        ["risk"] = new JsonObject
        {
            ["band"] = "MODERATE",
            ["explanation"] = "2 defect fix(es) changed these files in the last 90 days, 1 of them later reverted.",
            ["factors"] = new JsonArray(
                new JsonObject
                {
                    ["code"] = "recent_fixes", ["label"] = "Defect fixes that changed these files in the last 90 days",
                    ["value"] = 2, ["window_days"] = 90, ["missing"] = false,
                    ["references"] = new JsonArray("ticket 101 -> PR 201", "ticket 102 -> PR 202 (reverted)"),
                    ["evidence_ids"] = new JsonArray("E1", "E2"),
                },
                new JsonObject
                {
                    ["code"] = "hotspot_areas", ["label"] = "Areas flagged by the latest hotspot sweep",
                    ["value"] = null, ["window_days"] = null, ["missing"] = true,
                    ["references"] = new JsonArray(), ["evidence_ids"] = new JsonArray(),
                }),
        },
        ["advice"] = new JsonArray(
            Advice("ADV-001", "APPROACH", "Make the payment retry idempotent before changing the printer.", "E1"),
            Advice("ADV-002", "DONT", "Do not reuse the retry counter fix <b>as is</b>: it was reverted.", "E2", "DISPUTED")),
        ["evidence"] = new JsonArray(
            Evidence("E1", "Bug 101: Barcode print fails after payment retry", "https://dev.azure.com/contoso/Payments%20Core/_workitems/edit/101", "strong"),
            Evidence("E2", "Bug 102: Duplicate receipt <script>alert(1)</script>", "https://dev.azure.com/contoso/Payments%20Core/_workitems/edit/102", "strong",
                "A fix linked to this ticket was later reverted."),
            Evidence("E3", "Commit 9f1c2ab: raise printer timeout", "javascript:alert(1)", "related")),
        ["disagreements"] = new JsonArray(
            new JsonObject
            {
                ["advice_id"] = "ADV-002", ["drafted_by"] = "gemini", ["reviewed_by"] = "claude",
                ["verdict"] = "conflicting", ["reason"] = "E2 only says the fix was merged.",
            }),
        ["limitations"] = new JsonArray("Some source text contains instructions addressed to an AI assistant. It was treated as data and not followed."),
        ["injection_flags"] = new JsonArray(new JsonObject { ["source"] = "ticket", ["pattern"] = "override_instructions" }),
        ["generation"] = new JsonObject
        {
            ["provider"] = "gemini", ["model_id"] = "gemini-2.5-flash", ["prompt_template_version"] = "brief.defect@v1",
            ["routing_revision"] = "routing-2026-11-10.1", ["reviewed_by"] = "claude", ["calls"] = 2,
            ["input_tokens"] = 1800, ["output_tokens"] = 400,
        },
        ["timings"] = new JsonObject { ["retrieval_ms"] = 300, ["generation_ms"] = 9000, ["total_ms"] = 9600 },
    };

    public static BriefTarget Target(string state = "PUBLISHED", params string[] reasons) =>
        new(RunId, ResultId, "payments-core", "4711", 7, state, reasons);

    /// <summary>A "Work item commented" service-hook body (resource details: All) on work item 4711.</summary>
    public static JsonObject Commented(
        string text, string eventId = "c0mment-0000-4000-8000-000000000001", string actor = "dev@contoso.example",
        string project = "Payments Core", string organization = PickupTestData.Organization, string? actorId = "a1b2c3") => new()
    {
        ["id"] = eventId,
        ["eventType"] = "workitem.commented",
        ["publisherId"] = "tfs",
        ["createdDate"] = "2026-11-10T09:30:00Z",
        ["resource"] = new JsonObject
        {
            ["id"] = 4711,
            ["rev"] = 9,
            ["fields"] = new JsonObject
            {
                ["System.TeamProject"] = project,
                ["System.WorkItemType"] = "Bug",
                ["System.State"] = "In Progress",
                ["System.History"] = text,
                ["System.ChangedDate"] = "2026-11-10T09:29:58.12Z",
                ["System.ChangedBy"] = actorId is null
                    ? $"Dev <{actor}>"
                    : new JsonObject { ["id"] = actorId, ["uniqueName"] = actor, ["displayName"] = "Dev" },
            },
        },
        ["resourceVersion"] = "1.0",
        ["resourceContainers"] = new JsonObject
        {
            ["collection"] = new JsonObject { ["id"] = "c0ffee", ["baseUrl"] = organization },
        },
    };

    private static JsonObject Advice(string id, string kind, string statement, string evidence, string state = "UNVERIFIED") => new()
    {
        ["advice_id"] = id,
        ["kind"] = kind,
        ["statement"] = statement,
        ["evidence_ids"] = new JsonArray(evidence),
        ["support"] = new JsonArray(new JsonObject { ["evidence_id"] = evidence, ["quote"] = "a verbatim quote" }),
        ["evidence_state"] = state,
    };

    private static JsonObject Evidence(string id, string title, string url, string relevance, params string[] notes) => new()
    {
        ["id"] = id,
        ["item_key"] = new string('a', 64),
        ["kind"] = "work_item_current",
        ["title"] = title,
        ["url"] = url,
        ["revision"] = "3",
        ["snippet"] = "Pay at kiosk, payment captured, retry printing: no barcode on the receipt.",
        ["relevance"] = relevance,
        ["notes"] = new JsonArray([.. notes.Select(n => (JsonNode)n)]),
    };
}
