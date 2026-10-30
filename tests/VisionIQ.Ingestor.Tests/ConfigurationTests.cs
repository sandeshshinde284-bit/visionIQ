using System.Collections;
using System.Data.Common;
using VisionIQ.Ingestor.Configuration;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Tests;

public sealed class ConfigurationTests
{
    private static Dictionary<string, string?> Valid() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["ADO_ORG_URL"] = "https://dev.azure.com/contoso",
        ["ADO_PROJECTS"] = "VisionIQ Demo, Second",
        ["ADO_READ_PAT"] = "secret-token-value",
        ["DB_API_USER"] = "visioniq_api",
        ["DB_API_PASSWORD"] = "p;w\"d",
    };

    [Fact]
    public void Valid_values_produce_settings()
    {
        var settings = IngestorSettings.FromValues(Valid());

        Assert.Equal("contoso", settings.Ado.Tenant);
        Assert.Equal(new[] { "VisionIQ Demo", "Second" }, settings.Ado.Projects);
        Assert.Equal(CheckpointStoreKind.Postgres, settings.CheckpointStore);
        Assert.Equal(Path.Combine("artifacts", "ingestion"), settings.OutputDirectory);
    }

    [Fact]
    public void Every_missing_value_is_reported_together_without_secrets()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            IngestorSettings.FromValues(new Dictionary<string, string?> { ["DB_API_PASSWORD"] = "secret-db" }));

        foreach (var key in new[] { "ADO_ORG_URL", "ADO_PROJECTS", "ADO_READ_PAT", "DB_API_USER" })
        {
            Assert.Contains(key, error.Message, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("secret-db", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plain_http_organization_address_is_rejected()
    {
        var values = Valid();
        values["ADO_ORG_URL"] = "http://dev.azure.com/contoso";

        var error = Assert.Throws<InvalidOperationException>(() => IngestorSettings.FromValues(values));

        Assert.Contains("https://", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_visualstudio_com_address_gives_the_organization_as_tenant()
    {
        var values = Valid();
        values["ADO_ORG_URL"] = "https://contoso.visualstudio.com";

        Assert.Equal("contoso", IngestorSettings.FromValues(values).Ado.Tenant);
    }

    [Fact]
    public void Secrets_never_appear_in_text_output()
    {
        var settings = IngestorSettings.FromValues(Valid());

        Assert.DoesNotContain("secret-token-value", settings.Ado.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("p;w", settings.Database!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_connection_string_quotes_special_characters()
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = IngestorSettings.FromValues(Valid()).Database!.ConnectionString };

        Assert.Equal("p;w\"d", builder["Password"]);
        Assert.Equal("Disable", builder["SSL Mode"]);
    }

    [Fact]
    public void File_checkpoints_need_no_database_credentials()
    {
        var values = Valid();
        values.Remove("DB_API_USER");
        values.Remove("DB_API_PASSWORD");
        values["CHECKPOINT_STORE"] = "file";

        var settings = IngestorSettings.FromValues(values);

        Assert.Equal(CheckpointStoreKind.File, settings.CheckpointStore);
        Assert.Null(settings.Database);
    }

    [Fact]
    public void Pipeline_ids_and_the_start_date_are_parsed()
    {
        var values = Valid();
        values["ADO_PIPELINE_IDS"] = "12, 13";
        values["INGESTION_SINCE"] = "2024-01-01";

        var settings = IngestorSettings.FromValues(values);

        Assert.Equal(new[] { 12, 13 }, settings.Ado.PipelineIds);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), settings.Since);
    }

    [Fact]
    public void Environment_variables_override_the_env_file()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, ".git"));
            File.WriteAllLines(Path.Combine(root.FullName, ".env"), new[] { "ADO_PROJECTS=FromFile", "ADO_REPOS=from-file", "EMPTY_VALUE=" });

            var merged = EnvFile.LoadMerged(root.FullName, new Hashtable { ["ADO_PROJECTS"] = "FromEnvironment" });

            Assert.Equal("FromEnvironment", merged["ADO_PROJECTS"]);
            Assert.Equal("from-file", merged["ADO_REPOS"]);
            Assert.False(merged.ContainsKey("EMPTY_VALUE"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Sync_options_are_parsed()
    {
        var options = CommandLine.Parse(["sync", "--source", "pull-requests,commits", "--since", "2024-01-01", "--full"]);

        Assert.Null(options.Error);
        Assert.Equal(CliCommand.Sync, options.Command);
        Assert.True(options.Full);
        Assert.True(options.Kinds.SetEquals(new[] { SyncKind.PullRequests, SyncKind.Commits }));
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), options.Since);
    }

    [Fact]
    public void Unknown_sources_and_commands_are_errors()
    {
        Assert.NotNull(CommandLine.Parse(["sync", "--source", "everything"]).Error);
        Assert.NotNull(CommandLine.Parse(["upload"]).Error);
    }

    [Fact]
    public void No_arguments_shows_help_and_inventory_is_recognised()
    {
        Assert.Equal(CliCommand.Help, CommandLine.Parse([]).Command);
        Assert.Equal(CliCommand.Inventory, CommandLine.Parse(["inventory"]).Command);
    }
}
