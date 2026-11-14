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
    public static string Render(BriefTarget target, BriefDocument? brief)
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

        html.Append("<br>Run <code>").Append(target.RunId.ToString("D")).Append("</code> &middot; ticket revision ")
            .Append(target.TicketRevision.ToString(CultureInfo.InvariantCulture)).Append("</div>");

        if (target.ReasonCodes.Count > 0)
        {
            html.Append("<p><i>").Append(status).Append(" because ")
                .Append(Encode(string.Join("; ", target.ReasonCodes.Select(Explain)))).Append(".</i></p>");
        }

        if (brief is null)
        {
            html.Append("<p>No brief is available for this run.</p>");
            return Finish(html, target);
        }

        AppendRisk(html, brief.Risk);
        if (guidance)
        {
            AppendGuidance(html, brief);
        }
        else
        {
            AppendAdvice(html, brief);
        }

        AppendScope(html, brief.PredictedScope ?? []);
        AppendEvidence(html, brief.Evidence ?? []);
        AppendLimits(html, brief);
        if (brief.Advice is { Count: > 0 })
        {
            html.Append("<p><b>Rate the advice</b> by replying on this ticket, one line per item: ")
                .Append("<code>ADV-001 accepted</code>, <code>ADV-002 rejected: why</code>, ")
                .Append("<code>ADV-003 modified: what you did instead</code> or <code>ADV-004 n/a</code>. ")
                .Append("A rating records what you did with the advice; it does not mark the advice as correct.</p>");
        }

        return Finish(html, target);
    }

    private static void AppendRisk(StringBuilder html, BriefRisk risk)
    {
        html.Append("<p><b>Risk: ").Append(Encode(RiskLabel(risk.Band))).Append("</b> &mdash; ")
            .Append(Encode(risk.Explanation)).Append("</p>");
        var factors = risk.Factors ?? [];
        if (factors.Count == 0)
        {
            return;
        }

        html.Append("<ul>");
        foreach (var factor in factors)
        {
            html.Append("<li>").Append(Encode(factor.Label)).Append(": <b>")
                .Append(factor.Missing || factor.Value is null
                    ? "no data"
                    : factor.Value.Value.ToString(CultureInfo.InvariantCulture))
                .Append("</b>");
            if (factor.References is { Count: > 0 } references)
            {
                html.Append(" (").Append(Encode(string.Join("; ", references))).Append(')');
            }

            html.Append("</li>");
        }

        html.Append("</ul>");
    }

    private static void AppendAdvice(StringBuilder html, BriefDocument brief)
    {
        var advice = brief.Advice ?? [];
        if (advice.Count == 0)
        {
            html.Append("<p><b>Advice</b>: none. VisionIQ does not guess when the history does not support advice.</p>");
            return;
        }

        var evidence = (brief.Evidence ?? []).ToDictionary(e => e.Id, StringComparer.Ordinal);
        var disputes = (brief.Disagreements ?? []).ToLookup(d => d.AdviceId, StringComparer.Ordinal);
        html.Append("<p><b>Advice</b></p><ul>");
        foreach (var item in advice)
        {
            AppendItem(html, item, evidence, disputes);
        }

        html.Append("</ul>");
    }

    /// <summary>
    /// ENH-01: the eight sections of master design 8.5, numbered and in order. A section nothing supported is shown
    /// as missing: it is never filled in, and never dropped, so the engineer sees what the history does not cover.
    /// </summary>
    private static void AppendGuidance(StringBuilder html, BriefDocument brief)
    {
        var advice = (brief.Advice ?? []).ToDictionary(a => a.AdviceId, StringComparer.Ordinal);
        var evidence = (brief.Evidence ?? []).ToDictionary(e => e.Id, StringComparer.Ordinal);
        var disputes = (brief.Disagreements ?? []).ToLookup(d => d.AdviceId, StringComparer.Ordinal);
        var sections = brief.Guidance ?? [];
        var body = new StringBuilder();
        var number = 0;
        var covered = 0;
        foreach (var section in sections)
        {
            number++;
            var items = (section.AdviceIds ?? []).Where(advice.ContainsKey).Select(id => advice[id]).ToList();
            body.Append("<p><b>").Append(number.ToString(CultureInfo.InvariantCulture)).Append(". ")
                .Append(Encode(section.Title)).Append("</b>");
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

        html.Append("<p><b>Guidance</b> &mdash; ").Append(covered.ToString(CultureInfo.InvariantCulture)).Append(" of ")
            .Append(sections.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" sections are supported by the history found.");
        if (covered < sections.Count)
        {
            html.Append(" A section marked <i>missing</i> has no supporting evidence: VisionIQ does not fill it with a guess.");
        }

        html.Append("</p>").Append(body);
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

    private static void AppendEvidence(StringBuilder html, IReadOnlyList<BriefEvidence> evidence)
    {
        if (evidence.Count == 0)
        {
            return;
        }

        html.Append("<p><b>Evidence</b></p><ul>");
        foreach (var item in evidence.Take(MaxEvidence))
        {
            html.Append("<li>").Append(Link(item.Url, item.Id)).Append(" &mdash; ").Append(Encode(item.Title))
                .Append(" (").Append(Encode(item.Kind.Replace('_', ' '))).Append(", ")
                .Append(item.Relevance == "strong" ? "strong match" : "related").Append(')');
            foreach (var note in item.Notes ?? [])
            {
                html.Append("<br><i>").Append(Encode(note)).Append("</i>");
            }

            html.Append("</li>");
        }

        if (evidence.Count > MaxEvidence)
        {
            html.Append("<li>and ").Append((evidence.Count - MaxEvidence).ToString(CultureInfo.InvariantCulture))
                .Append(" more</li>");
        }

        html.Append("</ul>");
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
