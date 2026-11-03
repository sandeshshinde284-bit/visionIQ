using System.Globalization;
using System.Text.Json;

namespace VisionIQ.Ingestor.Ado;

public sealed record BuildArtifactRecord(string Name, string? ResourceType);

public sealed record BuildRecord(
    int Id,
    string Project,
    string BuildNumber,
    int DefinitionId,
    string DefinitionName,
    string Status,
    DateTimeOffset? FinishTime)
{
    public string? Result { get; init; }

    public string? Reason { get; init; }

    public string? SourceBranch { get; init; }

    /// <summary>The commit the build ran on.</summary>
    public string? SourceVersion { get; init; }

    public string? RepositoryId { get; init; }

    public DateTimeOffset? QueueTime { get; init; }

    public DateTimeOffset? StartTime { get; init; }

    /// <summary>The pull request that triggered the build, when it was a PR build.</summary>
    public int? PullRequestNumber { get; init; }

    public IReadOnlyList<BuildArtifactRecord> Artifacts { get; init; } = [];
}

/// <summary>Reads completed pipeline runs and their artifact names (S04-12). Artifacts are listed, never downloaded.</summary>
public sealed class AdoBuildSource
{
    private readonly AdoClient client;

    public AdoBuildSource(AdoClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
    }

    /// <summary>Completed builds finished at or after <paramref name="minTime"/>, oldest first.</summary>
    public async Task<IReadOnlyList<BuildRecord>> GetBuildsAsync(string project, DateTimeOffset? minTime, IReadOnlyList<int> definitionIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitionIds);
        var results = new List<BuildRecord>();
        string? token = null;
        string? previous;
        do
        {
            var query = new List<KeyValuePair<string, string>>
            {
                new("queryOrder", "finishTimeAscending"),
                new("statusFilter", "completed"),
                new("$top", "100"),
            };
            if (minTime is { } min)
            {
                query.Add(new("minTime", min.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
            }

            if (definitionIds.Count > 0)
            {
                query.Add(new("definitions", string.Join(',', definitionIds.Select(id => id.ToString(CultureInfo.InvariantCulture)))));
            }

            if (token is not null)
            {
                query.Add(new("continuationToken", token));
            }

            using var response = await client.GetAsync(client.BuildUri(project, "_apis/build/builds", query), cancellationToken).ConfigureAwait(false);
            results.AddRange(JsonRead.Items(response.Root, "value").Select(item => ParseBuild(item, project)).OfType<BuildRecord>());
            previous = token;
            token = response.ContinuationToken;
        }
        while (token is not null && token != previous);

        return results;
    }

    public async Task<BuildRecord> EnrichAsync(string project, BuildRecord build, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(build);
        var path = "_apis/build/builds/" + build.Id.ToString(CultureInfo.InvariantCulture) + "/artifacts";
        using var response = await client.GetAsync(client.BuildUri(project, path), cancellationToken).ConfigureAwait(false);
        var artifacts = JsonRead.Items(response.Root, "value")
            .Select(item => (Name: JsonRead.String(item, "name"), Resource: JsonRead.Child(item, "resource")))
            .Where(item => item.Name is not null)
            .Select(item => new BuildArtifactRecord(item.Name!, item.Resource is { } resource ? JsonRead.String(resource, "type") : null))
            .ToList();
        return build with { Artifacts = artifacts };
    }

    public static BuildRecord? ParseBuild(JsonElement element, string project)
    {
        if (JsonRead.Int(element, "id") is not { } id
            || JsonRead.String(element, "buildNumber") is not { } number
            || JsonRead.String(element, "status") is not { } status
            || JsonRead.Child(element, "definition") is not { } definition
            || JsonRead.Int(definition, "id") is not { } definitionId
            || JsonRead.String(definition, "name") is not { } definitionName)
        {
            return null;
        }

        return new BuildRecord(id, project, number, definitionId, definitionName, status, JsonRead.Date(element, "finishTime"))
        {
            Result = JsonRead.String(element, "result"),
            Reason = JsonRead.String(element, "reason"),
            SourceBranch = JsonRead.String(element, "sourceBranch"),
            SourceVersion = JsonRead.String(element, "sourceVersion"),
            RepositoryId = JsonRead.Child(element, "repository") is { } repository ? JsonRead.String(repository, "id") : null,
            QueueTime = JsonRead.Date(element, "queueTime"),
            StartTime = JsonRead.Date(element, "startTime"),
            PullRequestNumber = JsonRead.Child(element, "triggerInfo") is { } trigger ? JsonRead.Int(trigger, "pr.number") : null,
        };
    }
}
