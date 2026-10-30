using Microsoft.Extensions.Configuration;
using VisionIQ.Api.Configuration;

namespace VisionIQ.Api.Tests;

public sealed class DatabaseOptionsTests
{
    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> RuntimeCredentials() => new()
    {
        ["DB_API_USER"] = "visioniq_api",
        ["DB_API_PASSWORD"] = "runtime-secret",
    };

    [Fact]
    public void Defaults_match_the_local_docker_database()
    {
        var options = DatabaseOptions.FromConfiguration(Build(RuntimeCredentials()));

        Assert.Equal("localhost", options.Host);
        Assert.Equal(5432, options.Port);
        Assert.Equal("disable", options.SslMode);
        Assert.Equal("visioniq_api", options.User);
        Assert.Equal("runtime-secret", options.Password);
    }

    [Fact]
    public void Reads_only_the_dedicated_api_identity()
    {
        var values = RuntimeCredentials();
        values["DB_HOST"] = "postgres";
        values["DB_PORT"] = "6543";
        values["DB_NAME"] = "demo";
        values["DB_USER"] = "admin";
        values["DB_PASSWORD"] = "admin-secret";

        var options = DatabaseOptions.FromConfiguration(Build(values));

        Assert.Equal(("postgres", 6543, "demo", "visioniq_api"),
            (options.Host, options.Port, options.Name, options.User));
        Assert.Equal("runtime-secret", options.Password);
    }

    [Theory]
    [InlineData("DB_API_USER")]
    [InlineData("DB_API_PASSWORD")]
    public void Missing_runtime_credentials_fail_even_with_admin_fallback(string missing)
    {
        var values = RuntimeCredentials();
        values.Remove(missing);
        values["DB_USER"] = "admin";
        values["DB_PASSWORD"] = "admin-secret";

        var error = Assert.Throws<InvalidOperationException>(
            () => DatabaseOptions.FromConfiguration(Build(values)));

        Assert.Contains(missing, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("abc")]
    public void Invalid_ports_are_rejected(string port)
    {
        var values = RuntimeCredentials();
        values["DB_PORT"] = port;

        Assert.Throws<InvalidOperationException>(
            () => DatabaseOptions.FromConfiguration(Build(values)));
    }

    [Fact]
    public void ToString_never_contains_the_password()
    {
        var options = DatabaseOptions.FromConfiguration(Build(RuntimeCredentials()));

        Assert.DoesNotContain("runtime-secret", options.ToString(), StringComparison.Ordinal);
    }
}
