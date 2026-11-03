using Microsoft.Extensions.DependencyInjection;
using VisionIQ.Api.Configuration;
using VisionIQ.Api.Features.Evidence;
using VisionIQ.Api.Features.Graph;

namespace VisionIQ.Api.Tests;

/// <summary>B-16: LRN-07 services resolve from the API container and default to fail-closed access.</summary>
public sealed class GraphServiceRegistrationTests
{
    private static ServiceProvider Build(Action<IServiceCollection>? before = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new DatabaseOptions("localhost", 5432, "visioniq", "visioniq_api", "test-only", "disable"));
        before?.Invoke(services);
        services.AddRelationshipGraph();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public async Task Graph_traversal_resolves_with_all_dependencies()
    {
        await using var provider = Build();

        Assert.NotNull(provider.GetRequiredService<AuthorizedGraphTraversal>());
        Assert.NotNull(provider.GetRequiredService<EvidenceQueryService>());
    }

    [Fact]
    public async Task Provider_access_defaults_to_deny_all()
    {
        await using var provider = Build();

        Assert.IsType<DenyUnknownSourcePermissionVerifier>(provider.GetRequiredService<IProviderAccessVerifier>());
    }

    private sealed class CustomVerifier : IProviderAccessVerifier
    {
        public Task<bool> CanReadNowAsync(EvidenceIdentity identity, EvidenceRequester requester, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    [Fact]
    public async Task A_real_verifier_registered_first_is_kept()
    {
        await using var provider = Build(services => services.AddSingleton<IProviderAccessVerifier, CustomVerifier>());

        Assert.IsType<CustomVerifier>(provider.GetRequiredService<IProviderAccessVerifier>());
    }
}
