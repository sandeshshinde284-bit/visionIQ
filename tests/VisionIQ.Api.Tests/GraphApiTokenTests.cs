using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace VisionIQ.Api.Tests;

/// <summary>
/// Hackathon scope: enterprise identity integration is deferred. Graph HTTP
/// routes remain unmapped even when configuration is supplied. Internal graph
/// persistence/traversal is tested separately without creating public access.
/// </summary>
public sealed class GraphApiTokenTests(
    WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient Client(bool supplyEnterpriseConfiguration) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("DB_API_USER", "visioniq_api");
            builder.UseSetting("DB_API_PASSWORD", "test-only-api-password");
            if (supplyEnterpriseConfiguration)
            {
                builder.UseSetting("VISIONIQ_AUTH_TENANT_ID",
                    "11111111-1111-1111-1111-111111111111");
                builder.UseSetting("VISIONIQ_AUTH_AUDIENCES", "api://visioniq");
                builder.UseSetting("VISIONIQ_AUTH_ALLOWED_CALLERS",
                    "22222222-2222-2222-2222-222222222222");
                builder.UseSetting("ADO_ORG_URL", "https://dev.azure.com/example");
                builder.UseSetting("ADO_PROJECTS", "example-project");
            }
        }).CreateClient();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_graph_route_is_not_exposed_during_hackathon(
        bool supplyEnterpriseConfiguration)
    {
        using var client = Client(supplyEnterpriseConfiguration);
        using var response = await client.GetAsync("/api/v1/graph/work-items/example-project/1");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
