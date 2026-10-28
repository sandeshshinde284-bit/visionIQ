using VisionIQ.Api.Configuration;

namespace VisionIQ.Api.Tests;

public sealed class DotEnvLoaderTests
{
    [Fact]
    public void Parse_reads_key_value_pairs_and_skips_comments_and_blank_lines()
    {
        var values = DotEnvLoader.Parse(["# comment", "", "DB_HOST=postgres", "  DB_PORT = 5432  "]);

        Assert.Equal("postgres", values["DB_HOST"]);
        Assert.Equal("5432", values["DB_PORT"]);
        Assert.Equal(2, values.Count);
    }

    [Fact]
    public void Parse_removes_surrounding_quotes()
    {
        var values = DotEnvLoader.Parse(["A=\"double\"", "B='single'"]);

        Assert.Equal("double", values["A"]);
        Assert.Equal("single", values["B"]);
    }

    [Fact]
    public void Parse_leaves_out_empty_values_so_defaults_apply()
    {
        var values = DotEnvLoader.Parse(["DB_PASSWORD=", "ANTHROPIC_API_KEY=''"]);

        Assert.Empty(values);
    }

    [Fact]
    public void FindFile_stops_at_the_repository_root()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, ".git"));
            var nested = Directory.CreateDirectory(Path.Combine(root.FullName, "src", "app"));

            Assert.Null(DotEnvLoader.FindFile(nested.FullName));

            var envFile = Path.Combine(root.FullName, ".env");
            File.WriteAllText(envFile, "DB_HOST=localhost");

            Assert.Equal(envFile, DotEnvLoader.FindFile(nested.FullName));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
