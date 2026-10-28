using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using VisionIQ.Api.Features.Health;

namespace VisionIQ.Api.Tests;

public sealed class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    // "Testing" keeps the repository .env file from being loaded, so results never depend on a developer's machine.
    private readonly WebApplicationFactory<Program> testFactory =
        factory.WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));

    [Fact]
    public async Task Health_returns_healthy_payload()
    {
        using var client = testFactory.CreateClient();

        var response = await client.GetAsync("/health");
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new HealthResponse("healthy", "VisionIQ.Api"), body);
    }

    [Fact]
    public async Task Health_uses_the_configured_service_name()
    {
        using var customized = testFactory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Service:Name"] = "Custom.Name" })));
        using var client = customized.CreateClient();

        var body = await client.GetFromJsonAsync<HealthResponse>("/health");

        Assert.Equal("Custom.Name", body?.Service);
    }

    [Fact]
    public async Task Unknown_routes_return_not_found()
    {
        using var client = testFactory.CreateClient();

        var response = await client.GetAsync("/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
