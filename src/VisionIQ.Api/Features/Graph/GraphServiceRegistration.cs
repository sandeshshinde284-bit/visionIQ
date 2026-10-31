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
