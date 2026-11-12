using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using VisionIQ.Api.Features.Briefs;

namespace VisionIQ.Api.Features.Feedback;

/// <summary>What the engineer did with an advice item (v1.11 §8.10). It is not a statement about correctness.</summary>
public enum AdviceDisposition
{
    Accepted,
    Modified,
    Rejected,
    NotApplicable,
}

/// <summary>One rating read from a ticket comment, e.g. "ADV-002 rejected: we use a queue here".</summary>
public sealed record AdviceRating(string AdviceId, AdviceDisposition Disposition, string? Note);

/// <summary>A "work item commented" delivery, reduced to what feedback needs. The comment text is not kept.</summary>
public sealed record CommentDelivery(
    string EventId,
    string? OrganizationUrl,
    string Project,
    int WorkItemId,
    string Text,
    string? AuthorId,
    string? AuthorName,
    DateTimeOffset? CommentedAt);

/// <summary>
/// S13-05: the rating mechanism lives inside Azure DevOps. An engineer replies on the ticket with one line per
/// advice item; this parser reads those lines. Anything it cannot read is left alone: an unparsed comment is
/// simply not a rating (non-response stays UNVERIFIED, never "accepted").
/// </summary>
public static partial class AdviceFeedbackParser
{
    public const string WorkItemCommented = "workitem.commented";
    public const int MaxNoteLength = 500;
    public const int MaxRatingsPerComment = 20;

    /// <summary>The ratings in a comment (HTML or plain text); the last line for an advice id wins.</summary>
    public static IReadOnlyList<AdviceRating> Parse(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment) || comment.Contains(BriefCommentRenderer.MarkerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // VisionIQ's own brief quotes the rating syntax as an example; it must never rate itself.
            return [];
        }

        var ratings = new Dictionary<string, AdviceRating>(StringComparer.Ordinal);
        foreach (var line in PlainLines(comment))
        {
            var match = RatingLine().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var id = match.Groups["id"].Value.ToUpperInvariant();
            var note = match.Groups["note"].Value.Trim();
            ratings[id] = new AdviceRating(
                id,
                Disposition(match.Groups["verb"].Value),
                note.Length == 0 ? null : note.Length > MaxNoteLength ? note[..MaxNoteLength] : note);
            if (ratings.Count == MaxRatingsPerComment)
            {
                break;
            }
        }

        return [.. ratings.Values.OrderBy(r => r.AdviceId, StringComparer.Ordinal)];
    }

    /// <summary>Reads a "work item commented" service-hook body; null with a reason code when it cannot be used.</summary>
    public static CommentDelivery? ParseDelivery(ReadOnlySpan<byte> body, out string? problem)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException)
        {
            problem = "body_not_json";
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                Text(root, "id") is not { Length: <= 200 } eventId ||
                Text(root, "eventType") != WorkItemCommented ||
                !root.TryGetProperty("resource", out var resource) || resource.ValueKind != JsonValueKind.Object)
            {
                problem = "not_a_comment_event";
                return null;
            }

            // The commented item arrives as the resource itself or, like an update, under "revision".
            var item = resource.TryGetProperty("revision", out var revision) && revision.ValueKind == JsonValueKind.Object
                ? revision
                : resource;
            var workItemId = Int(resource, "workItemId") ?? Int(item, "id") ?? Int(resource, "id");
            if (!item.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object ||
                Text(fields, "System.TeamProject") is not { } project ||
                workItemId is not > 0 ||
                Text(fields, "System.History") is not { } text)
            {
                problem = "resource_details_missing";
                return null;
            }

            var actor = resource.TryGetProperty("revisedBy", out var revisedBy) && revisedBy.ValueKind == JsonValueKind.Object
                ? revisedBy
                : fields.TryGetProperty("System.ChangedBy", out var changedBy) ? changedBy : default;
            var when = Text(fields, "System.ChangedDate") ?? Text(root, "createdDate");
            problem = null;
            return new CommentDelivery(
                eventId,
                OrganizationUrl(root),
                project,
                workItemId.Value,
                text,
                actor.ValueKind == JsonValueKind.Object ? Text(actor, "id") : null,
                IdentityName(actor),
                DateTimeOffset.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null);
        }
    }

    private static AdviceDisposition Disposition(string verb) => verb.ToUpperInvariant() switch
    {
        "ACCEPTED" or "ACCEPT" => AdviceDisposition.Accepted,
        "MODIFIED" or "MODIFY" => AdviceDisposition.Modified,
        "REJECTED" or "REJECT" => AdviceDisposition.Rejected,
        _ => AdviceDisposition.NotApplicable,
    };

    /// <summary>The comment as plain text lines: block tags and line breaks end a line, other tags are dropped.</summary>
    private static string[] PlainLines(string comment)
    {
        var text = BlockBreak().Replace(comment, "\n");
        text = WebUtility.HtmlDecode(AnyTag().Replace(text, string.Empty));
        return text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? OrganizationUrl(JsonElement root)
    {
        if (!root.TryGetProperty("resourceContainers", out var containers) || containers.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in (string[])["collection", "account"])
        {
            if (containers.TryGetProperty(name, out var container) && Text(container, "baseUrl") is { } url)
            {
                return url;
            }
        }

        return null;
    }

    private static string? IdentityName(JsonElement identity) => identity.ValueKind switch
    {
        JsonValueKind.Object => Text(identity, "uniqueName"),
        JsonValueKind.String when identity.GetString() is { } text && text.LastIndexOf('<') is var open and >= 0 && text.EndsWith('>') =>
            text[(open + 1)..^1],
        JsonValueKind.String => identity.GetString(),
        _ => null,
    };

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    // "ADV-001 accepted", "- ADV-002: rejected - reason", "ADV-003 n/a"; the id must start the line.
    [GeneratedRegex(
        @"^[\s\-\*•>]*(?<id>ADV-\d{3})\b[\s:=\-–—]*(?<verb>accepted|accept|modified|modify|rejected|reject|not[ _\-]?applicable|n/?a)\b[\s:;,.\-–—]*(?<note>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex RatingLine();

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/tr|/h[1-6])\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex BlockBreak();

    [GeneratedRegex(@"<[^>]{0,2000}>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex AnyTag();
}
