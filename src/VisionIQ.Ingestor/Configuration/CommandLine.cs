using System.Globalization;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Configuration;

public enum CliCommand
{
    Help,
    Inventory,
    Sync,
}

public sealed record CliOptions(CliCommand Command, IReadOnlySet<SyncKind> Kinds, DateTimeOffset? Since, bool Full, string? Error)
{
    /// <summary>True when --source was given, so a requested but unconfigured source is an error rather than skipped.</summary>
    public bool SourceExplicit { get; init; }
}

public static class CommandLine
{
    public const string Usage = """
        VisionIQ ingestor (LRN-01): reads Azure DevOps history. Read-only.

        Usage (from the repository root):
          dotnet run --project src/VisionIQ.Ingestor.Cli -- inventory
          dotnet run --project src/VisionIQ.Ingestor.Cli -- sync [--source all|work-items|pull-requests|commits|builds|confluence] [--since 2024-01-01] [--full]

        Settings come from the repository .env: ADO_ORG_URL, ADO_PROJECTS, ADO_READ_PAT,
        and optionally ADO_REPOS, ADO_PIPELINE_IDS, INGESTION_SINCE, INGESTION_OUTPUT_DIR, CHECKPOINT_STORE,
        CONFLUENCE_BASE_URL, CONFLUENCE_SPACE_KEYS, CONFLUENCE_USER_EMAIL, CONFLUENCE_API_TOKEN.
        --source  one or more kinds, comma-separated (default: all configured sources)
        --since   read from this date when there is no checkpoint (overrides INGESTION_SINCE)
        --full    ignore saved checkpoints and read again from --since (or all history)
        """;

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var all = SyncRequest.AllKinds;
        if (args.Count == 0 || args[0] is "help" or "-h" or "--help")
        {
            return new CliOptions(CliCommand.Help, all, null, false, null);
        }

        if (args[0] == "inventory")
        {
            return args.Count == 1
                ? new CliOptions(CliCommand.Inventory, all, null, false, null)
                : new CliOptions(CliCommand.Help, all, null, false, "inventory takes no options.");
        }

        if (args[0] != "sync")
        {
            return new CliOptions(CliCommand.Help, all, null, false, $"Unknown command '{args[0]}'.");
        }

        IReadOnlySet<SyncKind> kinds = all;
        DateTimeOffset? since = null;
        var full = false;
        var explicitSource = false;
        for (var index = 1; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--full":
                    full = true;
                    break;
                case "--source" when index + 1 < args.Count:
                    var parsedKinds = ParseKinds(args[++index]);
                    if (parsedKinds is null)
                    {
                        return new CliOptions(CliCommand.Help, all, null, false, "--source must be all, work-items, pull-requests, commits, builds or confluence.");
                    }

                    kinds = parsedKinds;
                    explicitSource = true;
                    break;
                case "--since" when index + 1 < args.Count:
                    if (!DateTimeOffset.TryParse(args[++index], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedSince))
                    {
                        return new CliOptions(CliCommand.Help, all, null, false, "--since must be a date such as 2024-01-01.");
                    }

                    since = parsedSince;
                    break;
                default:
                    return new CliOptions(CliCommand.Help, all, null, false, $"Unknown or incomplete option '{args[index]}'.");
            }
        }

        return new CliOptions(CliCommand.Sync, kinds, since, full, null) { SourceExplicit = explicitSource };
    }

    private static HashSet<SyncKind>? ParseKinds(string text)
    {
        var kinds = new HashSet<SyncKind>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "all":
                    kinds.UnionWith(SyncRequest.AllKinds);
                    break;
                case "work-items":
                    kinds.Add(SyncKind.WorkItems);
                    break;
                case "pull-requests":
                    kinds.Add(SyncKind.PullRequests);
                    break;
                case "commits":
                    kinds.Add(SyncKind.Commits);
                    break;
                case "builds":
                    kinds.Add(SyncKind.Builds);
                    break;
                case "confluence":
                    kinds.Add(SyncKind.Confluence);
                    break;
                default:
                    return null;
            }
        }

        return kinds.Count == 0 ? null : kinds;
    }
}
