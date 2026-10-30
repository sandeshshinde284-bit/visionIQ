using System.Globalization;

namespace VisionIQ.Ingestor.Ado;

/// <summary>Reads work items, their relations and their revision history from Azure DevOps (S04-01).</summary>
public sealed class AdoWorkItemSource
{
    /// <summary>The most work-item IDs one WIQL query returns.</summary>
    public const int WiqlLimit = 20000;

    private const int BatchSize = 200;
    private readonly AdoClient client;

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
            var received = JsonRead.Items(response.Root, "value")
                .Select(AdoWorkItemReader.ParseWorkItem)
                .Where(outcome => outcome.Record is not null)
                .Select(outcome => outcome.Record!)
                .ToDictionary(record => record.Id);
            foreach (var id in batch)
            {
                outcomes.Add(received.TryGetValue(id, out var record)
                    ? new ReadOutcome(record, null)
                    : new ReadOutcome(null, $"Work item {id} is missing (deleted or not accessible)."));
            }
        }

        return outcomes;
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
