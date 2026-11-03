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
            foreach (var id in received.Keys.ToList())
            {
                received[id] = await WithCommentsAsync(project, received[id], cancellationToken).ConfigureAwait(false);
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

    private const int CommentPageSize = 200;
    private const int MaxCommentPages = 10;

    /// <summary>
    /// Discussion comments (master design 6.6 stage 2). Skipped when ADO reports none; a failure records the
    /// comments as unavailable with a reason instead of failing the work item.
    /// </summary>
    private async Task<WorkItemRecord> WithCommentsAsync(string project, WorkItemRecord record, CancellationToken cancellationToken)
    {
        switch (record.CommentCount)
        {
            case 0:
                return record with { Comments = [] }; // known to have none: no call
            case null:
                // ADO always reports System.CommentCount; without it nothing is assumed (not "none").
                return record with { Comments = null, CommentsGap = "comments: count not reported" };
        }

        var comments = new List<WorkItemComment>();
        string? continuation = null;
        try
        {
            for (var page = 0; page < MaxCommentPages; page++)
            {
                var query = new List<KeyValuePair<string, string>> { new("$top", CommentPageSize.ToString(CultureInfo.InvariantCulture)) };
                if (continuation is not null)
                {
                    query.Add(new("continuationToken", continuation));
                }

                var uri = client.BuildUri(project, "_apis/wit/workItems/" + record.Id.ToString(CultureInfo.InvariantCulture) + "/comments",
                    query, apiVersion: "7.1-preview.4");
                using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
                foreach (var comment in JsonRead.Items(response.Root, "comments"))
                {
                    if (JsonRead.Bool(comment, "isDeleted") || JsonRead.Int(comment, "id") is not { } commentId ||
                        JsonRead.String(comment, "text") is not { } text)
                    {
                        continue;
                    }

                    comments.Add(new WorkItemComment(commentId,
                        JsonRead.Child(comment, "createdBy") is { } author ? JsonRead.String(author, "id") : null,
                        JsonRead.Date(comment, "createdDate"), JsonRead.Date(comment, "modifiedDate"), text));
                }

                continuation = JsonRead.String(response.Root, "continuationToken");
                if (string.IsNullOrEmpty(continuation))
                {
                    return record with { Comments = comments.OrderBy(c => c.Id).ToList() };
                }
            }

            return record with { Comments = comments.OrderBy(c => c.Id).ToList(), CommentsTruncated = true };
        }
        catch (AdoSourceException error)
        {
            return record with
            {
                Comments = null,
                CommentsGap = "comments: " + (error.StatusCode is { } status
                    ? "HTTP " + ((int)status).ToString(CultureInfo.InvariantCulture)
                    : "unavailable"),
            };
        }
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

    /// <summary>N-2: IDs of work items currently in the project's ADO Recycle Bin (deleted, restorable).</summary>
    public async Task<IReadOnlyList<int>> GetRecycleBinIdsAsync(string project, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        using var response = await client.GetAsync(client.BuildUri(project, "_apis/wit/recyclebin"), cancellationToken).ConfigureAwait(false);
        return JsonRead.Items(response.Root, "value").Select(item => JsonRead.Int(item, "id")).OfType<int>().Distinct().ToList();
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

/// <summary>N-2: a work item VisionIQ must stop serving; Reason is deleted_in_source or missing_in_source.</summary>
public sealed record WorkItemDeletion(int Id, string Project, DateTimeOffset ObservedAt, string Reason);
