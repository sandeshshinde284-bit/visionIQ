using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using VisionIQ.Api.Features.GraphApi;

namespace VisionIQ.Api.Tests;

/// <summary>Graph API configuration and caller authorization: every missing or unexpected value denies.</summary>
public sealed class GraphApiAuthorizationTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string ImpactAgent = "22222222-2222-2222-2222-222222222222";

    private static Dictionary<string, string?> Settings() => new()
    {
        ["VISIONIQ_AUTH_TENANT_ID"] = Tenant.ToString(),
        ["VISIONIQ_AUTH_AUDIENCES"] = "api://visioniq",
        ["VISIONIQ_AUTH_ALLOWED_CALLERS"] = ImpactAgent,
        ["ADO_ORG_URL"] = "https://dev.azure.com/contoso",
        ["ADO_PROJECTS"] = "Payments, Mobile",
    };

    private static GraphApiOptions? Options(Dictionary<string, string?> settings) =>
        GraphApiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

    [Fact]
    public void Without_any_graph_api_settings_nothing_is_exposed() =>
        Assert.Null(Options(new() { ["ADO_ORG_URL"] = "https://dev.azure.com/contoso", ["ADO_PROJECTS"] = "Payments" }));

    [Fact]
    public void A_complete_configuration_is_parsed_with_the_default_scope()
    {
        var options = Options(Settings())!;

        Assert.Equal(("Graph.Read", "https://dev.azure.com/contoso"), (options.RequiredScope, options.AdoInstance));
        Assert.Equal(new[] { "Payments", "Mobile" }, options.ApprovedProjects);
    }

    [Theory]
    [InlineData("VISIONIQ_AUTH_ALLOWED_CALLERS", null, "VISIONIQ_AUTH_ALLOWED_CALLERS is required")]
    [InlineData("VISIONIQ_AUTH_TENANT_ID", "common", "not 'common'")]
    [InlineData("VISIONIQ_AUTH_TENANT_ID", "organizations", "not 'common'")]
    [InlineData("VISIONIQ_AUTH_ALLOWED_CALLERS", "impact-agent", "application (client) IDs")]
    [InlineData("ADO_PROJECTS", null, "ADO project membership")]
    public void Partial_or_unsafe_configuration_fails_at_startup(string key, string? value, string message)
    {
        var settings = Settings();
        settings[key] = value;

        var error = Assert.Throws<InvalidOperationException>(() => Options(settings));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    private static ClaimsPrincipal Token(Action<Dictionary<string, string>>? change = null, bool authenticated = true)
    {
        var claims = new Dictionary<string, string>
        {
            ["tid"] = Tenant.ToString(),
            ["scp"] = "User.Read Graph.Read",
            ["azp"] = ImpactAgent,
            ["oid"] = "33333333-3333-3333-3333-333333333333",
            ["preferred_username"] = "Ana.Lopez@Contoso.com",
        };
        change?.Invoke(claims);
        return new ClaimsPrincipal(new ClaimsIdentity(claims.Select(c => new Claim(c.Key, c.Value)), authenticated ? "Bearer" : null));
    }

    private static readonly GraphCallerAuthorizer Authorizer = new(Options(Settings())!);

    [Fact]
    public void An_approved_app_acting_for_a_user_is_allowed_with_the_users_ado_identity()
    {
        var decision = Authorizer.Authorize(Token());

        Assert.True(decision.Allowed);
        Assert.Equal("ana.lopez@contoso.com", decision.Requester!.PrincipalId);
        Assert.Equal(new[] { "ado-project:https://dev.azure.com/contoso/Mobile", "ado-project:https://dev.azure.com/contoso/Payments" },
            decision.Requester.GroupPrincipalIds.Order());
    }

    [Fact]
    public void Confluence_spaces_become_candidate_scopes_only_when_configured()
    {
        var settings = Settings();
        settings["CONFLUENCE_BASE_URL"] = "https://contoso.atlassian.net";
        settings["CONFLUENCE_SPACE_KEYS"] = "ENG";

        var withConfluence = new GraphCallerAuthorizer(Options(settings)!).Authorize(Token()).Requester!;

        Assert.Contains("confluence-space:https://contoso.atlassian.net/ENG", withConfluence.GroupPrincipalIds);
        Assert.DoesNotContain(Authorizer.Authorize(Token()).Requester!.GroupPrincipalIds, p => p.StartsWith("confluence-", StringComparison.Ordinal));
    }

    [Fact]
    public void A_v1_token_with_appid_instead_of_azp_is_accepted() =>
        Assert.True(Authorizer.Authorize(Token(c => { c.Remove("azp"); c["appid"] = ImpactAgent; })).Allowed);

    [Theory]
    [InlineData("tid", "99999999-9999-9999-9999-999999999999", "another tenant")]
    [InlineData("scp", "User.Read", "required scope missing")]
    [InlineData("scp", "graph.read", "required scope missing")]
    [InlineData("azp", "44444444-4444-4444-4444-444444444444", "not approved")]
    [InlineData("idtyp", "app", "does not act for a user")]
    [InlineData("preferred_username", "not-an-email", "user identity missing")]
    public void Unexpected_claims_are_denied(string claim, string value, string reason)
    {
        var decision = Authorizer.Authorize(Token(c => c[claim] = value));

        Assert.False(decision.Allowed);
        Assert.Null(decision.Requester);
        Assert.Contains(reason, decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("scp", "does not act for a user")]
    [InlineData("azp", "not approved")]
    [InlineData("oid", "user identity missing")]
    [InlineData("preferred_username", "user identity missing")]
    [InlineData("tid", "another tenant")]
    public void Missing_claims_are_denied(string claim, string reason) =>
        Assert.Contains(reason, Authorizer.Authorize(Token(c => c.Remove(claim))).Reason, StringComparison.Ordinal);

    [Fact]
    public void An_unauthenticated_principal_is_denied() =>
        Assert.False(Authorizer.Authorize(Token(authenticated: false)).Allowed);
}
