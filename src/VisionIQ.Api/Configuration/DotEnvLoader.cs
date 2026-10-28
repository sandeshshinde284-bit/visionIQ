namespace VisionIQ.Api.Configuration;

/// <summary>
/// Loads the repository-root .env file into configuration for local runs on the host.
/// .NET does not read .env files by itself.
/// </summary>
public static class DotEnvLoader
{
    /// <summary>Walks up from <paramref name="startDirectory"/> and returns the first .env file, stopping at the repository root.</summary>
    public static string? FindFile(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".env");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var isRepositoryRoot = Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, "VisionIQ.sln"));
            if (isRepositoryRoot)
            {
                return null;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Parses KEY=VALUE lines. Blank lines and # comments are skipped, surrounding quotes are removed,
    /// and keys with an empty value are left out so they fall back to defaults.
    /// </summary>
    public static Dictionary<string, string?> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = Unquote(line[(separator + 1)..].Trim());
            if (key.Length > 0 && value.Length > 0)
            {
                values[key] = value;
            }
        }

        return values;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }
}

public static class DotEnvConfigurationExtensions
{
    /// <summary>Adds the nearest .env file (if any) as a configuration source.</summary>
    public static IConfigurationBuilder AddDotEnvFile(this IConfigurationBuilder builder, string startDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var file = DotEnvLoader.FindFile(startDirectory);
        return file is null
            ? builder
            : builder.AddInMemoryCollection(DotEnvLoader.Parse(File.ReadLines(file)));
    }
}
