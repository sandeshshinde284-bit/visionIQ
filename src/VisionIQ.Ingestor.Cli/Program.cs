using Microsoft.Extensions.Logging;
using VisionIQ.Api.Configuration;
using VisionIQ.Api.Features.Evidence;
using VisionIQ.Api.Features.Backfill;
using VisionIQ.Api.Features.TicketFixes;
using VisionIQ.Api.Features.Graph;
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

if (options.Command == CliCommand.ServeWebhooks)
{
    // LRN-05: webhooks write canonical evidence, so they need the same database and archive as canonical sync.
    if (!canonicalMode)
    {
        Console.Error.WriteLine("serve-webhooks requires INGESTION_OUTPUT_MODE=canonical.");
        return 1;
    }

    return await WebhookHost.RunAsync(settings, configuration,
        new DatabaseOptions(settings.Database!.Host, settings.Database.Port, settings.Database.Name,
            settings.Database.User, settings.Database.Password!, settings.Database.SslMode),
        archiveBucket!);
}

var json = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter() },
};

using var loggerFactory = IngestorLogging.Create(configuration);
var log = loggerFactory.CreateLogger("VisionIQ.Ingestor");
using var http = new HttpClient { Timeout = settings.RequestTimeout };
var client = new AdoClient(http, settings.Ado, logger: loggerFactory.CreateLogger<AdoClient>());
var runDirectory = Path.Combine(settings.OutputDirectory, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
Directory.CreateDirectory(runDirectory);
if (!OperatingSystem.IsWindows())
{
    File.SetUnixFileMode(runDirectory,
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}
Console.WriteLine($"Azure DevOps: {settings.Ado}");
Console.WriteLine(settings.Confluence is { } confluenceSettings ? $"Confluence: {confluenceSettings}" : "Confluence: not configured");
var confluenceSource = settings.Confluence is { } configured
    ? ConfluenceSources.Create(http, configured, logger: loggerFactory.CreateLogger("VisionIQ.Ingestor.Confluence"))
    : null;

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

var request = new SyncRequest(kinds, options.Since ?? settings.Since, options.Full) { Reconcile = options.Reconcile };
foreach (var warning in settings.EnvironmentWarnings)
{
    Console.Error.WriteLine("Warning: " + warning);
}
if (options.Since is not null && !options.Full)
{
    // m-11: say so instead of silently ignoring --since for streams that already have a checkpoint.
    Console.WriteLine("Note: --since applies only to streams without a checkpoint. Add --full to re-read from that date.");
}
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
await using var testLinkStore = canonicalMode
    ? new VisionIQ.Api.Features.TestLinks.PostgresTestLinkStore(dbOptions!)
    : null;
await using var buildLinkStore = canonicalMode
    ? new VisionIQ.Api.Features.BuildLinks.PostgresBuildLinkStore(dbOptions!)
    : null;
// G-1: per-item quarantine list (PostgreSQL in canonical mode; local mode lists items in sync-quarantine.jsonl).
await using var quarantineStore = canonicalMode ? new PostgresSyncQuarantine(dbOptions!) : null;
using (var localSink = new JsonLinesSink(runDirectory))
{
    IRecordSink sink = localSink;
    if (canonicalMode)
    {
        var canonicalSink = new CanonicalEvidenceSink(
            new EvidenceIngestionService(
                new CloudStorageEvidenceArchive(archiveBucket), evidenceStore!),
            settings.Ado.OrganizationUrl, settings.Ado.Tenant, settings.Confluence?.BaseUrl)
        {
            Tombstones = evidenceStore, // N-2
        };
        sink = new BuildLinkProjectionSink(
            new TestLinkProjectionSink(
                new TicketFixProjectionSink(canonicalSink, linkStore!, settings.Ado.OrganizationUrl),
                testLinkStore!, settings.Ado.OrganizationUrl),
            buildLinkStore!, settings.Ado.OrganizationUrl);
    }
    Console.WriteLine(canonicalMode
        ? "Evidence destination: canonical PostgreSQL + immutable Cloud Storage (ADO: project-level access, re-checked live; Confluence: hidden)"
        : "Evidence destination: local JSONL (restricted/unverified Confluence bodies withheld)");
    if (kinds.Overlaps([SyncKind.WorkItems, SyncKind.PullRequests, SyncKind.Commits, SyncKind.Builds]))
    {
        reports.Add(await new SyncRunner(client, checkpoints, sink, registry, environments: settings.Environments, quarantine: quarantineStore, logger: loggerFactory.CreateLogger<SyncRunner>()).RunAsync(request));
    }

    if (confluenceSource is not null && kinds.Contains(SyncKind.Confluence))
    {
        reports.Add(await new ConfluenceSyncRunner(confluenceSource, checkpoints, sink, registry, logger: loggerFactory.CreateLogger<ConfluenceSyncRunner>()).RunAsync(request));
    }
}

var report = SyncReport.Merge(reports);

// LRN-07 graph projection is a deterministic, replayable post-ingestion step.
// Links are materialized only when both canonical endpoints already exist;
// importing missing source history and rerunning safely fills the gaps.
var graphFailed = false;
if (canonicalMode && dbOptions is not null)
{
    // B-4: a graph projection failure must not lose the sync report or audit rows written below.
    try
    {
        await using var graphStore = new PostgresRelationshipGraphStore(dbOptions);
        await using var graphMaterializer = new TicketFixGraphMaterializer(dbOptions, graphStore);
        var edges = await graphMaterializer.ReconcileAsync(
            settings.Ado.OrganizationUrl.AbsoluteUri.TrimEnd('/'));
        Console.WriteLine($"Relationship graph assertions processed: {edges}");
        IngestorLogging.Projected(log, "ticket-fix", edges);
        foreach (var failure in graphMaterializer.FailedAssociations)
        {
            IngestorLogging.ProjectionFailed(log, "ticket-fix", failure);
        }
        if (graphMaterializer.FailedAssociations.Count > 0)
        {
            // M-1: failed associations are reported and the run exits non-zero; the rest were processed.
            graphFailed = true;
            Console.Error.WriteLine($"Relationship graph: {graphMaterializer.FailedAssociations.Count} association(s) failed:");
            foreach (var failure in graphMaterializer.FailedAssociations.Take(20))
            {
                Console.Error.WriteLine("  - " + failure);
            }
        }

        // LRN-07: declared test associations and exact automated-test mappings.
        await using var testLinks = new VisionIQ.Api.Features.Graph.TestLinkGraphMaterializer(dbOptions, graphStore);
        var testEdges = await testLinks.ReconcileAsync(settings.Ado.OrganizationUrl.AbsoluteUri.TrimEnd('/'));
        Console.WriteLine($"Test link assertions processed: {testEdges}");
        IngestorLogging.Projected(log, "test-links", testEdges);
        foreach (var gap in testLinks.Unresolved)
        {
            IngestorLogging.TestUnresolved(log, gap);
        }

        foreach (var failure in testLinks.Failed)
        {
            IngestorLogging.ProjectionFailed(log, "test-links", failure);
        }
        foreach (var gap in testLinks.Unresolved.Take(20))
        {
            Console.WriteLine("  unresolved automated test: " + gap);
        }

        // LRN-07: CI builds -> the commit they built and the pull request that triggered them.
        await using var buildLinks = new VisionIQ.Api.Features.Graph.BuildLinkGraphMaterializer(dbOptions, graphStore);
        var buildEdges = await buildLinks.ReconcileAsync(settings.Ado.OrganizationUrl.AbsoluteUri.TrimEnd('/'));
        Console.WriteLine($"Build link assertions processed: {buildEdges}");
        IngestorLogging.Projected(log, "build-links", buildEdges);
        foreach (var failure in buildLinks.Failed)
        {
            IngestorLogging.ProjectionFailed(log, "build-links", failure);
        }
        if (buildLinks.Failed.Count > 0)
        {
            graphFailed = true;
            foreach (var failure in buildLinks.Failed.Take(20))
            {
                Console.Error.WriteLine("  build link failed: " + failure);
            }
        }

        if (testLinks.Failed.Count > 0)
        {
            graphFailed = true;
            foreach (var failure in testLinks.Failed.Take(20))
            {
                Console.Error.WriteLine("  test link failed: " + failure);
            }
        }
    }
    catch (Exception error) when (error is not OperationCanceledException)
    {
        graphFailed = true;
        Console.Error.WriteLine($"Relationship graph projection failed ({error.GetType().Name}): {error.Message}");
        IngestorLogging.ProjectionStopped(log, error.GetType().Name);
    }
}

// Persist one sanitized audit row per attempted source stream. This is a
// diagnostic record, not proof of complete source history or ACL verification.
if (settings.Database is { } auditDatabase)
{
    await using var audit = new PostgresSourceSyncRunStore(new DatabaseOptions(
        auditDatabase.Host, auditDatabase.Port, auditDatabase.Name,
        auditDatabase.User, auditDatabase.Password!, auditDatabase.SslMode));
    await SourceSyncAuditProjector.SaveAsync(
        report, request.Reconcile ? SourceSyncMode.Reconcile : request.IgnoreCheckpoints ? SourceSyncMode.Full : SourceSyncMode.Delta,
        audit);
}

await File.WriteAllTextAsync(Path.Combine(runDirectory, "report.json"), JsonSerializer.Serialize(report, json));
IngestorLogging.ReportWritten(log, Path.Combine(runDirectory, "report.json"));
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
return report.AllSynced && !graphFailed ? 0 : 2;
