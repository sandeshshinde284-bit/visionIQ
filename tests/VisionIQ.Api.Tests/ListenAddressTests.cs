using Microsoft.Extensions.Configuration;
using VisionIQ.Api.Configuration;

namespace VisionIQ.Api.Tests;

public sealed class ListenAddressTests
{
    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Falls_back_to_port_5100()
    {
        Assert.Equal("http://localhost:5100", ListenAddress.Resolve(Build(new())));
    }

    [Fact]
    public void Uses_ASPNETCORE_URLS_when_set()
    {
        var configuration = Build(new() { ["ASPNETCORE_URLS"] = "http://+:5100" });

        Assert.Equal("http://+:5100", ListenAddress.Resolve(configuration));
    }

    [Fact]
    public void PORT_from_Cloud_Run_wins_over_everything_else()
    {
        var configuration = Build(new() { ["PORT"] = "8080", ["ASPNETCORE_URLS"] = "http://+:5100" });

        Assert.Equal("http://0.0.0.0:8080", ListenAddress.Resolve(configuration));
    }
}
