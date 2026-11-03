using VisionIQ.Ingestor.Configuration;

namespace VisionIQ.Ingestor.Tests;

/// <summary>M-13, M-14, m-8: ingestor configuration defaults and parsing.</summary>
public sealed class IngestionSettingsTests
{
    private static Dictionary<string, string?> Valid(params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ADO_ORG_URL"] = "https://dev.azure.com/contoso",
            ["ADO_PROJECTS"] = "VisionIQ Demo",
            ["ADO_READ_PAT"] = "test-only-not-a-token",
            ["DB_API_USER"] = "visioniq_api",
            ["DB_API_PASSWORD"] = "test-only",
        };
        foreach (var (key, value) in extra)
        {
            values[key] = value;
        }

        return values;
    }

    [Fact]
    public void Request_timeout_defaults_to_100_seconds_and_can_be_configured()
    {
        Assert.Equal(TimeSpan.FromSeconds(100), IngestorSettings.FromValues(Valid()).RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(45), IngestorSettings.FromValues(Valid(("INGESTION_REQUEST_TIMEOUT_SECONDS", "45"))).RequestTimeout);
        Assert.Throws<InvalidOperationException>(() => IngestorSettings.FromValues(Valid(("INGESTION_REQUEST_TIMEOUT_SECONDS", "0"))));
    }

    [Fact]
    public void Database_ssl_defaults_to_prefer() =>
        Assert.Equal("prefer", IngestorSettings.FromValues(Valid()).Database!.SslMode);

    [Fact]
    public void Env_file_inline_comments_are_stripped_from_unquoted_values()
    {
        var values = EnvFile.Parse(["A=one # note", "B=\"two # kept\"", "C=x#y"]);

        Assert.Equal("one", values["A"]);
        Assert.Equal("two # kept", values["B"]);
        Assert.Equal("x#y", values["C"]);
    }
}
