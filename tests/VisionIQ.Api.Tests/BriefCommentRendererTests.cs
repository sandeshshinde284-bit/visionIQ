using System.Text.Json;
using Json.Schema;
using VisionIQ.Api.Features.Briefs;
using static VisionIQ.Api.Tests.BriefTestData;

namespace VisionIQ.Api.Tests;

/// <summary>S13-04: the ticket comment shows status, risk, advice ids and sources, and never trusts brief text.</summary>
public sealed class BriefCommentRendererTests
{
    private static string Render(string state = "PUBLISHED", params string[] reasons) =>
        BriefCommentRenderer.Render(Target(state, reasons), BriefDocument.Parse(Brief(state).ToJsonString()));

    [Fact]
    public void The_test_brief_is_a_valid_version_1_brief()
    {
        // Parity with Intelligence: the document these tests render must match the shared schema.
        var path = Path.Combine(EventContractTests.ContractsDirectory, "..", "briefs", "brief.v1.schema.json");
        var schema = JsonSchema.FromText(File.ReadAllText(path));

        var result = schema.Evaluate(JsonDocument.Parse(Brief().ToJsonString()).RootElement);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void The_comment_carries_the_run_id_the_status_and_the_risk_band()
    {
        var html = Render();

        Assert.Contains("<b>VisionIQ brief</b> &middot; Published &middot; Risk: <b>Moderate</b>", html, StringComparison.Ordinal);
        Assert.Contains("Run <code>7c9e6679-7425-40de-944b-e07fc1f90ae7</code> &middot; ticket revision 7", html, StringComparison.Ordinal);
        Assert.Contains("2 defect fix(es) changed these files in the last 90 days", html, StringComparison.Ordinal);
        Assert.EndsWith(
            "<p><small>visioniq-brief run=7c9e6679-7425-40de-944b-e07fc1f90ae7 result=3f2504e0-4f89-41d3-9a0c-0305e82c3301</small></p>",
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_advice_item_shows_its_id_kind_and_a_link_to_its_source()
    {
        var html = Render();

        Assert.Contains(
            "<li><b>ADV-001</b> [Approach] Make the payment retry idempotent before changing the printer. <i>Source: " +
            "<a href=\"https://dev.azure.com/contoso/Payments%20Core/_workitems/edit/101\">E1</a></i></li>",
            html,
            StringComparison.Ordinal);
        Assert.Contains("<b>ADV-002</b> [Don&#39;t]", html, StringComparison.Ordinal);
        Assert.Contains("A second review found this conflicting: E2 only says the fix was merged.", html, StringComparison.Ordinal);
        Assert.Contains("<code>ADV-001 accepted</code>", html, StringComparison.Ordinal); // how to rate, inside ADO
    }

    [Fact]
    public void Risk_factors_show_their_counts_and_missing_data_is_not_shown_as_zero()
    {
        var html = Render();

        Assert.Contains(
            "<li>Defect fixes that changed these files in the last 90 days: <b>2</b> " +
            "(ticket 101 -&gt; PR 201; ticket 102 -&gt; PR 202 (reverted))</li>",
            html,
            StringComparison.Ordinal);
        Assert.Contains("<li>Areas flagged by the latest hotspot sweep: <b>no data</b></li>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_from_tickets_and_commits_cannot_inject_markup_or_script_links()
    {
        var html = Render();

        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Duplicate receipt &lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("fix &lt;b&gt;as is&lt;/b&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase); // not a link, and not echoed
        Assert.Contains("<li>E3 &mdash; Commit 9f1c2ab: raise printer timeout", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Scope_evidence_notes_and_limits_are_listed()
    {
        var html = Render();

        Assert.Contains("<code>RetryPolicy.cs</code> (named in the ticket)", html, StringComparison.Ordinal);
        Assert.Contains("<code>/src/Kiosk/ReceiptPrinter.cs</code> (changed by past fixes of similar tickets)", html, StringComparison.Ordinal);
        Assert.Contains("(work item current, strong match)<br><i>A fix linked to this ticket was later reverted.</i>", html, StringComparison.Ordinal);
        Assert.Contains("<b>Limits of this brief</b>", html, StringComparison.Ordinal);
        Assert.Contains("It was treated as data and not followed.", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PARTIAL", "weak_evidence", "Partial because the matching history is thin, so treat the advice with care.")]
    [InlineData("PARTIAL", "llm_budget_exhausted", "Partial because the analysis budget ran out before advice could be written.")]
    [InlineData("BLOCKED", "some_new_reason", "Blocked because some new reason.")]
    public void Partial_and_blocked_briefs_say_why(string state, string reason, string expected)
    {
        var html = Render(state, reason);

        Assert.Contains("&middot; " + (state == "PARTIAL" ? "Partial" : "Blocked"), html, StringComparison.Ordinal);
        Assert.Contains("<p><i>" + expected + "</i></p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_brief_without_advice_says_so_and_offers_nothing_to_rate()
    {
        var brief = Brief("PARTIAL");
        brief["advice"] = new System.Text.Json.Nodes.JsonArray();

        var html = BriefCommentRenderer.Render(
            Target("PARTIAL", "no_supported_advice"), BriefDocument.Parse(brief.ToJsonString()));

        Assert.Contains("<b>Advice</b>: none. VisionIQ does not guess", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Rate the advice", html, StringComparison.Ordinal);
        Assert.Contains("no advice was supported by its sources", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_without_a_stored_brief_still_gets_its_status_and_marker()
    {
        var html = BriefCommentRenderer.Render(Target("BLOCKED", "ticket_not_readable"), null);

        Assert.Contains("&middot; Blocked<br>Run <code>", html, StringComparison.Ordinal);
        Assert.Contains("No brief is available for this run.", html, StringComparison.Ordinal);
        Assert.Contains(BriefCommentRenderer.Marker(RunId), html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{"schema_version": 2, "state": "PUBLISHED", "ticket": {"project": "p", "id": "1", "revision": 1}, "risk": {"band": "LOW", "explanation": "x"}}""")]
    [InlineData("""{"schema_version": 1, "state": "PUBLISHED", "ticket": {"project": "p", "id": "1", "revision": 1}}""")]
    public void A_brief_this_service_cannot_read_is_not_rendered(string json)
    {
        Assert.Null(BriefDocument.Parse(json));
    }

    [Fact]
    public void A_newer_brief_with_extra_fields_still_renders()
    {
        var brief = Brief();
        brief["a_field_added_later"] = "ignored";

        Assert.NotNull(BriefDocument.Parse(brief.ToJsonString()));
    }
}
