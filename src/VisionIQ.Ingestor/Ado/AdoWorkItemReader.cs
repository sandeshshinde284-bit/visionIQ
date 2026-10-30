using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VisionIQ.Ingestor.Ado;

/// <summary>A relation on a work item: another work item, a pull request, a commit, a build, or a hyperlink.</summary>
public sealed record WorkItemRelation(string Rel, string Url, string? Name);

/// <summary>One work-item revision as read from Azure DevOps.</summary>
public sealed record WorkItemRecord(
    int Id,
    int Revision,
    string WorkItemType,
    string State,
    string Title,
    IReadOnlyList<string> Tags,
    DateTimeOffset ChangedDate)
{
    public string Project { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string? AreaPath { get; init; }

    public DateTimeOffset? CreatedDate { get; init; }

    public IReadOnlyList<WorkItemRelation> Relations { get; init; } = [];
}

/// <summary>One historical revision of a work item.</summary>
public sealed record WorkItemRevisionRecord(int Id, int Revision, DateTimeOffset ChangedDate)
{
    public string? State { get; init; }

    public string? Title { get; init; }

    public string? Reason { get; init; }
}

/// <summary>Result of reading one source item: either a record or the reason it was not synced.</summary>
public sealed record ReadOutcome(WorkItemRecord? Record, string? NotSyncedReason)
{
    public bool IsSynced => Record is not null;
}

/// <summary>
/// Builds the work-item query and parses work-item responses. It makes no network calls,
/// so it is tested on recorded responses.
/// </summary>
public static class AdoWorkItemReader
{
    private static readonly Regex ProjectNamePattern = new(@"^[A-Za-z0-9 ._-]{1,64}$", RegexOptions.CultureInvariant);

    /// <summary>WIQL for one project, changed since the given point (inclusive), oldest first, ties by ID.</summary>
    public static string BuildWiql(string projectName, DateTimeOffset? changedSince)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        if (!ProjectNamePattern.IsMatch(projectName))
        {
            throw new ArgumentException("Project name contains characters that are not allowed.", nameof(projectName));
        }

        var query = "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = '" + projectName + "'";
        if (changedSince is { } since)
        {
            query += " AND [System.ChangedDate] >= '"
                + since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) + "'";
        }

        return query + " ORDER BY [System.ChangedDate] ASC, [System.Id] ASC";
    }

    /// <summary>Parses one work-item response body.</summary>
    public static ReadOutcome ParseWorkItem(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ReadOutcome(null, "Empty response body.");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return ParseWorkItem(document.RootElement);
        }
        catch (JsonException)
        {
            return new ReadOutcome(null, "Response is not valid JSON.");
        }
    }

    /// <summary>
    /// Parses one work item. A missing or malformed field produces NOT SYNCED with a reason,
    /// never an empty or guessed record.
    /// </summary>
    public static ReadOutcome ParseWorkItem(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ReadOutcome(null, "Work item is missing (deleted or not accessible).");
        }

        if (JsonRead.Int(root, "id") is not { } id)
        {
            return new ReadOutcome(null, "Missing or invalid work item id.");
        }

        if (JsonRead.Int(root, "rev") is not { } revision)
        {
            return new ReadOutcome(null, "Missing or invalid revision.");
        }

        if (JsonRead.Child(root, "fields") is not { } fields)
        {
            return new ReadOutcome(null, "Missing fields object.");
        }

        var type = JsonRead.String(fields, "System.WorkItemType");
        var state = JsonRead.String(fields, "System.State");
        var title = JsonRead.String(fields, "System.Title");
        var changedText = JsonRead.String(fields, "System.ChangedDate");
        if (type is null || state is null || title is null || changedText is null)
        {
            return new ReadOutcome(null, "Missing required field (type, state, title or changed date).");
        }

        if (JsonRead.Date(fields, "System.ChangedDate") is not { } changedDate)
        {
            return new ReadOutcome(null, "Changed date is not a valid timestamp.");
        }

        var tags = (JsonRead.String(fields, "System.Tags") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var relations = JsonRead.Items(root, "relations")
            .Select(relation => (Rel: JsonRead.String(relation, "rel"), Url: JsonRead.String(relation, "url"), Attributes: JsonRead.Child(relation, "attributes")))
            .Where(relation => relation.Rel is not null && relation.Url is not null)
            .Select(relation => new WorkItemRelation(
                relation.Rel!,
                relation.Url!,
                relation.Attributes is { } attributes ? JsonRead.String(attributes, "name") : null))
            .ToList();

        return new ReadOutcome(
            new WorkItemRecord(id, revision, type, state, title, tags, changedDate)
            {
                Description = JsonRead.String(fields, "System.Description"),
                AreaPath = JsonRead.String(fields, "System.AreaPath"),
                CreatedDate = JsonRead.Date(fields, "System.CreatedDate"),
                Relations = relations,
            },
            null);
    }

    /// <summary>Parses one entry of the revisions API. Returns null when it lacks an id, revision or date.</summary>
    public static WorkItemRevisionRecord? ParseRevision(JsonElement root)
    {
        if (JsonRead.Int(root, "id") is not { } id
            || JsonRead.Int(root, "rev") is not { } revision
            || JsonRead.Child(root, "fields") is not { } fields
            || JsonRead.Date(fields, "System.ChangedDate") is not { } changed)
        {
            return null;
        }

        return new WorkItemRevisionRecord(id, revision, changed)
        {
            State = JsonRead.String(fields, "System.State"),
            Title = JsonRead.String(fields, "System.Title"),
            Reason = JsonRead.String(fields, "System.Reason"),
        };
    }
}
