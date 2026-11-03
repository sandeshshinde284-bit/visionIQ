using System.Globalization;
using VisionIQ.Ingestor.Ado;

namespace VisionIQ.Ingestor.Inventory;

public sealed record ProjectInventory(string Name, string Access)
{
    public int? WorkItems { get; init; }

    /// <summary>True when the count hit the WIQL maximum, so the real number is higher.</summary>
    public bool WorkItemsCapped { get; init; }

    public IReadOnlyList<string> Repositories { get; init; } = [];

    public int? PipelineDefinitions { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed class InventoryReport
{
    public required string Tenant { get; init; }

    public DateTimeOffset GeneratedAt { get; init; }

    public IReadOnlyList<ProjectInventory> Projects { get; init; } = [];

    public bool AllReachable => Projects.Count > 0 && Projects.All(project => project.Access == "ok");
}

/// <summary>Lists what history exists and whether the token can read it (S00-03). Read-only.</summary>
public sealed class InventoryRunner
{
    private readonly AdoClient client;

    public InventoryRunner(AdoClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
    }

    public async Task<InventoryReport> RunAsync(CancellationToken cancellationToken = default)
    {
        HashSet<string>? known = null;
        string? organizationNote = null;
        try
        {
            using var response = await client.GetAsync(client.BuildUri(null, "_apis/projects", [new("$top", "500")]), cancellationToken).ConfigureAwait(false);
            known = JsonRead.Items(response.Root, "value")
                .Select(project => JsonRead.String(project, "name"))
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (AdoSourceException error)
        {
            organizationNote = "Organization project list unavailable: " + error.Message;
        }

        var results = new List<ProjectInventory>();
        foreach (var project in client.Options.Projects)
        {
            if (known is not null && !known.Contains(project))
            {
                results.Add(new ProjectInventory(project, "not found") { Notes = ["The project is not in the organization's project list."] });
                continue;
            }

            results.Add(await InspectAsync(project, organizationNote, cancellationToken).ConfigureAwait(false));
        }

        return new InventoryReport { Tenant = client.Options.Tenant, GeneratedAt = DateTimeOffset.UtcNow, Projects = results };
    }

    private async Task<ProjectInventory> InspectAsync(string project, string? organizationNote, CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        if (organizationNote is not null)
        {
            notes.Add(organizationNote);
        }

        var failures = new List<AdoSourceException>();
        int? workItems = null;
        var capped = false;
        try
        {
            var ids = await new AdoWorkItemSource(client).QueryIdsAsync(project, null, cancellationToken).ConfigureAwait(false);
            workItems = ids.Count;
            capped = ids.Count >= AdoWorkItemSource.WiqlLimit;
        }
        catch (AdoSourceException error)
        {
            failures.Add(error);
            notes.Add("Work items: " + error.Message);
        }

        IReadOnlyList<string> repositories = [];
        try
        {
            var found = await new AdoRepositorySource(client).GetRepositoriesAsync(project, cancellationToken).ConfigureAwait(false);
            repositories = found.Select(repository => repository.Name).ToList();
        }
        catch (AdoSourceException error)
        {
            failures.Add(error);
            notes.Add("Repositories: " + error.Message);
        }

        int? definitions = null;
        try
        {
            using var response = await client.GetAsync(client.BuildUri(project, "_apis/build/definitions", [new("$top", "1000")]), cancellationToken).ConfigureAwait(false);
            definitions = JsonRead.Int(response.Root, "count") ?? JsonRead.Items(response.Root, "value").Count();
        }
        catch (AdoSourceException error)
        {
            failures.Add(error);
            notes.Add("Pipelines: " + error.Message);
        }

        var access = failures.Count switch
        {
            0 => "ok",
            3 when failures.TrueForAll(failure => failure.IsAccessDenied) => "access denied",
            3 => "unreachable",
            _ => "partial",
        };

        if (capped)
        {
            notes.Add("Work item count is at least " + AdoWorkItemSource.WiqlLimit.ToString("N0", CultureInfo.InvariantCulture) + " (WIQL maximum).");
        }

        return new ProjectInventory(project, access)
        {
            WorkItems = workItems,
            WorkItemsCapped = capped,
            Repositories = repositories,
            PipelineDefinitions = definitions,
            Notes = notes,
        };
    }
}
