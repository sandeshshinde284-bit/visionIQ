using System.Text.Json;
using System.Text.Json.Nodes;
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

    // ---- ENH-01: enhancement guidance (master design 8.5) ---------------------------------------------------

    private static readonly (string Code, string Title)[] Sections =
    [
        ("acceptance_criteria_gaps", "Acceptance-criteria gaps"),
        ("contracts_and_apis", "Contracts and APIs touched"),
        ("deployment_and_migration_risk", "Deployment and migration risk"),
        ("related_past_changes", "Related past changes"),
        ("do_and_dont", "Do / don't"),
        ("recommended_sequence", "Recommended sequence"),
        ("tests_to_add", "Tests to add"),
        ("reviewer_decisions", "Reviewer decisions"),
    ];

    /// <summary>The test brief as an enhancement: ADV-001 in "related past changes", ADV-002 in "do / don't".</summary>
    private static JsonObject Guidance(bool withAdvice = true)
    {
        var brief = Brief();
        brief["kind"] = "enhancement";
        var placed = new Dictionary<string, string>(StringComparer.Ordinal);
        if (withAdvice)
        {
            placed["related_past_changes"] = "ADV-001";
            placed["do_and_dont"] = "ADV-002";
            brief["advice"]![0]!["section"] = "related_past_changes";
            brief["advice"]![1]!["section"] = "do_and_dont";
        }
        else
        {
            brief["advice"] = new JsonArray();
            brief["disagreements"] = new JsonArray();
        }

        var sections = new JsonArray();
        foreach (var (code, title) in Sections)
        {
            var ids = new JsonArray();
            if (placed.TryGetValue(code, out var id))
            {
                ids.Add(id);
            }

            sections.Add(new JsonObject
            {
                ["section"] = code,
                ["title"] = title,
                ["status"] = ids.Count > 0 ? "covered" : "missing",
                ["advice_ids"] = ids,
            });
        }

        brief["guidance"] = sections;
        return brief;
    }

    private static string RenderGuidance(JsonObject brief, string state = "PUBLISHED", params string[] reasons) =>
        BriefCommentRenderer.Render(Target(state, reasons), BriefDocument.Parse(brief.ToJsonString()));

    [Fact]
    public void The_enhancement_test_brief_is_a_valid_version_1_brief()
    {
        var path = Path.Combine(EventContractTests.ContractsDirectory, "..", "briefs", "brief.v1.schema.json");
        var schema = JsonSchema.FromText(File.ReadAllText(path));

        var result = schema.Evaluate(JsonDocument.Parse(Guidance().ToJsonString()).RootElement);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void An_enhancement_gets_guidance_in_eight_numbered_sections()
    {
        var html = RenderGuidance(Guidance());

        Assert.Contains("<b>VisionIQ enhancement guidance</b> &middot; Published &middot; Risk: <b>Moderate</b>", html, StringComparison.Ordinal);
        Assert.Contains("<b>Guidance</b> &mdash; 2 of 8 sections are supported by the history found.", html, StringComparison.Ordinal);
        // Each item keeps its advice id, kind and source link, under the section it belongs to.
        Assert.Contains("<p><b>4. Related past changes</b></p><ul><li><b>ADV-001</b> [Approach] ", html, StringComparison.Ordinal);
        Assert.Contains("<p><b>5. Do / don&#39;t</b></p><ul><li><b>ADV-002</b> [Don&#39;t] ", html, StringComparison.Ordinal);
        Assert.Contains("A second review found this conflicting", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<p><b>Advice</b></p>", html, StringComparison.Ordinal);
        Assert.Contains("Rate the advice", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_section_nothing_supports_is_shown_as_missing_and_never_dropped()
    {
        var html = RenderGuidance(Guidance());

        foreach (var (number, title) in new[]
                 {
                     (1, "Acceptance-criteria gaps"), (2, "Contracts and APIs touched"),
                     (3, "Deployment and migration risk"), (6, "Recommended sequence"), (7, "Tests to add"),
                     (8, "Reviewer decisions"),
                 })
        {
            Assert.Contains($"<p><b>{number}. {title}</b> &mdash; <i>missing</i></p>", html, StringComparison.Ordinal);
        }

        Assert.Contains("VisionIQ does not fill it with a guess", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Guidance_with_nothing_supported_lists_all_eight_as_missing_and_offers_nothing_to_rate()
    {
        var html = RenderGuidance(Guidance(withAdvice: false), "PARTIAL", "no_supported_advice");

        Assert.Contains("<b>Guidance</b> &mdash; 0 of 8 sections are supported by the history found.", html, StringComparison.Ordinal);
        Assert.Equal(8, html.Split("&mdash; <i>missing</i></p>", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("Rate the advice", html, StringComparison.Ordinal);
    }

    [Fact]
    public void An_advice_id_a_section_names_but_the_brief_does_not_have_is_not_shown()
    {
        var brief = Guidance();
        brief["guidance"]![0]!["advice_ids"] = new JsonArray("ADV-099");
        brief["guidance"]![0]!["status"] = "covered";

        var html = RenderGuidance(brief);

        Assert.Contains("<p><b>1. Acceptance-criteria gaps</b> &mdash; <i>missing</i></p>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ADV-099", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_defect_brief_is_rendered_as_before_when_it_states_its_kind()
    {
        var brief = Brief();
        brief["kind"] = "defect";
        brief["guidance"] = new JsonArray();

        var html = RenderGuidance(brief);

        Assert.Contains("<b>VisionIQ brief</b> &middot; Published", html, StringComparison.Ordinal);
        Assert.Contains("<p><b>Advice</b></p><ul>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("enhancement guidance", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_newer_brief_with_extra_fields_still_renders()
    {
        var brief = Brief();
        brief["a_field_added_later"] = "ignored";

        Assert.NotNull(BriefDocument.Parse(brief.ToJsonString()));
    }
}
