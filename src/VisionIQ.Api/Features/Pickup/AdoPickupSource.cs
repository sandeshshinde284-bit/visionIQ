using System.Globalization;
using System.Text.Json;
using VisionIQ.Ingestor.Ado;

namespace VisionIQ.Api.Features.Pickup;

/// <summary>Finds pickups that happened in Azure DevOps, whether or not a webhook reported them (S15-14).</summary>
public interface IPickupSource
{
    /// <summary>
    /// For each work item of the project that is now in a pickup state and changed since <paramref name="since"/>,
    /// the change that made it actionable, when that change itself happened at or after <paramref name="since"/>.
    /// When <paramref name="onlyWorkItems"/> is given, no other work item is asked about.
    /// </summary>
    Task<IReadOnlyList<TicketChange>> FindPickupsAsync(
        ProjectPickupPolicy project, DateTimeOffset since, IReadOnlyCollection<int>? onlyWorkItems, CancellationToken cancellationToken);
}

/// <summary>Resolves the commit a run analyses: the head of the project's configured branch.</summary>
public interface IBaseCommitResolver
{
    Task<string> ResolveAsync(ProjectPickupPolicy project, CancellationToken cancellationToken);
}

/// <summary>One revision of a work item, reduced to what the pickup rules read.</summary>
public sealed record WorkItemSnapshot(
    int Revision,
    DateTimeOffset? ChangedAt,
    string? WorkItemType,
    string? State,
    bool Assigned,
    IReadOnlyList<string> Tags,
    string? ChangedById,
    string? ChangedByName);

/// <summary>Works out, from a revision history, the change that last moved a work item into a pickup state.</summary>
public static class PickupTransitions
{
    /// <summary>
    /// The latest revision that made the item actionable (see <see cref="ProjectPickupPolicy.IsActionable"/>), as a
    /// <see cref="TicketChange"/>; null if none. This is the same revision the webhook reports for that change.
    /// </summary>
    public static TicketChange? Latest(
        string? organizationUrl, ProjectPickupPolicy project, int workItemId, IReadOnlyList<WorkItemSnapshot> revisions)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(revisions);
        var ordered = revisions.OrderBy(r => r.Revision).ToList();
        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            var current = ordered[i];
            var before = i > 0 ? ordered[i - 1] : null;
            if (project.IsActionable(current.State, current.Assigned) &&
                !project.IsActionable(before?.State, before?.Assigned ?? false))
            {
                return new TicketChange(
                    organizationUrl,
                    project.Project,
                    workItemId,
                    current.Revision,
                    current.WorkItemType ?? string.Empty,
                    before?.State,
                    current.State,
                    current.Assigned,
                    current.Tags,
                    current.ChangedById,
                    current.ChangedByName,
                    before?.Assigned ?? false);
            }
        }

        return null;
    }
}

/// <summary>ADO REST implementation of <see cref="IPickupSource"/> and <see cref="IBaseCommitResolver"/>.</summary>
public sealed class AdoPickupSource(AdoClient client) : IPickupSource, IBaseCommitResolver
{
    /// <summary>The most work items one sweep looks at per project.</summary>
    public const int SweepLimit = 500;

    public async Task<IReadOnlyList<TicketChange>> FindPickupsAsync(
        ProjectPickupPolicy project, DateTimeOffset since, IReadOnlyCollection<int>? onlyWorkItems, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (onlyWorkItems is { Count: 0 })
        {
            return [];
        }

        var uri = client.BuildUri(
            project.Project,
            "_apis/wit/wiql",
            [new("timePrecision", "true"), new("$top", SweepLimit.ToString(CultureInfo.InvariantCulture))]);
        List<int> ids;
        using (var response = await client.PostJsonAsync(uri, new { query = BuildWiql(project, since, onlyWorkItems) }, cancellationToken))
        {
            ids = Items(response.Root, "workItems").Select(item => Int(item, "id")).OfType<int>().ToList();
        }

        var organization = client.Options.OrganizationUrl.AbsoluteUri;
        var pickups = new List<TicketChange>();
        foreach (var id in ids)
        {
            var revisions = await RevisionsAsync(project.Project, id, cancellationToken);
            var latest = PickupTransitions.Latest(organization, project, id, revisions);
            var enteredAt = revisions.FirstOrDefault(r => r.Revision == latest?.Revision)?.ChangedAt;
            if (latest is not null && enteredAt >= since)
            {
                pickups.Add(latest);
            }
        }

        return pickups;
    }

    public async Task<string> ResolveAsync(ProjectPickupPolicy project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var branch = "refs/heads/" + project.Branch;
        var uri = client.BuildUri(
            project.Project,
            "_apis/git/repositories/" + Uri.EscapeDataString(project.RepositoryId) + "/refs",
            [new("filter", "heads/" + project.Branch)]);
        using var response = await client.GetAsync(uri, cancellationToken);
        // The filter is a prefix match ("heads/main" also matches "heads/main-old"), so take the exact name.
        var head = Items(response.Root, "value")
            .FirstOrDefault(r => string.Equals(Text(r, "name"), branch, StringComparison.Ordinal));
        return Text(head, "objectId") is { Length: 40 } sha
            ? sha.ToLowerInvariant()
            : throw new InvalidOperationException("The configured branch was not found in the repository.");
    }

    /// <summary>
    /// WIQL for items of the project's eligible types that are in a pickup state and changed since the given time.
    /// With <paramref name="onlyWorkItems"/> the query names those ids, so ADO returns (and the sweep then reads
    /// the history of) nothing else.
    /// </summary>
    public static string BuildWiql(ProjectPickupPolicy project, DateTimeOffset since, IReadOnlyCollection<int>? onlyWorkItems = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var only = onlyWorkItems is { Count: > 0 }
            ? " AND [System.Id] IN (" + string.Join(", ", onlyWorkItems.Order().Select(id => id.ToString(CultureInfo.InvariantCulture))) + ")"
            : string.Empty;
        return "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = " + Quote(project.Project) + only +
            " AND [System.WorkItemType] IN (" + string.Join(", ", project.DefectTypes.Concat(project.EnhancementTypes).Select(Quote)) + ")" +
            " AND [System.State] IN (" + string.Join(", ", project.PickupStates.Select(Quote)) + ")" +
            " AND [System.ChangedDate] >= " + Quote(since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)) +
            // Newest first: if more than SweepLimit items match, the cut drops the oldest, not recent pickups.
            " ORDER BY [System.ChangedDate] DESC, [System.Id] ASC";
    }

    /// <summary>Reads one revision as returned by the work-item revisions API.</summary>
    public static WorkItemSnapshot? ParseRevision(JsonElement revision)
    {
        if (Int(revision, "rev") is not { } rev ||
            !revision.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var changedBy = fields.TryGetProperty("System.ChangedBy", out var actor) ? actor : default;
        return new WorkItemSnapshot(
            rev,
            Text(fields, "System.ChangedDate") is { } changed &&
                DateTimeOffset.TryParse(changed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at
                : null,
            Text(fields, "System.WorkItemType"),
            Text(fields, "System.State"),
            fields.TryGetProperty("System.AssignedTo", out var assignee) &&
                (assignee.ValueKind == JsonValueKind.Object ||
                 (assignee.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(assignee.GetString()))),
            AdoServiceHook.Tags(Text(fields, "System.Tags")),
            changedBy.ValueKind == JsonValueKind.Object ? Text(changedBy, "id") : null,
            changedBy.ValueKind == JsonValueKind.Object ? Text(changedBy, "uniqueName") : null);
    }

    private async Task<IReadOnlyList<WorkItemSnapshot>> RevisionsAsync(string project, int id, CancellationToken cancellationToken)
    {
        const int page = 200;
        var revisions = new List<WorkItemSnapshot>();
        for (var skip = 0; ; skip += page)
        {
            var uri = client.BuildUri(
                project,
                "_apis/wit/workItems/" + id.ToString(CultureInfo.InvariantCulture) + "/revisions",
                [new("$top", page.ToString(CultureInfo.InvariantCulture)), new("$skip", skip.ToString(CultureInfo.InvariantCulture))]);
            using var response = await client.GetAsync(uri, cancellationToken);
            var items = Items(response.Root, "value");
            revisions.AddRange(items.Select(ParseRevision).OfType<WorkItemSnapshot>());
            if (items.Count < page)
            {
                return revisions;
            }
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static List<JsonElement> Items(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray()]
            : [];

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
}
