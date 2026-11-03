using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VisionIQ.Api.Configuration;
using VisionIQ.Api.Features.Evidence;
using VisionIQ.Api.Features.Graph;
using VisionIQ.Ingestor.Ado;

namespace VisionIQ.Api.Tests;

/// <summary>B-5/B-6: ADO evidence is readable by live members of its project, and by nobody else.</summary>
public sealed class AdoEvidenceAccessTests
{
    private const string Org = "https://dev.azure.com/contoso";
    private static readonly string Key = new('a', 64);

    private sealed class Membership(params string[] members) : IAdoProjectMembership
    {
        public bool Fail { get; init; }

        public Task<bool> IsMemberAsync(string project, string principalId, CancellationToken cancellationToken = default) =>
            Fail ? throw new AdoSourceException(null, "outage") : Task.FromResult(project == "Alpha" && members.Contains(principalId));
    }

    private sealed class Reader(EvidenceReadCandidate candidate) : ICanonicalEvidenceReader
    {
        public Task<EvidenceReadCandidate?> FindLatestAsync(string itemKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<EvidenceReadCandidate?>(candidate);
    }

    private static EvidenceIdentity Ticket(string project = "Alpha", string provider = "azure_devops") =>
        new(provider, Org, project, null, "work_item_current", "123");

    private static EvidenceReadCandidate Stored(string project = "Alpha") =>
        new(Ticket(project), "7", Org + "/" + project + "/_workitems/edit/123", "{\"kind\":\"work_item_current\"}",
            true, [AdoAccessPrincipals.Project(Org, project)]);

    private static EvidenceRequester Person(string id, params string[] projects) =>
        new(id, projects.Select(project => AdoAccessPrincipals.Project(Org, project)).ToHashSet());

    private static AdoProjectAccessVerifier Verifier(IAdoProjectMembership membership) =>
        new(membership, Org + "/", ["Alpha", "Beta"]);

    [Fact]
    public async Task A_live_project_member_can_read_the_project_evidence()
    {
        var query = new EvidenceQueryService(new Reader(Stored()), Verifier(new Membership("ana@contoso.com")));

        var result = await query.FindLatestAuthorizedAsync(Key, Person("ana@contoso.com", "Alpha"));

        Assert.NotNull(result);
        Assert.Equal("123", result!.Identity.ExternalId);
    }

    [Fact]
    public async Task A_non_member_is_denied_even_when_claiming_the_project_scope()
    {
        var query = new EvidenceQueryService(new Reader(Stored()), Verifier(new Membership("ana@contoso.com")));

        Assert.Null(await query.FindLatestAuthorizedAsync(Key, Person("eve@contoso.com", "Alpha")));
    }

    [Fact]
    public async Task A_member_of_another_project_cannot_read_this_one()
    {
        var query = new EvidenceQueryService(new Reader(Stored("Beta")), Verifier(new Membership("ana@contoso.com")));

        Assert.Null(await query.FindLatestAuthorizedAsync(Key, Person("ana@contoso.com", "Beta")));
    }

    [Fact]
    public async Task A_membership_outage_denies_access()
    {
        var verifier = Verifier(new Membership("ana@contoso.com") { Fail = true });

        Assert.False(await verifier.CanReadNowAsync(Ticket(), Person("ana@contoso.com", "Alpha")));
    }

    [Fact]
    public async Task Unapproved_projects_and_other_providers_are_denied()
    {
        var verifier = Verifier(new Membership("ana@contoso.com"));

        Assert.False(await verifier.CanReadNowAsync(Ticket("Gamma"), Person("ana@contoso.com", "Gamma")));
        Assert.False(await verifier.CanReadNowAsync(Ticket(provider: "confluence"), Person("ana@contoso.com")));
    }

    [Fact]
    public async Task The_router_denies_providers_without_a_verifier()
    {
        var router = new ProviderRoutingAccessVerifier(new Dictionary<string, IProviderAccessVerifier>
        {
            ["azure_devops"] = Verifier(new Membership("ana@contoso.com")),
        });

        Assert.True(await router.CanReadNowAsync(Ticket(), Person("ana@contoso.com")));
        Assert.False(await router.CanReadNowAsync(Ticket(provider: "confluence"), Person("ana@contoso.com")));
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new DatabaseOptions("localhost", 5432, "visioniq", "visioniq_api", "test-only", "disable"));
        services.AddAdoEvidenceAccess(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddRelationshipGraph();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    [Fact]
    public async Task Configured_ado_settings_install_the_project_verifier()
    {
        await using var provider = Build(new()
        {
            ["ADO_ORG_URL"] = Org,
            ["ADO_PROJECTS"] = "Alpha, Beta",
            ["ADO_READ_PAT"] = "test-only-not-a-token",
        });

        Assert.IsType<ProviderRoutingAccessVerifier>(provider.GetRequiredService<IProviderAccessVerifier>());
    }

    [Fact]
    public async Task Without_ado_settings_access_stays_deny_all()
    {
        await using var provider = Build(new());

        Assert.IsType<DenyUnknownSourcePermissionVerifier>(provider.GetRequiredService<IProviderAccessVerifier>());
    }
}
