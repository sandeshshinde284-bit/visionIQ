using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VisionIQ.Api.Features.Backfill;

namespace VisionIQ.Ingestor.Cli;

public enum ServiceHookOutcome
{
    /// <summary>A supported change for an approved project: process it.</summary>
    Accepted,

    /// <summary>Not for VisionIQ (other event type, organization or project): acknowledge, do nothing.</summary>
    Ignored,

    /// <summary>Malformed: reject.</summary>
    Invalid,
}

/// <summary>
/// LRN-05: reads an Azure DevOps Service Hook work-item event only to learn WHICH item changed (event ID, item ID,
/// revision, project). Its content is never trusted: the handler re-fetches the item from ADO.
/// </summary>
public static class AdoServiceHook
{
    private static readonly string[] Supported = ["workitem.created", "workitem.updated", "workitem.restored", "workitem.deleted"];

    public static ServiceHookOutcome Parse(
        JsonElement root, Uri organization, IReadOnlyCollection<string> approvedProjects, string tenant,
        out SourceChangeNotification? change)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(approvedProjects);
        change = null;
        if (root.ValueKind != JsonValueKind.Object ||
            Text(root, "id") is not { Length: > 0 and <= 256 } eventId ||
            Text(root, "eventType") is not { } eventType ||
            !root.TryGetProperty("resource", out var resource) || resource.ValueKind != JsonValueKind.Object)
        {
            return ServiceHookOutcome.Invalid;
        }

        if (!Supported.Contains(eventType, StringComparer.Ordinal))
        {
            return ServiceHookOutcome.Ignored;
        }

        // "updated" carries the item in resource.workItemId and its new state in resource.revision;
        // "created"/"restored" carry the item itself in resource.
        var updated = eventType == "workitem.updated";
        var item = updated && resource.TryGetProperty("revision", out var revision) && revision.ValueKind == JsonValueKind.Object ? revision : resource;
        var id = updated ? Number(resource, "workItemId") : Number(resource, "id");
        var rev = Number(resource, "rev") ?? Number(item, "rev");
        var url = Text(resource, "url") ?? Text(item, "url");
        var project = item.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object
            ? Text(fields, "System.TeamProject")
            : null;
        if (id is not > 0 || rev is not > 0 || url is null || project is null)
        {
            return ServiceHookOutcome.Invalid;
        }

        var prefix = organization.AbsoluteUri.TrimEnd('/') + "/";
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !approvedProjects.Contains(project, StringComparer.Ordinal))
        {
            return ServiceHookOutcome.Ignored;
        }

        // N-2: a deletion is a different kind of change; the handler confirms it with ADO before hiding anything.
        var kind = eventType == "workitem.deleted" ? "work_item_deleted" : "work_item";
        change = new SourceChangeNotification(tenant + "/ado/" + project, eventId, kind,
            id.Value.ToString(CultureInfo.InvariantCulture), rev.Value.ToString(CultureInfo.InvariantCulture));
        return ServiceHookOutcome.Accepted;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}

/// <summary>
/// HTTP Basic credentials configured on the ADO Service Hook subscription (ADO_WEBHOOK_USERNAME /
/// ADO_WEBHOOK_SECRET). Compared in constant time; the secret must be at least 32 characters.
/// </summary>
public sealed class WebhookCredentials
{
    private readonly byte[] expected;

    public WebhookCredentials(string username, string secret)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ADO_WEBHOOK_USERNAME is required and must not contain ':'.");
        }

        if (string.IsNullOrEmpty(secret) || secret.Length < 32)
        {
            throw new InvalidOperationException("ADO_WEBHOOK_SECRET must be at least 32 characters.");
        }

        expected = SHA256.HashData(Encoding.UTF8.GetBytes(username + ":" + secret));
    }

    public bool IsValid(string? authorization)
    {
        if (authorization is null || !authorization.StartsWith("Basic ", StringComparison.Ordinal))
        {
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(authorization["Basic ".Length..].Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        // Hash first so the comparison is constant-time regardless of the presented length.
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(decoded), expected);
    }
}
