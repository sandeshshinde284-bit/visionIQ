using System.Text.Json;

namespace VisionIQ.Api.Features.Pickup;

/// <summary>A verified ADO service-hook delivery: its event id (stable across ADO's retries) and the change it reports.</summary>
public sealed record ServiceHookDelivery(string EventId, string EventType, TicketChange Change);

/// <summary>
/// Reads ADO "Work item created" / "Work item updated" service-hook bodies (resource version 1.0, resource details
/// "All"). Only the fields pickup needs are read; the ticket text in the body is never kept or logged.
/// </summary>
public static class AdoServiceHook
{
    public const string WorkItemCreated = "workitem.created";
    public const string WorkItemUpdated = "workitem.updated";

    /// <summary>Parses a delivery; returns null with a fixed reason code when the body cannot be used.</summary>
    public static ServiceHookDelivery? Parse(ReadOnlySpan<byte> body, out string? problem)
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
                Text(root, "id") is not { Length: > 0 and <= 200 } eventId ||
                Text(root, "eventType") is not { } eventType ||
                !root.TryGetProperty("resource", out var resource) || resource.ValueKind != JsonValueKind.Object)
            {
                problem = "not_a_service_hook";
                return null;
            }

            if (eventType is not (WorkItemCreated or WorkItemUpdated))
            {
                problem = "unsupported_event";
                return null;
            }

            // "updated" carries the change plus the full new revision; "created" carries the new item itself.
            var updated = eventType == WorkItemUpdated;
            var item = updated && resource.TryGetProperty("revision", out var revision) ? revision : resource;
            if (!item.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
            {
                problem = "resource_details_missing";
                return null;
            }

            var workItemId = Int(updated ? resource : item, updated ? "workItemId" : "id") ?? Int(item, "id");
            var rev = Int(item, "rev") ?? Int(resource, "rev");
            if (workItemId is not > 0 || rev is not > 0 ||
                Text(fields, "System.TeamProject") is not { } project ||
                Text(fields, "System.WorkItemType") is not { } workItemType)
            {
                problem = "resource_details_missing";
                return null;
            }

            // Unchanged fields keep their current value; "updated" lists changed ones as {oldValue, newValue}.
            var stateAfter = Text(fields, "System.State");
            var stateBefore = updated ? stateAfter : null;
            var assigned = fields.TryGetProperty("System.AssignedTo", out var assignee) && HasValue(assignee);
            var assignedBefore = updated && assigned;
            if (updated && resource.TryGetProperty("fields", out var changed) && changed.ValueKind == JsonValueKind.Object)
            {
                if (changed.TryGetProperty("System.State", out var stateChange) && stateChange.ValueKind == JsonValueKind.Object)
                {
                    stateBefore = Text(stateChange, "oldValue");
                    stateAfter = Text(stateChange, "newValue") ?? stateAfter;
                }

                if (changed.TryGetProperty("System.AssignedTo", out var assigneeChange) && assigneeChange.ValueKind == JsonValueKind.Object)
                {
                    assignedBefore = assigneeChange.TryGetProperty("oldValue", out var previous) && HasValue(previous);
                }
            }

            // revisedBy may be absent or null; System.ChangedBy (an object or "Name <unique name>") is the fallback.
            var actor = updated && resource.TryGetProperty("revisedBy", out var revisedBy) && revisedBy.ValueKind == JsonValueKind.Object
                ? revisedBy
                : fields.TryGetProperty("System.ChangedBy", out var changedBy) ? changedBy : default;

            problem = null;
            return new ServiceHookDelivery(
                eventId,
                eventType,
                new TicketChange(
                    OrganizationUrl(root),
                    project,
                    workItemId.Value,
                    rev.Value,
                    workItemType,
                    stateBefore,
                    stateAfter,
                    assigned,
                    Tags(Text(fields, "System.Tags")),
                    IdentityId(actor),
                    IdentityName(actor),
                    assignedBefore));
        }
    }

    /// <summary>"tag one; tag two" as ADO stores System.Tags.</summary>
    public static IReadOnlyList<string> Tags(string? tags) =>
        (tags ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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

    // Identities arrive as {id, uniqueName, displayName} or, in older payloads, as "Display Name <unique name>".
    private static string? IdentityId(JsonElement identity) =>
        identity.ValueKind == JsonValueKind.Object ? Text(identity, "id") : null;

    private static string? IdentityName(JsonElement identity) => identity.ValueKind switch
    {
        JsonValueKind.Object => Text(identity, "uniqueName"),
        JsonValueKind.String when identity.GetString() is { } text && text.LastIndexOf('<') is var open and >= 0 && text.EndsWith('>') =>
            text[(open + 1)..^1],
        JsonValueKind.String => identity.GetString(),
        _ => null,
    };

    private static bool HasValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => true,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        _ => false,
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
}
