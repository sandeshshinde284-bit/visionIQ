using VisionIQ.Api.Configuration;
using VisionIQ.Api.Features.Evidence;
using VisionIQ.Api.Features.Backfill;
using VisionIQ.Api.Features.TicketFixes;
using VisionIQ.Ingestor.Cli;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Checkpoints;
using VisionIQ.Ingestor.Configuration;
using VisionIQ.Ingestor.Confluence;
using VisionIQ.Ingestor.Inventory;
using VisionIQ.Ingestor.Sources;
using VisionIQ.Ingestor.Sync;

// VisionIQ ingestor (LRN-01). Read-only: it never writes to Azure DevOps.
// Exit codes: 0 everything synced, 2 something partial or not synced, 1 configuration or usage error.

var options = CommandLine.Parse(args);
if (options.Error is not null)
{
    Console.Error.WriteLine(options.Error);
    Console.Error.WriteLine();
    Console.Error.WriteLine(CommandLine.Usage);
    return 1;
}

if (options.Command == CliCommand.Help)
{
    Console.WriteLine(CommandLine.Usage);
    return 0;
}

IngestorSettings settings;
Dictionary<string, string?> configuration;
try
{
    configuration = EnvFile.LoadMerged(Directory.GetCurrentDirectory());
    settings = IngestorSettings.FromValues(configuration);
}
catch (InvalidOperationException error)
{
    Console.Error.WriteLine("Configuration problems (check your .env):");
    Console.Error.WriteLine(error.Message);
    return 1;
}

var outputMode = configuration.GetValueOrDefault("INGESTION_OUTPUT_MODE")?.Trim().ToLowerInvariant() ?? "jsonl";
if (outputMode is not ("jsonl" or "canonical"))
{
    Console.Error.WriteLine("INGESTION_OUTPUT_MODE must be jsonl or canonical.");
    return 1;
}

var canonicalMode = outputMode == "canonical";
var archiveBucket = configuration.GetValueOrDefault("GCS_EVIDENCE_BUCKET");
if (canonicalMode && (settings.Database is null || string.IsNullOrWhiteSpace(archiveBucket)))
{
    Console.Error.WriteLine("Canonical evidence ingestion requires CHECKPOINT_STORE=postgres and GCS_EVIDENCE_BUCKET.");
    return 1;
}

var json = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter() },
};

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
var client = new AdoClient(http, settings.Ado);
var runDirectory = Path.Combine(settings.OutputDirectory, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
Directory.CreateDirectory(runDirectory);
if (!OperatingSystem.IsWindows())
{
    File.SetUnixFileMode(runDirectory,
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}
Console.WriteLine($"Azure DevOps: {settings.Ado}");
Console.WriteLine(settings.Confluence is { } confluenceSettings ? $"Confluence: {confluenceSettings}" : "Confluence: not configured");
var confluenceSource = settings.Confluence is { } configured ? ConfluenceSources.Create(http, configured) : null;

if (options.Command == CliCommand.Inventory)
{
    var inventory = await new InventoryRunner(client).RunAsync();
    var inventoryPath = Path.Combine(runDirectory, "inventory.json");
    await File.WriteAllTextAsync(inventoryPath, JsonSerializer.Serialize(inventory, json));

    foreach (var project in inventory.Projects)
    {
        var count = project.WorkItems is { } workItems
            ? workItems.ToString("N0", CultureInfo.InvariantCulture) + (project.WorkItemsCapped ? "+" : string.Empty)
            : "?";
        var pipelines = project.PipelineDefinitions?.ToString(CultureInfo.InvariantCulture) ?? "?";
        Console.WriteLine($"  {project.Name}: {project.Access}; work items {count}; repositories {project.Repositories.Count}; pipelines {pipelines}");
        foreach (var note in project.Notes)
        {
            Console.WriteLine($"    - {note}");
        }
    }

    var confluenceReachable = true;
    if (confluenceSource is not null)
    {
        foreach (var space in confluenceSource.SpaceKeys)
        {
            try
            {
                var pages = await confluenceSource.CountPagesAsync(space);
                Console.WriteLine($"  Confluence space {space}: ok; pages {pages.ToString("N0", CultureInfo.InvariantCulture)}");
            }
            catch (ConfluenceSourceException error)
            {
                confluenceReachable = false;
                Console.WriteLine($"  Confluence space {space}: {(error.IsAccessDenied ? "access denied" : "unreachable")} - {error.Message}");
            }
        }
    }

    Console.WriteLine($"Inventory written to {inventoryPath}");
    return inventory.AllReachable && confluenceReachable ? 0 : 2;
}

ICheckpointStore checkpoints = settings.CheckpointStore == CheckpointStoreKind.File
    ? new FileCheckpointStore(Path.Combine(settings.OutputDirectory, "checkpoints.json"))
    : new DbCheckpointStore(() => new NpgsqlConnection(settings.Database!.ConnectionString));
Console.WriteLine(settings.CheckpointStore == CheckpointStoreKind.File
    ? "Checkpoints: local file"
    : $"Checkpoints: PostgreSQL {settings.Database}");

var kinds = new HashSet<SyncKind>(options.Kinds);
if (kinds.Contains(SyncKind.Confluence) && confluenceSource is null)
{
    if (options.SourceExplicit)
    {
        Console.Error.WriteLine("Confluence is not configured: set CONFLUENCE_BASE_URL, CONFLUENCE_SPACE_KEYS and CONFLUENCE_API_TOKEN in .env.");
        return 1;
    }

    kinds.Remove(SyncKind.Confluence);
}

var request = new SyncRequest(kinds, options.Since ?? settings.Since, options.Full);
var registry = new SourceCapabilityRegistry();
var reports = new List<SyncReport>();

// LRN-03 is an opt-in trusted destination. JSONL remains a local export mode
// with restricted/unverified Confluence bodies excluded by the runner.
var dbOptions = canonicalMode
    ? new DatabaseOptions(
        settings.Database!.Host,
        settings.Database.Port,
        settings.Database.Name,
        settings.Database.User,
        settings.Database.Password!,
        settings.Database.SslMode)
    : null;
await using var evidenceStore = canonicalMode
    ? new PostgresEvidenceStore(dbOptions!)
    : null;
await using var linkStore = canonicalMode
    ? new PostgresTicketFixLinkStore(dbOptions!)
    : null;
using (var localSink = new JsonLinesSink(runDirectory))
{
    IRecordSink sink = localSink;
    if (canonicalMode)
    {
        var canonicalSink = new CanonicalEvidenceSink(
            new EvidenceIngestionService(
                new CloudStorageEvidenceArchive(archiveBucket), evidenceStore!),
            settings.Ado.OrganizationUrl, settings.Ado.Tenant, settings.Confluence?.BaseUrl);
        sink = new TicketFixProjectionSink(canonicalSink, linkStore!, settings.Ado.OrganizationUrl);
    }
    Console.WriteLine(canonicalMode
        ? "Evidence destination: canonical PostgreSQL + immutable Cloud Storage (unverified ACL, no retrieval access)"
        : "Evidence destination: local JSONL (restricted/unverified Confluence bodies withheld)");
    if (kinds.Overlaps([SyncKind.WorkItems, SyncKind.PullRequests, SyncKind.Commits, SyncKind.Builds]))
    {
        reports.Add(await new SyncRunner(client, checkpoints, sink, registry).RunAsync(request));
    }

    if (confluenceSource is not null && kinds.Contains(SyncKind.Confluence))
    {
        reports.Add(await new ConfluenceSyncRunner(confluenceSource, checkpoints, sink, registry).RunAsync(request));
    }
}

var report = SyncReport.Merge(reports);

// Persist one sanitized audit row per attempted source stream. This is a
// diagnostic record, not proof of complete source history or ACL verification.
if (settings.Database is { } auditDatabase)
{
    await using var audit = new PostgresSourceSyncRunStore(new DatabaseOptions(
        auditDatabase.Host, auditDatabase.Port, auditDatabase.Name,
        auditDatabase.User, auditDatabase.Password!, auditDatabase.SslMode));
    await SourceSyncAuditProjector.SaveAsync(
        report, request.IgnoreCheckpoints ? SourceSyncMode.Full : SourceSyncMode.Delta,
        audit);
}

await File.WriteAllTextAsync(Path.Combine(runDirectory, "report.json"), JsonSerializer.Serialize(report, json));
foreach (var stream in report.Streams)
{
    var reason = stream.Reason is null ? string.Empty : " - " + stream.Reason;
    var second = stream.Stream == SyncKind.Confluence ? $"{stream.Links} design decisions" : $"{stream.Links} links";
    Console.WriteLine($"  [{stream.Status}] {stream.Stream} {stream.Scope}: {stream.Records} records, {second}{reason}");
    foreach (var note in stream.Notes)
    {
        Console.WriteLine($"    - {note}");
    }
}

Console.WriteLine($"Output: {runDirectory}");
return report.AllSynced ? 0 : 2;
