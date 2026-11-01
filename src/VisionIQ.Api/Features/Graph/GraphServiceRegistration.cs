using VisionIQ.Api.Configuration;
using VisionIQ.Api.Features.Evidence;

namespace VisionIQ.Api.Features.Graph;

/// <summary>
/// B-16: composition root for the LRN-07 evidence reader and authorized graph traversal.
/// Requires <see cref="DatabaseOptions"/> to be registered. No HTTP endpoint is mapped: the API has
/// no authenticated caller identity yet, and graph results must never be served without one.
/// </summary>
public static class GraphServiceRegistration
{
    /// <summary>
    /// B-6: when ADO_ORG_URL, ADO_PROJECTS and ADO_READ_PAT are configured, ADO evidence is readable by
    /// live members of its project; every other provider stays denied. Without them nothing changes
    /// (deny-all). Call before <see cref="AddRelationshipGraph"/>.
    /// </summary>
    public static IServiceCollection AddAdoEvidenceAccess(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var projects = (configuration["ADO_PROJECTS"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!Uri.TryCreate(configuration["ADO_ORG_URL"], UriKind.Absolute, out var organization) ||
            organization.Scheme != Uri.UriSchemeHttps ||
            projects.Length == 0 ||
            string.IsNullOrWhiteSpace(configuration["ADO_READ_PAT"]))
        {
            return services;
        }

        var options = new VisionIQ.Ingestor.Ado.AdoOptions
        {
            OrganizationUrl = organization,
            Projects = projects,
            PersonalAccessToken = configuration["ADO_READ_PAT"]!,
        };
        services.AddSingleton<VisionIQ.Ingestor.Ado.IAdoProjectMembership>(_ =>
            new VisionIQ.Ingestor.Ado.AdoTeamProjectMembership(
                new VisionIQ.Ingestor.Ado.AdoClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, options)));
        services.AddSingleton<IProviderAccessVerifier>(provider => new ProviderRoutingAccessVerifier(
            new Dictionary<string, IProviderAccessVerifier>(StringComparer.Ordinal)
            {
                ["azure_devops"] = new AdoProjectAccessVerifier(
                    provider.GetRequiredService<VisionIQ.Ingestor.Ado.IAdoProjectMembership>(),
                    organization.AbsoluteUri.TrimEnd('/'), projects),
                // N-17: code-scan evidence is readable by live members of the project it was scanned for.
                ["git"] = new CodeScanAccessVerifier(
                    provider.GetRequiredService<VisionIQ.Ingestor.Ado.IAdoProjectMembership>(), projects),
            }));
        return services;
    }

    public static IServiceCollection AddRelationshipGraph(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ICanonicalEvidenceReader>(provider =>
            new PostgresCanonicalEvidenceReader(provider.GetRequiredService<DatabaseOptions>()));

        // Fail closed until real provider verifiers exist (ADO: B-5/B-6). Register a real
        // IProviderAccessVerifier before calling this method to replace the default.
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IProviderAccessVerifier)))
        {
            services.AddSingleton<IProviderAccessVerifier, DenyUnknownSourcePermissionVerifier>();
        }

        services.AddSingleton<EvidenceQueryService>();
        services.AddSingleton<IRelationshipGraphStore>(provider =>
            new PostgresRelationshipGraphStore(provider.GetRequiredService<DatabaseOptions>()));
        services.AddSingleton<IGraphRevisionAccess>(provider =>
            new PostgresGraphRevisionAccess(provider.GetRequiredService<DatabaseOptions>()));
        services.AddSingleton<AuthorizedGraphTraversal>();
        return services;
    }
}
