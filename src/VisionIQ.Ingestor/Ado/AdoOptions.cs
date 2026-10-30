namespace VisionIQ.Ingestor.Ado;

/// <summary>Azure DevOps connection settings. The token never appears in <see cref="ToString"/>.</summary>
public sealed class AdoOptions
{
    public required Uri OrganizationUrl { get; init; }

    public required IReadOnlyList<string> Projects { get; init; }

    /// <summary>Repository names to read. Empty means every enabled repository in each project.</summary>
    public IReadOnlyList<string> Repositories { get; init; } = [];

    /// <summary>Pipeline definition IDs to read. Empty means every pipeline.</summary>
    public IReadOnlyList<int> PipelineIds { get; init; } = [];

    public required string PersonalAccessToken { get; init; }

    public string ApiVersion { get; init; } = "7.1";

    public int MaxRetries { get; init; } = 4;

    /// <summary>The organization name, used as the tenant in every source identity.</summary>
    public string Tenant
    {
        get
        {
            var host = OrganizationUrl.Host;
            if (host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
            {
                return host[..host.IndexOf('.', StringComparison.Ordinal)];
            }

            var segments = OrganizationUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length > 0 ? segments[0] : host;
        }
    }

    public override string ToString() => $"{OrganizationUrl} (projects: {string.Join(", ", Projects)})";
}
