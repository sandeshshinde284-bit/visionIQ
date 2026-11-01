using System.Globalization;
using System.Text.Json;

namespace VisionIQ.Ingestor.Ado;

/// <summary>Reads work items, their relations and their revision history from Azure DevOps (S04-01).</summary>
public sealed class AdoWorkItemSource
{
    /// <summary>The most work-item IDs one WIQL query returns.</summary>
    public const int WiqlLimit = 20000;

    private const int BatchSize = 200;
    private readonly AdoClient client;

    private readonly Defects.DefectEnvironmentClassifier environments = Defects.DefectEnvironmentClassifier.None;

    /// <param name="environments">Per-project environment rules; omitted means every item is UNKNOWN (not_configured).</param>
    public AdoWorkItemSource(AdoClient client, Defects.DefectEnvironmentClassifier? environments)
        : this(client)
    {
        this.environments = environments ?? Defects.DefectEnvironmentClassifier.None;
    }

    public AdoWorkItemSource(AdoClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
    }

    /// <summary>IDs of work items changed since the given point, oldest first.</summary>
    public async Task<IReadOnlyList<int>> QueryIdsAsync(string project, DateTimeOffset? changedSince, CancellationToken cancellationToken = default)
    {
        var uri = client.BuildUri(
            project,
            "_apis/wit/wiql",
            [new("timePrecision", "true"), new("$top", WiqlLimit.ToString(CultureInfo.InvariantCulture))]);
        using var response = await client
            .PostJsonAsync(uri, new { query = AdoWorkItemReader.BuildWiql(project, changedSince) }, cancellationToken)
            .ConfigureAwait(false);

        return JsonRead.Items(response.Root, "workItems")
            .Select(item => JsonRead.Int(item, "id"))
            .OfType<int>()
            .ToList();
    }

    /// <summary>Full work items with relations, in batches of 200. Deleted or inaccessible items come back as NOT SYNCED.</summary>
    public async Task<IReadOnlyList<ReadOutcome>> GetWorkItemsAsync(string project, IReadOnlyList<int> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var outcomes = new List<ReadOutcome>();
        var uri = client.BuildUri(project, "_apis/wit/workitemsbatch");

        foreach (var batch in ids.Chunk(BatchSize))
        {
            var body = new Dictionary<string, object>
            {
                ["ids"] = batch,
                ["$expand"] = "Relations",
                ["errorPolicy"] = "Omit",
            };
            using var response = await client.PostJsonAsync(uri, body, cancellationToken).ConfigureAwait(false);
            // Azure DevOps errorPolicy=Omit may silently remove an inaccessible ID.
            // Reconstruct results by requested identity, never by response index.
            var received = new Dictionary<int, WorkItemRecord>();
            foreach (var element in JsonRead.Items(response.Root, "value"))
            {
                if (AdoWorkItemReader.ParseWorkItem(element).Record is { } parsed)
                {
                    // Environment is classified from the raw fields (custom fields included) per project rules.
                    var fields = JsonRead.Child(element, "fields") ?? default;
                    received[parsed.Id] = parsed.WithEnvironment(environments.Classify(ToDefectEvidence(project, parsed, fields)));
                }
            }
            foreach (var id in batch)
            {
                outcomes.Add(received.TryGetValue(id, out var record)
                    ? new ReadOutcome(record, null)
                    : new ReadOutcome(null, $"Work item {id} is missing (deleted or not accessible)."));
            }
        }

        return outcomes;
    }

    /// <summary>Maps an ADO work item into the provider-neutral defect evidence used for classification.</summary>
    public static Defects.DefectEvidence ToDefectEvidence(string project, WorkItemRecord item, JsonElement fields)
    {
        ArgumentNullException.ThrowIfNull(item);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (fields.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in fields.EnumerateObject())
            {
                var value = field.Value.ValueKind switch
                {
                    JsonValueKind.String => field.Value.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => field.Value.GetRawText(),
                    _ => null, // identities and other objects are not environment values
                };
                if (value is not null)
                {
                    values[field.Name] = value;
                }
            }
        }

        return new Defects.DefectEvidence(
            "azure_devops", project, item.Title, item.Description, values, item.Tags,
            (item.AreaPath ?? string.Empty).Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>Every revision of one work item, oldest first.</summary>
    public async Task<IReadOnlyList<WorkItemRevisionRecord>> GetRevisionsAsync(string project, int id, CancellationToken cancellationToken = default)
    {
        var revisions = new List<WorkItemRevisionRecord>();
        var path = "_apis/wit/workItems/" + id.ToString(CultureInfo.InvariantCulture) + "/revisions";

        for (var skip = 0; ; skip += BatchSize)
        {
            var uri = client.BuildUri(
                project,
                path,
                [new("$top", BatchSize.ToString(CultureInfo.InvariantCulture)), new("$skip", skip.ToString(CultureInfo.InvariantCulture))]);
            using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            var page = JsonRead.Items(response.Root, "value").ToList();
            revisions.AddRange(page.Select(AdoWorkItemReader.ParseRevision).OfType<WorkItemRevisionRecord>());
            if (page.Count < BatchSize)
            {
                return revisions;
            }
        }
    }
}
