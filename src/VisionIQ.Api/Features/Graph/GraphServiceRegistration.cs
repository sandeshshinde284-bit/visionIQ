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
        var confluence = ConfluenceReadOptions(configuration);
        if (confluence is not null)
        {
            services.AddSingleton(new VisionIQ.Ingestor.Confluence.ConfluenceHttp(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, confluence.Value.Options));
            services.AddSingleton<IConfluenceIdentityResolver>(confluence.Value.Identities);
        }

        services.AddSingleton<IProviderAccessVerifier>(provider =>
        {
            var verifiers = new Dictionary<string, IProviderAccessVerifier>(StringComparer.Ordinal)
            {
                ["azure_devops"] = new AdoProjectAccessVerifier(
                    provider.GetRequiredService<VisionIQ.Ingestor.Ado.IAdoProjectMembership>(),
                    organization.AbsoluteUri.TrimEnd('/'), projects),
                // N-17: code-scan evidence is readable by live members of the project it was scanned for.
                ["git"] = new CodeScanAccessVerifier(
                    provider.GetRequiredService<VisionIQ.Ingestor.Ado.IAdoProjectMembership>(), projects),
            };
            if (confluence is not null)
            {
                // LRN-02: Confluence pages are readable only if Confluence's live permission check says so.
                verifiers["confluence"] = new ConfluencePageAccessVerifier(
                    provider.GetRequiredService<VisionIQ.Ingestor.Confluence.ConfluenceHttp>(),
                    provider.GetRequiredService<IConfluenceIdentityResolver>());
            }

            return new ProviderRoutingAccessVerifier(verifiers);
        });
        return services;
    }

    /// <summary>
    /// Confluence read access is enabled only with CONFLUENCE_BASE_URL, CONFLUENCE_SPACE_KEYS, CONFLUENCE_API_TOKEN
    /// (and CONFLUENCE_USER_EMAIL on Cloud) plus an identity map (CONFLUENCE_IDENTITY_MAP_FILE). Otherwise
    /// Confluence evidence stays hidden.
    /// </summary>
    private static (VisionIQ.Ingestor.Confluence.ConfluenceOptions Options, IConfluenceIdentityResolver Identities)? ConfluenceReadOptions(IConfiguration configuration)
    {
        var spaces = (configuration["CONFLUENCE_SPACE_KEYS"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!Uri.TryCreate(configuration["CONFLUENCE_BASE_URL"], UriKind.Absolute, out var site) || site.Scheme != Uri.UriSchemeHttps ||
            spaces.Length == 0 || string.IsNullOrWhiteSpace(configuration["CONFLUENCE_API_TOKEN"]) ||
            configuration["CONFLUENCE_IDENTITY_MAP_FILE"] is not { Length: > 0 } mapFile)
        {
            return null;
        }

        var deployment = string.Equals(configuration["CONFLUENCE_DEPLOYMENT"], "datacenter", StringComparison.OrdinalIgnoreCase)
            ? VisionIQ.Ingestor.Confluence.ConfluenceDeployment.DataCenter
            : VisionIQ.Ingestor.Confluence.ConfluenceOptions.Infer(site);
        var options = new VisionIQ.Ingestor.Confluence.ConfluenceOptions
        {
            BaseUrl = site,
            SpaceKeys = spaces,
            Deployment = deployment,
            UserEmail = configuration["CONFLUENCE_USER_EMAIL"],
            ApiToken = configuration["CONFLUENCE_API_TOKEN"]!,
        };
        return (options, MappedConfluenceIdentityResolver.FromJson(File.ReadAllText(mapFile)));
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
