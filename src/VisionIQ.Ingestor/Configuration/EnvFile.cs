using System.Collections;

namespace VisionIQ.Ingestor.Configuration;

/// <summary>Reads the repository-root .env. Real environment variables always win over the file.</summary>
public static class EnvFile
{
    /// <summary>Walks up from <paramref name="startDirectory"/> and returns the first .env, stopping at the repository root.</summary>
    public static string? Find(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".env");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, "VisionIQ.sln")))
            {
                return null;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>KEY=VALUE lines; comments, blank lines and empty values are skipped, surrounding quotes removed.</summary>
    public static Dictionary<string, string?> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
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
            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            if (key.Length > 0 && value.Length > 0)
            {
                values[key] = value;
            }
        }

        return values;
    }

    /// <summary>The .env values overlaid with real environment variables.</summary>
    public static Dictionary<string, string?> LoadMerged(string startDirectory, IDictionary? environment = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (Find(startDirectory) is { } file)
        {
            foreach (var (key, value) in Parse(File.ReadLines(file)))
            {
                values[key] = value;
            }
        }

        foreach (DictionaryEntry entry in environment ?? Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value && value.Length > 0)
            {
                values[key] = value;
            }
        }

        return values;
    }
}
