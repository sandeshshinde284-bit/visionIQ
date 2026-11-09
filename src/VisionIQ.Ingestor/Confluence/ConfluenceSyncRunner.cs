using System.Collections.Concurrent;
using System.Globalization;
using VisionIQ.Ingestor.Checkpoints;
using VisionIQ.Ingestor.Sources;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Confluence;

/// <summary>
/// Reads the approved spaces (LRN-02). Page content is written once per version, so older versions are kept.
/// Access-control classification/withholding was removed from this layer (2026-11-09, team decision); every
/// fetched page is written unconditionally.
/// </summary>
public sealed class ConfluenceSyncRunner
{
    private readonly IConfluenceSource source;
    private readonly ICheckpointStore checkpoints;
    private readonly IRecordSink sink;
    private readonly SourceCapabilityRegistry registry;
    private readonly TimeProvider time;
    private readonly int evidenceConcurrency;

    private readonly Microsoft.Extensions.Logging.ILogger log;

    public ConfluenceSyncRunner(IConfluenceSource source, ICheckpointStore checkpoints, IRecordSink sink, SourceCapabilityRegistry registry, TimeProvider? time = null, Microsoft.Extensions.Logging.ILogger? logger = null, int evidenceConcurrency = SyncRunner.DefaultEvidenceConcurrency)
    {
        log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(registry);
        this.source = source;
        this.checkpoints = checkpoints;
        this.sink = sink;
        this.registry = registry;
        this.time = time ?? TimeProvider.System;
        this.evidenceConcurrency = evidenceConcurrency > 0 ? evidenceConcurrency : SyncRunner.DefaultEvidenceConcurrency;
    }

    private string SourceId => source.Tenant + "/confluence";

    public async Task<SyncReport> RunAsync(SyncRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var scope = SyncRunLogging.BeginRun(log, request, "confluence");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var report = await RunCoreAsync(request, cancellationToken).ConfigureAwait(false);
        report.RunId = request.RunId;
        SyncRunLogging.LogOutcome(log, report, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return report;
    }

    private async Task<SyncReport> RunCoreAsync(SyncRequest request, CancellationToken cancellationToken)
    {
        var report = new SyncReport { Tenant = source.Tenant, StartedAt = time.GetUtcNow() };
        if (!request.Kinds.Contains(SyncKind.Confluence))
        {
            report.FinishedAt = time.GetUtcNow();
            return report;
        }

        foreach (var space in source.SpaceKeys)
        {
            var result = report.Add(SyncKind.Confluence, space + "/pages");
            try
            {
                await SyncSpaceAsync(space, request, result, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // B-4: one space's unexpected failure must not stop the other spaces.
                result.Status = result.Records > 0 ? StreamStatus.Partial : StreamStatus.NotSynced;
                var reason = $"Unexpected {error.GetType().Name}: {error.Message} Checkpoint not advanced.";
                result.Reason = result.Reason is null ? reason : result.Reason + " " + reason;
            }
        }

        report.FinishedAt = time.GetUtcNow();
        return report;
    }

    private async Task SyncSpaceAsync(string space, SyncRequest request, StreamResult result, CancellationToken cancellationToken)
    {
        var scope = space + "/pages";
        TimeCursor? cursor;
        try
        {
            cursor = request.IgnoreCheckpoints
                ? null
                : TimeCursor.Parse((await checkpoints.GetAsync(SourceId, scope, cancellationToken).ConfigureAwait(false))?.Cursor);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Fail(result, "Checkpoint store unavailable: " + error.Message + " (for the database store, run: python -m db.manage migrate).");
            return;
        }

        var written = new List<(DateTimeOffset At, string Id)>();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var pages = await source.GetPagesAsync(space, cancellationToken).ConfigureAwait(false);
            var byId = pages.ToDictionary(page => page.Id, StringComparer.Ordinal);

            // R3-01: every page/ACL write here is a real GCS+PostgreSQL round trip (canonical mode),
            // exactly the same cost profile that made LRN-01's ADO ingestion take 20+ minutes one
            // write at a time. Bounded concurrency (shared with SyncRunner's default) applies the same
            // fix here; this is a different bottleneck from the restriction-fetch concurrency already
            // added to ConfluenceCloudSource (that one is rate-limited by Confluence's own API, this
            // one by GCS/PostgreSQL round trips, so a separate gate is correct, not redundant).
            using var writeGate = new SemaphoreSlim(evidenceConcurrency);
            async Task GatedWriteAsync(string stream, string key, object value, CancellationToken token)
            {
                await writeGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await sink.WriteAsync(stream, key, value, token).ConfigureAwait(false);
                }
                finally
                {
                    writeGate.Release();
                }
            }

            var fresh = pages
                .Where(page => cursor is null
                    ? request.Since is null || page.LastModified >= request.Since
                    : cursor.IsNew(page.LastModified, page.Id + "@" + page.Version.ToString(CultureInfo.InvariantCulture)))
                .OrderBy(page => page.LastModified)
                .ThenBy(page => page.Id, StringComparer.Ordinal)
                .ToList();

            var recordsDone = 0;
            var linksDone = 0;
            var writtenBag = new ConcurrentBag<(DateTimeOffset At, string Id)>();

            await RunGatedAsync(fresh, async (page, token) =>
            {
                var enriched = await source.EnrichAsync(page, token).ConfigureAwait(false);
                var normalized = ConfluenceText.Normalize(enriched.StorageBody);
                var snapshot = new ConfluencePageSnapshot(source.Tenant, space, page.Id, page.Version, page.Title, page.LastModified)
                {
                    ParentId = page.ParentId,
                    AncestorIds = ConfluenceAccess.AncestorIds(page, byId),
                    Url = page.Url,
                    Labels = enriched.Labels,
                    Text = normalized.Text,
                    Sections = normalized.Sections,
                    Properties = normalized.Properties,
                    Versions = enriched.Versions,
                };

                var version = page.Version.ToString(CultureInfo.InvariantCulture);
                await GatedWriteAsync("confluence-pages", Key(space, "page", page.Id, version), snapshot, token).ConfigureAwait(false);

                var done = Interlocked.Increment(ref recordsDone);
                if (done % 50 == 0 || done == fresh.Count)
                {
                    Logging.IngestionLog.ConfluencePagePersistProgress(log, space, done, fresh.Count, timer.Elapsed.TotalMilliseconds);
                }

                writtenBag.Add((page.LastModified, page.Id + "@" + version));
            }, cancellationToken).ConfigureAwait(false);

            result.Records = recordsDone;
            result.Links = linksDone;
            written.AddRange(writtenBag);

            registry.Register(SourceKind.Confluence, "pages", CapabilityStatus.Supported);
            registry.Register(SourceKind.Confluence, "page-restrictions", CapabilityStatus.Supported);
        }
        catch (ConfluenceSourceException error)
        {
            Fail(result, error.Message);
        }

        if (!request.Reconcile && result.Status == StreamStatus.Synced && TimeCursor.Advance(cursor, written) is { } next)
        {
            try
            {
                // Review R1-04: written pages must be durable before the checkpoint moves past them.
                await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
                await checkpoints.SaveAsync(new SyncCheckpoint(SourceId, scope, next.Format(), time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Fail(result, "Pages were written but the checkpoint could not be saved: " + error.Message);
            }
        }
    }

    /// <summary>Bounded-concurrency ForEachAsync that unwraps a single real exception instead of
    /// letting Parallel.ForEachAsync's AggregateException hide it from the existing single-exception
    /// catch below (only observable when more than one item faults concurrently).</summary>
    private async Task RunGatedAsync<T>(List<T> items, Func<T, CancellationToken, ValueTask> body, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        try
        {
            await Parallel.ForEachAsync(items, new ParallelOptions
            {
                MaxDegreeOfParallelism = evidenceConcurrency,
                CancellationToken = cancellationToken,
            }, body).ConfigureAwait(false);
        }
        catch (AggregateException aggregate)
        {
            var flattened = aggregate.Flatten().InnerExceptions;
            Exception toRethrow = flattened.OfType<ConfluenceSourceException>().FirstOrDefault()
                ?? (flattened.Count == 1 ? flattened[0] : aggregate);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(toRethrow).Throw();
        }
    }

    private string Key(string space, string kind, string id, string? version) =>
        source.Tenant + "/confluence/" + space + "/" + kind + "/" + id + (version is null ? string.Empty : "@" + version);

    private static void Fail(StreamResult result, string reason)
    {
        result.Status = result.Records > 0 ? StreamStatus.Partial : StreamStatus.NotSynced;
        result.Reason = result.Reason is null ? reason : result.Reason + " " + reason;
    }
}

/// <summary>Creates the right source for the configured deployment.</summary>
public static class ConfluenceSources
{
    public static IConfluenceSource Create(HttpClient http, ConfluenceOptions options, Func<TimeSpan, CancellationToken, Task>? delay = null, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var client = new ConfluenceHttp(http, options, delay, logger);
        return options.Deployment == ConfluenceDeployment.Cloud
            ? new ConfluenceCloudSource(client, logger)
            : new ConfluenceDataCenterSource(client);
    }
}
