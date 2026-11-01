using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using VisionIQ.Api.Features.Evidence;
using VisionIQ.Api.Features.GraphApi;

namespace VisionIQ.Api.Tests;

/// <summary>
/// End-to-end token handling with Microsoft's real JwtBearer validation and locally signed test tokens. No real
/// Entra app registration is needed: the test signing key replaces Entra's published keys.
/// </summary>
public sealed class GraphApiTokenTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string OtherTenant = "99999999-9999-9999-9999-999999999999";
    private const string ImpactAgent = "22222222-2222-2222-2222-222222222222";
    private const string Audience = "api://visioniq";
    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test-key" };
    private static readonly RsaSecurityKey ForgedKey = new(RSA.Create(2048)) { KeyId = "test-key" };

    private sealed class FakeQuery : IGraphQueryService
    {
        public Task<GraphQueryResult?> QueryWorkItemAsync(string project, string workItemId, EvidenceRequester requester, int hops, CancellationToken cancellationToken = default) =>
            Task.FromResult(project == "Payments" && workItemId == "1"
                ? new GraphQueryResult("root", [], [])
                : null);
    }

    private static Dictionary<string, string?> Settings(bool graphApi) => new()
    {
        ["DB_API_USER"] = "visioniq_api",
        ["DB_API_PASSWORD"] = "test-only-api-password",
        ["ADO_ORG_URL"] = "https://dev.azure.com/contoso",
        ["ADO_PROJECTS"] = "Payments",
        ["ADO_READ_PAT"] = "test-only-not-a-token",
        ["VISIONIQ_AUTH_TENANT_ID"] = graphApi ? Tenant : null,
        ["VISIONIQ_AUTH_AUDIENCES"] = graphApi ? Audience : null,
        ["VISIONIQ_AUTH_ALLOWED_CALLERS"] = graphApi ? ImpactAgent : null,
    };

    private HttpClient Client(bool graphApi = true) => factory.WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(Settings(graphApi)));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IGraphQueryService, FakeQuery>();
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                // Use the test key instead of downloading Entra's signing keys; everything else is unchanged.
                options.Authority = null;
                options.Configuration = new OpenIdConnectConfiguration { Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0" };
                options.Configuration.SigningKeys.Add(SigningKey);
                options.TokenValidationParameters.IssuerSigningKey = SigningKey;
            });
        });
    }).CreateClient();

    private static string Token(
        RsaSecurityKey? key = null, string tenant = Tenant, string audience = Audience, string? scope = "Graph.Read",
        string caller = ImpactAgent, TimeSpan? expiresIn = null, bool appOnly = false)
    {
        var now = DateTime.UtcNow;
        var expires = now + (expiresIn ?? TimeSpan.FromMinutes(30));
        var claims = new Dictionary<string, object>
        {
            ["tid"] = tenant,
            ["azp"] = caller,
            ["oid"] = "33333333-3333-3333-3333-333333333333",
            ["preferred_username"] = "ana@contoso.com",
        };
        if (scope is not null)
        {
            claims["scp"] = scope;
        }

        if (appOnly)
        {
            claims["idtyp"] = "app";
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = $"https://login.microsoftonline.com/{tenant}/v2.0",
            Audience = audience,
            IssuedAt = expires.AddMinutes(-60),
            NotBefore = expires.AddMinutes(-60),
            Expires = expires,
            Claims = claims,
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }

    private async Task<HttpResponseMessage> Get(string? token, string path = "/api/v1/graph/work-items/Payments/1", bool graphApi = true)
    {
        using var client = Client(graphApi);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task A_valid_delegated_token_from_the_approved_app_is_answered_and_not_cached()
    {
        var response = await Get(Token());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Not_found_or_not_allowed_is_a_plain_404() =>
        Assert.Equal(HttpStatusCode.NotFound, (await Get(Token(), "/api/v1/graph/work-items/Payments/404")).StatusCode);

    [Fact]
    public async Task No_token_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(null)).StatusCode);

    [Fact]
    public async Task A_forged_signature_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(Token(key: ForgedKey))).StatusCode);

    [Fact]
    public async Task An_expired_token_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(Token(expiresIn: TimeSpan.FromMinutes(-10)))).StatusCode);

    [Fact]
    public async Task A_token_for_another_application_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(Token(audience: "api://some-other-app"))).StatusCode);

    [Fact]
    public async Task A_token_from_another_tenant_is_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(Token(tenant: OtherTenant))).StatusCode);

    [Fact]
    public async Task An_unapproved_calling_application_is_403() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(Token(caller: "44444444-4444-4444-4444-444444444444"))).StatusCode);

    [Fact]
    public async Task An_app_only_token_without_a_user_is_403() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(Token(scope: null, appOnly: true))).StatusCode);

    [Fact]
    public async Task A_token_without_the_required_scope_is_403() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(Token(scope: "User.Read"))).StatusCode);

    [Fact]
    public async Task Out_of_range_hops_are_rejected() =>
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(Token(), "/api/v1/graph/work-items/Payments/1?hops=9")).StatusCode);

    [Fact]
    public async Task Without_entra_settings_the_route_does_not_exist() =>
        Assert.Equal(HttpStatusCode.NotFound, (await Get(Token(), graphApi: false)).StatusCode);
}
