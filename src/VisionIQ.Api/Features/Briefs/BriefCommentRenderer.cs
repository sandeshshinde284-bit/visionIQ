using System.Globalization;
using System.Net;
using System.Text;

namespace VisionIQ.Api.Features.Briefs;

/// <summary>What a brief comment is about: the run, its result and the ticket it goes on.</summary>
public sealed record BriefTarget(
    Guid RunId, Guid ResultId, string ProjectId, string TicketId, int TicketRevision, string ResultState,
    IReadOnlyList<string> ReasonCodes);

/// <summary>
/// S13-04: turns a stored brief into the HTML comment posted on the work item. Every value from the brief is
/// HTML-encoded and only http(s) links become anchors, so text that came from tickets, commits or pages can never
/// inject markup. The comment carries the run id and status, each advice item's id for rating (BRF-06), and a
/// marker line used to recognise VisionIQ's own comment again.
/// </summary>
public static class BriefCommentRenderer
{
    /// <summary>Prefix of the marker line; the feedback parser ignores any comment that contains it.</summary>
    public const string MarkerPrefix = "visioniq-brief";

    /// <summary>ADO accepts large comments, but a ticket comment this long is no longer a brief.</summary>
    public const int MaxLength = 60_000;

    private const int MaxEvidence = 12;
    private const int MaxScope = 8;
    private const int MaxOtherTitle = 90;

    // The order and labels are contract-owned, not taken from the stored model document.
    private static readonly (string Code, string Title)[] EnhancementSections =
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

    private static readonly Dictionary<string, string> Reasons = new(StringComparer.Ordinal)
    {
        ["ticket_not_readable"] = "the ticket is not in the knowledge base under this project's access scope yet",
        ["ticket_too_vague"] = "the ticket has too little detail to match history",
        ["no_history_found"] = "no similar past work was found",
        ["weak_evidence"] = "the matching history is thin, so treat the advice with care",
        ["no_supported_advice"] = "no advice was supported by its sources",
        ["llm_budget_exhausted"] = "the analysis budget ran out before advice could be written",
        ["llm_no_allowed_provider"] = "no AI model is approved for this data",
        ["llm_refused"] = "the AI model declined to answer",
        ["llm_unavailable"] = "the AI model was unavailable",
        ["analysis_failed"] = "the analysis failed and was stopped",
        ["deep_analysis_not_implemented"] = "deep analysis is not available yet",
    };

    /// <summary>The marker that identifies the comment of one run.</summary>
    public static string Marker(Guid runId) => $"{MarkerPrefix} run={runId:D}";

    /// <summary>Renders the comment. <paramref name="brief"/> is null when the run has no stored brief.</summary>
    /// <param name="offerStarter">ENH-02: say how to ask for a starter pull request (published guidance only).</param>
    public static string Render(BriefTarget target, BriefDocument? brief, bool offerStarter = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        var html = new StringBuilder();
        var status = target.ResultState switch
        {
            "PUBLISHED" => "Published",
            "PARTIAL" => "Partial",
            _ => "Blocked",
        };
        // ENH-01: an enhancement gets guidance in eight sections instead of a flat advice list.
        var guidance = brief is { IsEnhancementGuidance: true };
        html.Append("<div><b>").Append(guidance ? "VisionIQ enhancement guidance" : "VisionIQ brief")
            .Append("</b> &middot; ").Append(status);
        if (brief is not null)
        {
            html.Append(" &middot; Risk: <b>").Append(Encode(RiskLabel(brief.Risk.Band))).Append("</b>");
        }

        html.Append("</div>");
        // The answer in one sentence, before any detail.
        if (brief is not null && !string.IsNullOrWhiteSpace(brief.Risk.Explanation))
        {
            html.Append("<p><i>").Append(Encode(brief.Risk.Explanation)).Append("</i></p>");
        }

        if (target.ReasonCodes.Count > 0)
        {
            html.Append("<p><i>").Append(status).Append(" because ")
                .Append(Encode(string.Join("; ", target.ReasonCodes.Select(Explain)))).Append(".</i></p>");
        }

        if (brief is null)
        {
            html.Append("<p>No brief is available for this run.</p>");
            AppendFooter(html, target, canRateAdvice: false);
            return Finish(html, target);
        }

        // What to act on first; the background (files, risk, evidence) after it; run details last.
        var advice = brief.Advice ?? [];
        var disputes = (brief.Disagreements ?? []).ToLookup(d => d.AdviceId, StringComparer.Ordinal);
        bool canRateAdvice;
        HashSet<string> usedEvidence;
        if (guidance)
        {
            canRateAdvice = AppendGuidance(html, brief);
            usedEvidence = EvidenceOf(advice);
        }
        else
        {
            var confirmed = advice.Where(a => !disputes.Contains(a.AdviceId)).ToList();
            AppendAdvice(html, brief, confirmed, disputes);
            canRateAdvice = advice.Count > 0;
            usedEvidence = EvidenceOf(confirmed);
        }

        AppendScope(html, brief.PredictedScope ?? []);
        AppendRisk(html, brief.Risk);
        AppendEvidence(html, brief.Evidence ?? [], usedEvidence);
        if (!guidance)
        {
            AppendDisputed(html, brief, disputes);
        }

        AppendLimits(html, brief);
        if (offerStarter && brief is { IsEnhancementGuidance: true } && target.ResultState == "PUBLISHED")
        {
            html.Append("<p><b>Want a starter pull request?</b> Reply on this ticket with a line ")
                .Append("<code>").Append(Starters.StarterCommand.Text).Append("</code>. ")
                .Append("If the change is understood well enough, VisionIQ opens a draft pull request with a scaffold to begin from. ")
                .Append("It is optional: nothing is built or tested, and the bot cannot approve or merge it.</p>");
        }

        AppendFooter(html, target, canRateAdvice);
        return Finish(html, target);
    }

    private static HashSet<string> EvidenceOf(IEnumerable<BriefAdvice> advice) =>
        advice.SelectMany(a => a.EvidenceIds ?? []).ToHashSet(StringComparer.Ordinal);

    private static void AppendRisk(StringBuilder html, BriefRisk risk)
    {
        var factors = risk.Factors ?? [];
        if (factors.Count == 0)
        {
            return;
        }

        html.Append("<p><b>Why the risk is ").Append(Encode(RiskLabel(risk.Band))).Append("</b></p><ul>");
        var missing = new List<string>();
        foreach (var factor in factors)
        {
            // A factor without data is never shown as a count: it is listed once, as not available.
            if (factor.Missing || factor.Value is null)
            {
                missing.Add(factor.Label);
                continue;
            }

            html.Append("<li>").Append(Encode(factor.Label)).Append(": <b>")
                .Append(factor.Value.Value.ToString(CultureInfo.InvariantCulture)).Append("</b>");
            var references = (factor.References ?? []).Distinct(StringComparer.Ordinal).ToList();
            if (references.Count > 0)
            {
                html.Append(" (").Append(Encode(string.Join("; ", references))).Append(')');
            }

            html.Append("</li>");
        }

        if (missing.Count > 0)
        {
            html.Append("<li><i>Not available:</i> ").Append(Encode(string.Join("; ", missing))).Append("</li>");
        }

        html.Append("</ul>");
    }

    private static void AppendAdvice(
        StringBuilder html, BriefDocument brief, List<BriefAdvice> confirmed, ILookup<string, BriefDisagreement> disputes)
    {
        if ((brief.Advice ?? []).Count == 0)
        {
            html.Append("<p><b>Advice</b>: none. VisionIQ does not guess when the history does not support advice.</p>");
            return;
        }

        if (confirmed.Count == 0)
        {
            html.Append("<p><b>Advice</b>: none confirmed. A second review questioned every item (listed below).</p>");
            return;
        }

        var evidence = (brief.Evidence ?? []).ToDictionary(e => e.Id, StringComparer.Ordinal);
        html.Append("<p><b>Advice</b></p><ul>");
        foreach (var item in confirmed)
        {
            AppendItem(html, item, evidence, disputes);
        }

        html.Append("</ul>");
    }

    /// <summary>Advice a second review questioned: kept for transparency, apart from the advice to act on.</summary>
    private static void AppendDisputed(StringBuilder html, BriefDocument brief, ILookup<string, BriefDisagreement> disputes)
    {
        var items = (brief.Advice ?? []).Where(a => disputes.Contains(a.AdviceId)).ToList();
        if (items.Count == 0)
        {
            return;
        }

        var evidence = (brief.Evidence ?? []).ToDictionary(e => e.Id, StringComparer.Ordinal);
        html.Append("<p><b>Not confirmed by a second review</b> (shown for transparency; treat with care)</p><ul>");
        foreach (var item in items)
        {
            AppendItem(html, item, evidence, disputes);
        }

        html.Append("</ul>");
    }

    private static void AppendFooter(StringBuilder html, BriefTarget target, bool canRateAdvice)
    {
        html.Append("<p><small>");
        if (canRateAdvice)
        {
            html.Append("<b>Rate the advice</b> (optional): reply on this ticket with what you did, one line per item: ")
                .Append("<code>ADV-001 accepted</code>, <code>ADV-002 rejected: why</code>, ")
                .Append("<code>ADV-003 modified: what you did instead</code> or <code>ADV-004 n/a</code>. ")
                .Append("It is feedback only: VisionIQ records it to learn which advice helps. It does not create a pull request, ")
                .Append("start a run or mark the advice as correct.<br>");
        }

        html.Append("Run <code>").Append(target.RunId.ToString("D")).Append("</code> &middot; ticket revision ")
            .Append(target.TicketRevision.ToString(CultureInfo.InvariantCulture)).Append("</small></p>");
    }

    /// <summary>
    /// ENH-01: the eight sections of master design 8.5, numbered and in order. A section nothing supported is shown
    /// as missing: it is never filled in, and never dropped, so the engineer sees what the history does not cover.
    /// </summary>
    private static bool AppendGuidance(StringBuilder html, BriefDocument brief)
    {
        // Stored documents may be incomplete or malformed. Never let them reorder sections,
        // attach an advice item to another section, or claim unsupported advice as covered.
        var advice = (brief.Advice ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a.AdviceId))
            .GroupBy(a => a.AdviceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var evidence = (brief.Evidence ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e.Id))
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var disputes = (brief.Disagreements ?? []).ToLookup(d => d.AdviceId, StringComparer.Ordinal);
        var sections = (brief.Guidance ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.Section))
            .GroupBy(s => s.Section, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var body = new StringBuilder();
        var covered = 0;
        for (var index = 0; index < EnhancementSections.Length; index++)
        {
            var (code, title) = EnhancementSections[index];
            sections.TryGetValue(code, out var section);
            var items = (section?.AdviceIds ?? [])
                .Distinct(StringComparer.Ordinal)
                .Where(id => advice.TryGetValue(id, out var item)
                    && string.Equals(item.Section, code, StringComparison.Ordinal)
                    && item.EvidenceIds is { Count: > 0 }
                    && item.EvidenceIds.All(evidence.ContainsKey))
                .Select(id => advice[id])
                .ToList();
            body.Append("<p><b>").Append((index + 1).ToString(CultureInfo.InvariantCulture)).Append(". ")
                .Append(Encode(title)).Append("</b>");
            if (items.Count == 0)
            {
                body.Append(" &mdash; <i>missing</i></p>");
                continue;
            }

            covered++;
            body.Append("</p><ul>");
            foreach (var item in items)
            {
                AppendItem(body, item, evidence, disputes);
            }

            body.Append("</ul>");
        }

        html.Append("<p><b>Guidance</b> &mdash; ").Append(covered.ToString(CultureInfo.InvariantCulture))
            .Append(" of ").Append(EnhancementSections.Length.ToString(CultureInfo.InvariantCulture))
            .Append(" sections are supported by the history found.");
        if (covered < EnhancementSections.Length)
        {
            html.Append(" A section marked <i>missing</i> has no supporting evidence: VisionIQ does not fill it with a guess.");
        }

        html.Append("</p>").Append(body);
        return covered > 0;
    }

    private static void AppendItem(
        StringBuilder html, BriefAdvice item, Dictionary<string, BriefEvidence> evidence,
        ILookup<string, BriefDisagreement> disputes)
    {
        html.Append("<li><b>").Append(Encode(item.AdviceId)).Append("</b> [").Append(KindLabel(item.Kind)).Append("] ")
            .Append(Encode(item.Statement)).Append(" <i>Source: ");
        var sources = (item.EvidenceIds ?? []).Select(id =>
            evidence.TryGetValue(id, out var source) ? Link(source.Url, id) : Encode(id));
        html.Append(string.Join(", ", sources)).Append("</i>");
        foreach (var dispute in disputes[item.AdviceId])
        {
            html.Append("<br><i>A second review found this ").Append(Encode(dispute.Verdict)).Append(": ")
                .Append(Encode(dispute.Reason)).Append("</i>");
        }

        html.Append("</li>");
    }

    private static void AppendScope(StringBuilder html, IReadOnlyList<BriefScopeItem> scope)
    {
        if (scope.Count == 0)
        {
            return;
        }

        html.Append("<p><b>Likely affected files</b></p><ul>");
        foreach (var item in scope.Take(MaxScope))
        {
            html.Append("<li><code>").Append(Encode(item.Path)).Append("</code> (")
                .Append(item.Basis == "named_in_ticket" ? "named in the ticket" : "changed by past fixes of similar tickets")
                .Append(")</li>");
        }

        html.Append("</ul>");
    }

    private static void AppendEvidence(StringBuilder html, IReadOnlyList<BriefEvidence> evidence, HashSet<string> used)
    {
        if (evidence.Count == 0)
        {
            return;
        }

        var shown = evidence.Take(MaxEvidence).ToList();
        var primary = shown.Where(e => used.Contains(e.Id)).ToList();
        var other = shown.Where(e => !used.Contains(e.Id)).ToList();
        if (primary.Count == 0)
        {
            html.Append("<p><b>Evidence</b></p><ul>");
            other.ForEach(item => AppendEvidenceItem(html, item, shorten: false));
        }
        else
        {
            html.Append("<p><b>Evidence used by the advice</b></p><ul>");
            primary.ForEach(item => AppendEvidenceItem(html, item, shorten: false));
            if (other.Count > 0)
            {
                html.Append("</ul><p><b>Other matches</b> (found by the search, not used by the advice)</p><ul>");
                other.ForEach(item => AppendEvidenceItem(html, item, shorten: true));
            }
        }

        if (evidence.Count > MaxEvidence)
        {
            html.Append("<li>and ").Append((evidence.Count - MaxEvidence).ToString(CultureInfo.InvariantCulture))
                .Append(" more</li>");
        }

        html.Append("</ul>");
    }

    private static void AppendEvidenceItem(StringBuilder html, BriefEvidence item, bool shorten)
    {
        var title = item.Title ?? string.Empty;
        if (shorten && title.Length > MaxOtherTitle)
        {
            title = title[..(MaxOtherTitle - 1)].TrimEnd() + "\u2026";
        }

        html.Append("<li>").Append(Link(item.Url, item.Id)).Append(" &mdash; ").Append(Encode(title))
            .Append(" (").Append(Encode(item.Kind.Replace('_', ' '))).Append(", ")
            .Append(item.Relevance == "strong" ? "strong match" : "related").Append(')');
        foreach (var note in item.Notes ?? [])
        {
            html.Append("<br><i>").Append(Encode(note)).Append("</i>");
        }

        html.Append("</li>");
    }

    private static void AppendLimits(StringBuilder html, BriefDocument brief)
    {
        var limits = brief.Limitations ?? [];
        if (limits.Count == 0)
        {
            return;
        }

        html.Append("<p><b>Limits of this brief</b></p><ul>");
        foreach (var limit in limits)
        {
            html.Append("<li>").Append(Encode(limit)).Append("</li>");
        }

        html.Append("</ul>");
    }

    private static string Finish(StringBuilder html, BriefTarget target)
    {
        var marker = "<p><small>" + Marker(target.RunId) + " result=" + target.ResultId.ToString("D") + "</small></p>";
        const string note = "<p><i>(shortened)</i></p>";
        if (html.Length + marker.Length > MaxLength)
        {
            // Cut after a complete list item or paragraph, never inside a tag, attribute or entity: a
            // broken tag could swallow the marker, which is what lets a retry find this comment again
            // and stops the brief from being read as a rating.
            var text = html.ToString(0, MaxLength - marker.Length - note.Length);
            var cut = Math.Max(text.LastIndexOf("</li>", StringComparison.Ordinal), text.LastIndexOf("</p>", StringComparison.Ordinal));
            html.Length = cut < 0 ? 0 : text.IndexOf('>', cut) + 1;
            html.Append(note);
        }

        return html.Append(marker).ToString();
    }

    private static string Explain(string reason) =>
        Reasons.TryGetValue(reason, out var text) ? text : reason.Replace('_', ' ');

    private static string RiskLabel(string band) => band switch
    {
        "LOW" => "Low",
        "MODERATE" => "Moderate",
        "HIGH" => "High",
        _ => "Not enough data",
    };

    private static string KindLabel(string kind) => kind switch
    {
        "RISK" => "Risk",
        "DO" => "Do",
        "DONT" => "Don&#39;t",
        "APPROACH" => "Approach",
        _ => "Note",
    };

    /// <summary>An anchor for an http(s) URL, else just the encoded text: other schemes never become links.</summary>
    private static string Link(string? url, string text) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? "<a href=\"" + Encode(uri.AbsoluteUri) + "\">" + Encode(text) + "</a>"
            : Encode(text);

    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
}
