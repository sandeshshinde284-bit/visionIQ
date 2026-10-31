using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using VisionIQ.Api.Features.Health;

namespace VisionIQ.Api.Tests;

/// <summary>FND-04: database readiness is distinct from liveness and never leaks credentials.</summary>
public sealed class DatabaseReadinessEndpointTests
{
    /// <summary>Tests an unavailable DB and a normal process liveness response.</summary>
    [Fact]
    public async Task Unreachable_database_returns_sanitized_503_without_blocking_health()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["DB_HOST"] = "127.0.0.1",
                        ["DB_PORT"] = "1",
                        ["DB_NAME"] = "test_database",
                        ["DB_API_USER"] = "visioniq_api",
                        ["DB_API_PASSWORD"] = "secret-should-never-be-rendered",
                        ["DB_SSL_MODE"] = "disable",
                    }));
            });
        using var client = factory.CreateClient();
        var readiness = await client.GetAsync("/ready");
        var response = await readiness.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        Assert.DoesNotContain("secret-should-never-be-rendered", response);
        Assert.DoesNotContain("test_database", response);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    /// <summary>Runs a real restricted-role SELECT 1 only on disposable CI PostgreSQL.</summary>
    [Fact]
    public async Task Disposable_database_uses_restricted_api_identity_for_readiness()
    {
        if (Environment.GetEnvironmentVariable("VISIONIQ_READY_DB_INTEGRATION") != "1")
        {
            return;
        }

        var password = Environment.GetEnvironmentVariable("DB_API_PASSWORD");
        Assert.False(string.IsNullOrWhiteSpace(password));

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["DB_HOST"] = Environment.GetEnvironmentVariable("DB_HOST") ?? "127.0.0.1",
                        ["DB_PORT"] = "5432",
                        ["DB_NAME"] = Environment.GetEnvironmentVariable("DB_LOCAL_NAME") ?? "visioniq",
                        ["DB_API_USER"] = Environment.GetEnvironmentVariable("DB_API_USER") ?? "visioniq_api",
                        ["DB_API_PASSWORD"] = password!,
                        ["DB_SSL_MODE"] = "disable",
                    }));
            });
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new DatabaseReadinessResponse("ready"),
            await response.Content.ReadFromJsonAsync<DatabaseReadinessResponse>());
    }
}
