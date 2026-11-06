using System.Net;
using System.Text.RegularExpressions;

namespace VisionIQ.Ingestor.Confluence;

public sealed record NormalizedPage(string Text, IReadOnlyList<PageSection> Sections, IReadOnlyDictionary<string, string> Properties);

/// <summary>
/// Turns Confluence storage format (XHTML with ac:/ri: macros) into plain text, sections by heading, and
/// key/value properties from two-column tables such as the page-properties block of a decision template (S05-07).
/// </summary>
public static partial class ConfluenceText
{
    public static NormalizedPage Normalize(string? storage)
    {
        try
        {
            return NormalizeCore(storage);
        }
        catch (RegexMatchTimeoutException)
        {
            // m-9: pathological page markup must not stall the sync. Keep the page (identity,
            // version, ACL) but no text, sections or properties from this version.
            return new NormalizedPage(string.Empty, [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static NormalizedPage NormalizeCore(string? storage)
    {
        var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(storage))
        {
            return new NormalizedPage(string.Empty, [], empty);
        }

        // Keep code-block content; drop macro configuration, which is not page content.
        var html = CData().Replace(storage, match => WebUtility.HtmlEncode(match.Groups[1].Value));
        html = MacroParameter().Replace(html, " ");

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match row in PropertyRow().Matches(html))
        {
            var key = Clean(row.Groups[1].Value);
            var value = Clean(row.Groups[2].Value);
            if (key.Length > 0 && value.Length > 0)
            {
                properties.TryAdd(key, value);
            }
        }

        var sections = new List<PageSection>();
        var headings = Heading().Matches(html);
        var preface = Clean(headings.Count > 0 ? html[..headings[0].Index] : html);
        if (preface.Length > 0)
        {
            sections.Add(new PageSection(string.Empty, 0, preface));
        }

        for (var index = 0; index < headings.Count; index++)
        {
            var heading = headings[index];
            var bodyStart = heading.Index + heading.Length;
            var bodyEnd = index + 1 < headings.Count ? headings[index + 1].Index : html.Length;
            sections.Add(new PageSection(
                Clean(heading.Groups[2].Value),
                int.Parse(heading.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                Clean(html[bodyStart..bodyEnd])));
        }

        return new NormalizedPage(Clean(html), sections, properties);
    }

    private static string Clean(string fragment)
    {
        var text = BlockEnd().Replace(fragment, "\n");
        text = ListItem().Replace(text, "\n- ");
        text = CellEnd().Replace(text, " ");
        text = AnyTag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        var lines = text.Split('\n').Select(line => Spaces().Replace(line, " ").Trim()).Where(line => line.Length > 0);
        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"<!\[CDATA\[(.*?)\]\]>", RegexOptions.Singleline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex CData();

    [GeneratedRegex(@"<ac:parameter\b[^>]*>.*?</ac:parameter>", RegexOptions.Singleline | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex MacroParameter();

    // Review R2-05: cell content may not cross a row, cell or table boundary, so a header row
    // (<th>Name</th><th>Owner</th>) before the properties table cannot swallow the next row's key.
    [GeneratedRegex(@"<tr\b[^>]*>\s*<th\b[^>]*>((?:(?!</?(?:t[rhd]|table)\b).)*?)</th>\s*<td\b[^>]*>((?:(?!</?(?:t[rhd]|table)\b).)*?)</td>\s*</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex PropertyRow();

    [GeneratedRegex(@"<h([1-6])\b[^>]*>(.*?)</h\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Heading();

    [GeneratedRegex(@"</(?:p|h[1-6]|li|tr|div|pre|blockquote|table)>|<br\s*/?>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"</t[dh]>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex CellEnd();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t\u00A0]+", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Spaces();
}

/// <summary>Recognizes design decisions by label, title, or the decision template's properties, and says which.</summary>
public static partial class DesignDecisionDetector
{
    private const int MaxOutcomeLength = 2000;

    private static readonly HashSet<string> DecisionLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "decision", "decisions", "decision-log", "adr", "design-decision", "architecture-decision",
    };

    public static DesignDecisionRecord? Detect(ConfluencePageSnapshot page)
    {
        ArgumentNullException.ThrowIfNull(page);
        string? detectedBy = null;
        // Checked in order of confidence: an explicit label is a deliberate human tag, a matching
        // title is a naming convention that can false-positive, and the template-properties check
        // only fires when neither of those already claimed the page.
        if (page.Labels.FirstOrDefault(DecisionLabels.Contains) is { } label)
        {
            detectedBy = "label:" + label.ToLowerInvariant();
        }
        else if (DecisionTitle().IsMatch(page.Title))
        {
            detectedBy = "title";
        }
        else if (page.Properties.ContainsKey("Status") && (page.Properties.ContainsKey("Outcome") || page.Properties.ContainsKey("Decision")))
        {
            detectedBy = "decision template";
        }

        if (detectedBy is null)
        {
            return null;
        }

        var outcome = page.Properties.GetValueOrDefault("Outcome")
            ?? page.Properties.GetValueOrDefault("Decision")
            ?? page.Sections.FirstOrDefault(section => OutcomeHeading().IsMatch(section.Heading))?.Text;

        return new DesignDecisionRecord(page.Id, page.Version, page.SpaceKey, page.Title, detectedBy)
        {
            Status = page.Properties.GetValueOrDefault("Status"),
            Outcome = outcome is { Length: > MaxOutcomeLength } ? outcome[..MaxOutcomeLength] : outcome,
            Labels = page.Labels,
            Url = page.Url,
            LastModified = page.LastModified,
            Acl = page.Acl,
        };
    }

    [GeneratedRegex(@"^\s*(ADR[-\s]?\d+|Decision\b|Design decision\b|Architecture decision\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DecisionTitle();

    [GeneratedRegex(@"^(decision|outcome|decision outcome)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex OutcomeHeading();
}
