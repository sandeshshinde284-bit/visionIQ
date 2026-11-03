using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Inventory;

namespace VisionIQ.Ingestor.Tests;

public sealed class InventoryRunnerTests
{
    [Fact]
    public async Task The_inventory_counts_what_the_token_can_read()
    {
        var report = await new InventoryRunner(DemoAdo.Client(DemoAdo.Server())).RunAsync();

        var project = Assert.Single(report.Projects);
        Assert.Equal("ok", project.Access);
        Assert.Equal(2, project.WorkItems);
        Assert.Equal(new[] { "payments-service" }, project.Repositories);
        Assert.Equal(2, project.PipelineDefinitions);
        Assert.True(report.AllReachable);
    }

    [Fact]
    public async Task A_project_that_does_not_exist_is_reported()
    {
        var options = new AdoOptions
        {
            OrganizationUrl = new Uri(DemoAdo.Org),
            Projects = ["Missing Project"],
            PersonalAccessToken = "test-token-not-real",
            MaxRetries = 0,
        };

        var report = await new InventoryRunner(DemoAdo.Client(DemoAdo.Server(), options)).RunAsync();

        Assert.Equal("not found", Assert.Single(report.Projects).Access);
        Assert.False(report.AllReachable);
    }
}
