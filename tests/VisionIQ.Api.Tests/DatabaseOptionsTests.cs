using Microsoft.Extensions.Configuration;
using VisionIQ.Api.Configuration;

namespace VisionIQ.Api.Tests;

public sealed class DatabaseOptionsTests
{
    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Defaults_match_the_local_docker_database()
    {
        var options = DatabaseOptions.FromConfiguration(Build(new()));

        Assert.Equal("localhost", options.Host);
        Assert.Equal(5432, options.Port);
        Assert.Equal("disable", options.SslMode);
        Assert.Null(options.Password);
    }

    [Fact]
    public void Values_are_read_from_the_shared_env_keys()
    {
        var options = DatabaseOptions.FromConfiguration(Build(new()
        {
            ["DB_HOST"] = "postgres",
            ["DB_PORT"] = "6543",
            ["DB_NAME"] = "demo",
            ["DB_USER"] = "demo_user",
            ["DB_PASSWORD"] = "secret-value",
        }));

        Assert.Equal(("postgres", 6543, "demo", "demo_user"), (options.Host, options.Port, options.Name, options.User));
        Assert.Equal("secret-value", options.Password);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("abc")]
    public void Invalid_ports_are_rejected(string port)
    {
        var configuration = Build(new() { ["DB_PORT"] = port });

        Assert.Throws<InvalidOperationException>(() => DatabaseOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void ToString_never_contains_the_password()
    {
        var options = DatabaseOptions.FromConfiguration(Build(new() { ["DB_PASSWORD"] = "secret-value" }));

        Assert.DoesNotContain("secret-value", options.ToString(), StringComparison.Ordinal);
    }
}
