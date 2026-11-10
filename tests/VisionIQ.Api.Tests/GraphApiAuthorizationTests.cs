using Microsoft.Extensions.Configuration;
using VisionIQ.Api.Features.GraphApi;

namespace VisionIQ.Api.Tests;

/// <summary>
/// Graph HTTP authentication is deferred for the hackathon. Verify that the
/// retired Microsoft Entra configuration cannot reactivate the endpoint.
/// End-user authorization will be tested when the protected API is restored.
/// </summary>
public sealed class GraphApiAuthorizationTests
{
    [Fact]
    public void Missing_enterprise_configuration_disables_the_graph_api()
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        Assert.Null(GraphApiOptions.FromConfiguration(config));
    }

    [Fact]
    public void Legacy_enterprise_configuration_does_not_reactivate_the_graph_api()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VISIONIQ_AUTH_TENANT_ID"] = "11111111-1111-1111-1111-111111111111",
                ["VISIONIQ_AUTH_AUDIENCES"] = "api://visioniq",
                ["VISIONIQ_AUTH_SCOPE"] = "Graph.Read",
                ["VISIONIQ_AUTH_ALLOWED_CALLERS"] = "22222222-2222-2222-2222-222222222222",
                ["ADO_ORG_URL"] = "https://dev.azure.com/example",
                ["ADO_PROJECTS"] = "example",
            })
            .Build();
        Assert.Null(GraphApiOptions.FromConfiguration(config));
    }

    [Fact]
    public void Null_configuration_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => GraphApiOptions.FromConfiguration(null!));
    }
}
