using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Checkpoints;
using VisionIQ.Ingestor.Links;
using VisionIQ.Ingestor.Sources;

namespace VisionIQ.Ingestor.Sync;

/// <summary>
/// Reads every configured project, resuming each stream from its checkpoint. A source that fails is reported
/// as NOT SYNCED with the reason; the others continue. Items are processed oldest first and the checkpoint
/// advances only past items that were written, so an interrupted run resumes without duplicates.
/// </summary>
public sealed class SyncRunner
{
    private static readonly Action<ILogger, string, DateTimeOffset?, bool, Exception?> LogWorkItemsStart =
        LoggerMessage.Define<string, DateTimeOffset?, bool>(
            LogLevel.Information,
            new EventId(1001, nameof(LogWorkItemsStart)),
            "ADO WorkItems START project={Project}, since={Since}, ignoreCheckpoints={IgnoreCheckpoints}");

    private static readonly Action<ILogger, Exception?> LogWorkItemsLoadingCheckpoint =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1002, nameof(LogWorkItemsLoadingCheckpoint)),
            "ADO WorkItems: loading checkpoint");

    private static readonly Action<ILogger, long, Exception?> LogWorkItemsCheckpointReady =
        LoggerMessage.Define<long>(
            LogLevel.Information,
            new EventId(1003, nameof(LogWorkItemsCheckpointReady)),
            "ADO WorkItems: checkpoint ready after {ElapsedMs}ms; querying IDs");

    private static readonly Action<ILogger, int, long, Exception?> LogWorkItemsQueried =
        LoggerMessage.Define<int, long>(
            LogLevel.Information,
            new EventId(1004, nameof(LogWorkItemsQueried)),
            "ADO WorkItems: query returned {Count} IDs after {ElapsedMs}ms; fetching records");

    private static readonly Action<ILogger, int, long, Exception?> LogWorkItemsFetched =
        LoggerMessage.Define<int, long>(
            LogLevel.Information,
            new EventId(1005, nameof(LogWorkItemsFetched)),
            "ADO WorkItems: fetched {Count} outcomes after {ElapsedMs}ms; loading quarantine");

    private static readonly Action<ILogger, long, Exception?> LogWorkItemsQuarantineLoaded =
        LoggerMessage.Define<long>(
            LogLevel.Information,
            new EventId(1006, nameof(LogWorkItemsQuarantineLoaded)),
            "ADO WorkItems: quarantine loaded after {ElapsedMs}ms");

    private static readonly Action<ILogger, int, int, long, Exception?> LogWorkItemsPersisted =
        LoggerMessage.Define<int, int, long>(
            LogLevel.Information,
            new EventId(1007, nameof(LogWorkItemsPersisted)),
            "ADO WorkItems: persisted {Count}/{Total} records after {ElapsedMs}ms");

    private static readonly Action<ILogger, int, long, Exception?> LogWorkItemsBeginningPersistence =
        LoggerMessage.Define<int, long>(
            LogLevel.Information,
            new EventId(1008, nameof(LogWorkItemsBeginningPersistence)),
            "ADO WorkItems: beginning persistence of {Total} records after {ElapsedMs}ms");

    private static readonly Action<ILogger, int, int, int, Exception?>
    LogDeletionStart =
        LoggerMessage.Define<int, int, int>(
            LogLevel.Information,
            new EventId(1010, nameof(LogDeletionStart)),
            "Recycle Bin: starting deletion {Index}/{Total}, ID={Id}");

    private static readonly Action<ILogger, int, int, int, long, Exception?>
    LogDeletionCompleted =
        LoggerMessage.Define<int, int, int, long>(
            LogLevel.Information,
            new EventId(1011, nameof(LogDeletionCompleted)),
            "Recycle Bin: completed {Index}/{Total}, ID={Id}, elapsed={ElapsedMs}ms");

    private static readonly Action<ILogger, int, int, int, Exception?>
        LogDeletionFailed =
            LoggerMessage.Define<int, int, int>(
                LogLevel.Error,
                new EventId(1012, nameof(LogDeletionFailed)),
                "Recycle Bin: FAILED at ID={Id}, index={Index}/{Total}");

    private static readonly Action<ILogger, int, Exception?>
        LogDeletionsFinished =
            LoggerMessage.Define<int>(
                LogLevel.Information,
                new EventId(1013, nameof(LogDeletionsFinished)),
                "Recycle Bin: finished processing {Count} deleted IDs");

    private static readonly Action<ILogger, string, Exception?>
    LogRepositoryDiscoveryStart =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1014, nameof(LogRepositoryDiscoveryStart)),
            "ADO Repository discovery START project={Project}");

    private static readonly Action<ILogger, string, int, Exception?>
        LogRepositoryDiscoveryCompleted =
            LoggerMessage.Define<string, int>(
                LogLevel.Information,
                new EventId(1015, nameof(LogRepositoryDiscoveryCompleted)),
                "ADO Repository discovery COMPLETED project={Project}, count={Count}");

    private static readonly Action<ILogger, string, int, int, int, Exception?>
    LogPullRequestEnrichmentStart =
        LoggerMessage.Define<string, int, int, int>(
            LogLevel.Information,
            new EventId(1018, nameof(LogPullRequestEnrichmentStart)),
            "ADO PR enrichment START repository={Repository}, index={Index}/{Total}, PR={PullRequestId}");

    private static readonly Action<ILogger, string, int, int, int, Exception?>
        LogPullRequestEnrichmentCompleted =
            LoggerMessage.Define<string, int, int, int>(
                LogLevel.Information,
                new EventId(1019, nameof(LogPullRequestEnrichmentCompleted)),
                "ADO PR enrichment COMPLETED repository={Repository}, index={Index}/{Total}, PR={PullRequestId}");

    private static readonly Action<ILogger, string, int, Exception?>
        LogPullRequestsFinished =
            LoggerMessage.Define<string, int>(
                LogLevel.Information,
                new EventId(1020, nameof(LogPullRequestsFinished)),
                "ADO PullRequests FINISHED repository={Repository}, processed={Count}");

    private static readonly Action<ILogger, string, Exception?>
    LogPullRequestsCheckpointLoaded =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1021, nameof(LogPullRequestsCheckpointLoaded)),
            "ADO PullRequests CHECKPOINT LOADED repository={Repository}");

    private static readonly Action<ILogger, string, Exception?>
    LogRepositoryDiscovered =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1022, nameof(LogRepositoryDiscovered)),
            "ADO Repository discovered: {Repository}");

    private static readonly Action<ILogger, string, Exception?>
    LogPullRequestsStart =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1016, nameof(LogPullRequestsStart)),
            "ADO PullRequests START repository={Repository}");

    private static readonly Action<ILogger, string, int, Exception?>
        LogPullRequestsFetched =
            LoggerMessage.Define<string, int>(
                LogLevel.Information,
                new EventId(1017, nameof(LogPullRequestsFetched)),
                "ADO PullRequests FETCHED repository={Repository}, count={Count}");

    private static readonly Action<ILogger, string, Exception?> LogCheckpointSaved =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1023, nameof(LogCheckpointSaved)),
            "ADO ingestion checkpoint SAVED scope={Scope}");

    /// <summary>R2-01: how many evidence writes (GCS archive + PostgreSQL) run at once. One at a time made
    /// 116 real work items take over 20 minutes in canonical mode; this is a deliberate, bounded cap on
    /// real network concurrency, not unbounded fan-out.</summary>
    public const int DefaultEvidenceConcurrency = 8;

    /// <summary>
    /// How far behind the commit checkpoint each run looks again. A commit's committer date is set by the developer's
    /// Git client, not when it reached the branch: a feature-branch commit merged later keeps its older date and
    /// would otherwise fall behind a checkpoint that already passed it. Commits in this window that are not stored
    /// yet are ingested; stored ones are skipped (when the sink can tell). Older stragglers need a --full run.
    /// </summary>
    public static readonly TimeSpan DefaultCommitLookback = TimeSpan.FromDays(30);

    private static readonly Action<ILogger, string, string, string, string, string, Exception?> LogRecordFailed =
        LoggerMessage.Define<string, string, string, string, string>(
            LogLevel.Error,
            new EventId(1024, nameof(LogRecordFailed)),
            "ADO {Kind} {Id} FAILED at {Stage}: {ErrorType}: {Message}");

    private static readonly Action<ILogger, string, string, string, Exception?> LogCheckpointKept =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(1025, nameof(LogCheckpointKept)),
            "ADO ingestion checkpoint KEPT scope={Scope}: this --full run is at {RunAt}, behind the stored {StoredAt}");

    private static readonly Action<ILogger, string, int, string, string, Exception?> LogPullRequestTimings =
        LoggerMessage.Define<string, int, string, string>(
            LogLevel.Information,
            new EventId(1027, nameof(LogPullRequestTimings)),
            "ADO PR TIMINGS repository={Repository}, PR={PullRequestId}, size=[{Size}]: {Timings}");

    private static readonly Action<ILogger, string, int, Exception?> LogLateCommits =
        LoggerMessage.Define<string, int>(
            LogLevel.Information,
            new EventId(1026, nameof(LogLateCommits)),
            "ADO Commits repository={Repository}: {Count} commit(s) older than the checkpoint were not stored yet (merged later); ingesting them");

    private readonly AdoClient client;
    private readonly ICheckpointStore checkpoints;
    private readonly IRecordSink sink;
    private readonly SourceCapabilityRegistry registry;
    private readonly TimeProvider time;
    private readonly AdoWorkItemSource workItems;
    private readonly AdoRepositorySource repositories;
    private readonly AdoCommitSource commits;
    private readonly AdoBuildSource builds;
    private readonly int evidenceConcurrency;
    private readonly TimeSpan commitLookback;

    /// <summary>
    /// True when failed items can be recorded durably and retried later (a database quarantine), so a checkpoint may
    /// move past them. The default in-memory quarantine forgets everything at exit, so with it the checkpoint always
    /// stops before the first failed item instead.
    /// </summary>
    private readonly bool durableQuarantine;

    public SyncRunner(AdoClient client, ICheckpointStore checkpoints, IRecordSink sink, SourceCapabilityRegistry registry, TimeProvider? time = null, Defects.DefectEnvironmentClassifier? environments = null, ISyncQuarantine? quarantine = null, Microsoft.Extensions.Logging.ILogger? logger = null, int evidenceConcurrency = DefaultEvidenceConcurrency, TimeSpan? commitLookback = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(registry);

        this.client = client;
        this.checkpoints = checkpoints;
        this.sink = sink;
        this.registry = registry;
        this.time = time ?? TimeProvider.System;
        workItems = new AdoWorkItemSource(client, environments);
        this.quarantine = quarantine ?? new InMemorySyncQuarantine();
        durableQuarantine = this.quarantine is not InMemorySyncQuarantine;
        this.commitLookback = commitLookback is { } lookback && lookback >= TimeSpan.Zero ? lookback : DefaultCommitLookback;
        log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        repositories = new AdoRepositorySource(client);
        commits = new AdoCommitSource(client);
        builds = new AdoBuildSource(client);
        this.evidenceConcurrency = evidenceConcurrency > 0 ? evidenceConcurrency : DefaultEvidenceConcurrency;
    }

    private string SourceId => client.Options.Tenant + "/ado";

    public async Task<SyncReport> RunAsync(SyncRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var scope = SyncRunLogging.BeginRun(log, request, "ado");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        // Each project stream (work items, PRs, commits, builds) runs to completion independently.
        // One failure doesn't cascade: we report the outcome per stream and continue.
        var report = await RunCoreAsync(request, cancellationToken).ConfigureAwait(false);
        report.RunId = request.RunId;
        SyncRunLogging.LogOutcome(log, report, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return report;
    }

    private async Task<SyncReport> RunCoreAsync(SyncRequest request, CancellationToken cancellationToken)
    {
        reconciling = request.Reconcile;
        ignoringCheckpoints = request.IgnoreCheckpoints;
        storedCheckpointAt.Clear();
        var report = new SyncReport { Tenant = client.Options.Tenant, StartedAt = time.GetUtcNow() };

        foreach (var project in client.Options.Projects)
        {
            if (request.Kinds.Contains(SyncKind.WorkItems))
            {
                await IsolateAsync(report, SyncKind.WorkItems, project, () => SyncWorkItemsAsync(project, request, report, cancellationToken), cancellationToken).ConfigureAwait(false);
            }

            var wantsPullRequests = request.Kinds.Contains(SyncKind.PullRequests);
            var wantsCommits = request.Kinds.Contains(SyncKind.Commits);
            if (wantsPullRequests || wantsCommits)
            {
                IReadOnlyList<RepositoryRecord> repositories = [];
                await IsolateAsync(
                    report,
                    wantsPullRequests ? SyncKind.PullRequests : SyncKind.Commits,
                    project + "/*",
                    async () => repositories = await ResolveRepositoriesAsync(project, wantsPullRequests, wantsCommits, report, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                foreach (var repository in repositories)
                {
                    if (wantsPullRequests)
                    {
                        await IsolateAsync(report, SyncKind.PullRequests, project + "/" + repository.Name, () => SyncPullRequestsAsync(project, repository, request, report, cancellationToken), cancellationToken).ConfigureAwait(false);
                    }

                    if (wantsCommits)
                    {
                        await IsolateAsync(report, SyncKind.Commits, project + "/" + repository.Name, () => SyncCommitsAsync(project, repository, request, report, cancellationToken), cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (request.Kinds.Contains(SyncKind.Builds))
            {
                await IsolateAsync(report, SyncKind.Builds, project, () => SyncBuildsAsync(project, request, report, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
        }

        report.FinishedAt = time.GetUtcNow();
        return report;
    }

    private async Task<IReadOnlyList<RepositoryRecord>> ResolveRepositoriesAsync(
        string project, bool wantsPullRequests, bool wantsCommits, SyncReport report, CancellationToken cancellationToken)
    {
        IReadOnlyList<RepositoryRecord> found;
        try
        {
            LogRepositoryDiscoveryStart(log, project, null);

            found = await repositories
                .GetRepositoriesAsync(project, cancellationToken)
                .ConfigureAwait(false);

            LogRepositoryDiscoveryCompleted(log, project, found.Count, null);

            foreach (var repo in found)
            {
                LogRepositoryDiscovered(log, repo.Name, null);
            }

            registry.Register(
                SourceKind.AzureDevOpsRepos,
                "repositories",
                CapabilityStatus.Supported);
        }
        catch (AdoSourceException error)
        {
            if (wantsPullRequests)
            {
                NotSynced(report.Add(SyncKind.PullRequests, project + "/*"), "Repositories: " + error.Message);
            }

            if (wantsCommits)
            {
                NotSynced(report.Add(SyncKind.Commits, project + "/*"), "Repositories: " + error.Message);
            }

            return [];
        }

        var wanted = client.Options.Repositories;
        if (wanted.Count == 0)
        {
            return found;
        }

        foreach (var missing in wanted.Where(name => !found.Any(repository => string.Equals(repository.Name, name, StringComparison.OrdinalIgnoreCase))))
        {
            const string reason = "Repository not found in the project, or it is disabled.";
            if (wantsPullRequests)
            {
                NotSynced(report.Add(SyncKind.PullRequests, project + "/" + missing), reason);
            }

            if (wantsCommits)
            {
                NotSynced(report.Add(SyncKind.Commits, project + "/" + missing), reason);
            }
        }

        return found.Where(repository => wanted.Contains(repository.Name, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    private async Task SyncWorkItemsAsync(string project, SyncRequest request, SyncReport report, CancellationToken cancellationToken)
    {
        // Work items are the source of truth for what was broken and fixed. We need to:
        // 1. Resume from the last checkpoint so we don't re-read everything each run
        // 2. Capture every revision if requested (for audit trail)
        // 3. Handle the 20k WIQL result limit by capping and retrying
        // 4. Quarantine any items that fail to read so we can retry them without skipping forward
        var scope = project + "/work-items";
        var result = report.Add(SyncKind.WorkItems, scope);
        var timer = System.Diagnostics.Stopwatch.StartNew();

        LogWorkItemsStart(log, project, request.Since, request.IgnoreCheckpoints, null);

        TimeCursor? cursor = null;
        var written = new List<(DateTimeOffset At, string Id)>();
        var capped = false;
        try
        {
            LogWorkItemsLoadingCheckpoint(log, null);

            cursor = await LoadCursorAsync(scope, request, cancellationToken).ConfigureAwait(false);
            LogWorkItemsCheckpointReady(log, timer.ElapsedMilliseconds, null);


            // Review R1-01: "asOf" is the instant the WIQL query itself was evaluated at. Passing it to
            // the batch read too makes both calls agree on one snapshot, so an item changed while this
            // (possibly long) query was running is read again next run instead of silently skipped.
            var query = await workItems.QueryIdsAsync(project, cursor?.At ?? request.Since, cancellationToken).ConfigureAwait(false);
            var ids = query.Ids;

            LogWorkItemsQueried(log, ids.Count, timer.ElapsedMilliseconds, null);

            var outcomes = await workItems.GetWorkItemsAsync(project, ids, query.AsOf, cancellationToken).ConfigureAwait(false);

            LogWorkItemsFetched(log, outcomes.Count, timer.ElapsedMilliseconds, null);
            var open = await quarantine.OpenAsync(SourceId, scope, "work_item", cancellationToken).ConfigureAwait(false);
            LogWorkItemsQuarantineLoaded(log, timer.ElapsedMilliseconds, null);
            var fresh = new List<WorkItemRecord>();

            // The WIQL result is ordered by (ChangedDate, Id), so an unreadable item's position in it is its
            // position in time even though its record (and ChangedDate) is unknown. The checkpoint may advance up
            // to, never past, the first unreadable item: everything before it is complete, and it is read again
            // next run. (Previously one unreadable item disabled every checkpoint save of the stream.)
            var wiqlIndex = new Dictionary<int, int>(ids.Count);
            for (var position = 0; position < ids.Count; position++)
            {
                wiqlIndex.TryAdd(ids[position], position);
            }

            var firstUnreadable = int.MaxValue;
            for (var index = 0; index < outcomes.Count; index++)
            {
                var outcome = outcomes[index];
                // Outcomes are returned one per requested ID, in the requested order.
                var nativeId = ids[index].ToString(CultureInfo.InvariantCulture);

                // Check if this item was previously quarantined (failed before).
                // If it's now readable, mark it as resolved and move forward.
                if (outcome.Record is not null && open.Contains(nativeId))
                {
                    await quarantine.ResolveAsync(SourceId, scope, "work_item", nativeId, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                    Logging.IngestionLog.QuarantineResolved(log, "work_item", nativeId, scope);
                }

                if (outcome.Record is null)
                {
                    firstUnreadable = Math.Min(firstUnreadable, index);
                    result.NotStored++;
                    // Unreadable items go to quarantine. By NOT advancing the checkpoint past them,
                    // we guarantee retry on the next run. The checkpoint tracks the *last written* item,
                    // so skipped items are always re-queried. This is G-1: graceful degradation.
                    var failed = new QuarantinedItem(SourceId, scope, "work_item", nativeId, "not_readable",
                        outcome.NotSyncedReason ?? "A work item could not be read.", time.GetUtcNow());
                    await quarantine.AddAsync(failed, cancellationToken).ConfigureAwait(false);
                    Logging.IngestionLog.Quarantined(log, "work_item", nativeId, scope, failed.ReasonCode);
                    await sink.WriteAsync("sync-quarantine", $"{SourceId}/{scope}/work_item/{nativeId}", failed, cancellationToken).ConfigureAwait(false);
                    result.Quarantined++;
                    Partial(result, outcome.NotSyncedReason ?? "A work item could not be read.");
                    continue;
                }

                var record = outcome.Record with { Project = project };
                if (cursor is null || cursor.IsNew(record.ChangedDate, Text(record.Id)))
                {
                    fresh.Add(record);
                }
            }

            LogWorkItemsBeginningPersistence(log, fresh.Count, timer.ElapsedMilliseconds, null);
            var ordered = fresh.OrderBy(item => item.ChangedDate).ThenBy(item => item.Id).ToList();

            // R2-01: each evidence write is a real GCS upload followed by a PostgreSQL insert
            // (CanonicalEvidenceSink -> EvidenceIngestionService.ImportAsync). Doing that one write at a
            // time per record (item, then every revision, then every link) made 116 real work items take
            // over 20 minutes. One shared gate caps how many of those writes are ever in flight at once,
            // so overlapping records/revisions/links cannot turn into unbounded concurrent network calls.
            using var writeGate = new SemaphoreSlim(evidenceConcurrency);
            var writtenBag = new ConcurrentBag<(DateTimeOffset At, string Id)>();
            var completedIds = new ConcurrentDictionary<int, bool>();
            // Progress may be saved while the stream is complete so far, or only partial because of unreadable
            // (quarantined) items, which the checkpoint stops in front of.
            var canSaveProgress = result.Status == StreamStatus.Synced || result.Quarantined > 0;
            var recordsDone = 0;
            var linksWritten = 0;

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

            async ValueTask ProcessWorkItemAsync(WorkItemRecord record, CancellationToken token)
            {
                await GatedWriteAsync("work-items", Key(project, "work-item", Text(record.Id), Text(record.Revision)), record, token).ConfigureAwait(false);

                if (request.IncludeRevisions)
                {
                    var revisions = await workItems.GetRevisionsAsync(project, record.Id, token).ConfigureAwait(false);
                    await Task.WhenAll(revisions.Select(revision => GatedWriteAsync(
                        "work-item-revisions", Key(project, "work-item", Text(revision.Id), Text(revision.Revision)), revision, token))).ConfigureAwait(false);
                }

                var links = LinkExtractor.FromWorkItem(record);
                if (links.Count > 0)
                {
                    await Task.WhenAll(links.Select(link =>
                        GatedWriteAsync("links", Key(project, "link", link.Key, null), link, token))).ConfigureAwait(false);
                    Interlocked.Add(ref linksWritten, links.Count);
                }

                var done = Interlocked.Increment(ref recordsDone);
                if (done % 25 == 0)
                {
                    LogWorkItemsPersisted(log, done, ordered.Count, timer.ElapsedMilliseconds, null);
                }

                // Review R1-01 defense-in-depth: even though GetWorkItemsAsync was asked for field
                // values as of the query's own snapshot, never let the saved checkpoint move past that
                // snapshot. ChangedDate stored in the record itself is never clamped (real source data).
                var checkpointAt = query.AsOf is { } asOf && record.ChangedDate > asOf ? asOf : record.ChangedDate;
                writtenBag.Add((checkpointAt, Text(record.Id)));
                completedIds[record.Id] = true;
            }

            (DateTimeOffset At, string Id) Position(WorkItemRecord record) => (
                query.AsOf is { } asOf && record.ChangedDate > asOf ? asOf : record.ChangedDate,
                Text(record.Id));

            // Safe to leave behind: fully written, and before the first unreadable item in WIQL order.
            bool Safe(WorkItemRecord record) =>
                completedIds.ContainsKey(record.Id) &&
                wiqlIndex.TryGetValue(record.Id, out var position) && position < firstUnreadable;

            try
            {
                // Persist each fully completed batch before processing the next
                // batch, so an interrupted backfill does not lose its cursor.
                const int checkpointBatchSize = 25;
                for (var offset = 0; offset < ordered.Count; offset += checkpointBatchSize)
                {
                    var batch = ordered.Skip(offset).Take(checkpointBatchSize).ToList();
                    Exception? batchError = null;
                    try
                    {
                        await Parallel.ForEachAsync(batch, new ParallelOptions
                        {
                            MaxDegreeOfParallelism = evidenceConcurrency,
                            CancellationToken = cancellationToken,
                        }, ProcessWorkItemAsync).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        batchError = error;
                    }

                    result.Records = recordsDone;
                    result.Links = linksWritten;

                    // The checkpoint covers only the completed run of this batch in (ChangedDate, Id) order, up to
                    // the first failed, unfinished or unreadable item. Items completed after that point (parallel
                    // completion is out of order) are written again next run; never skipped.
                    var safe = CheckpointProgress.SafePrefix(batch, Position, Safe);
                    if (safe.Count > 0 && canSaveProgress && !request.Reconcile)
                    {
                        cursor = TimeCursor.Advance(cursor, safe);
                        // On cancellation, still record what is complete (a short, best-effort save).
                        if (!await SaveCursorAsync(scope, cursor, result,
                            batchError is OperationCanceledException ? CancellationToken.None : cancellationToken).ConfigureAwait(false))
                        {
                            return; // the checkpoint could not be saved; SaveCursorAsync reported why
                        }
                    }

                    if (batchError is not null)
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(batchError).Throw();
                    }
                }
            }
            catch (AggregateException aggregate)
            {
                // Parallel.ForEachAsync only wraps in AggregateException when more than one iteration
                // faults concurrently; unwrap so the existing single-exception handling below still
                // applies instead of silently falling through as an unrecognized exception type.
                var flattened = aggregate.Flatten().InnerExceptions;
                Exception toRethrow = flattened.OfType<CheckpointStoreException>().FirstOrDefault()
                    ?? flattened.OfType<AdoSourceException>().FirstOrDefault()
                    ?? (flattened.Count == 1 ? flattened[0] : aggregate);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(toRethrow).Throw();
            }

            result.Records = recordsDone;
            result.Links = linksWritten;
            written.AddRange(writtenBag);

            if (ids.Count >= AdoWorkItemSource.WiqlLimit)
            {
                // B-10: the query is ordered by ChangedDate, Id ascending. When every returned item was
                // written, checkpointing at the last written item is safe (same-timestamp IDs are kept in
                // the cursor boundary) and lets the next run continue instead of re-reading this batch.
                capped = result.Status == StreamStatus.Synced && written.Count > 0;
                Partial(result, capped
                    ? "The query returned the 20,000-item maximum; progress was saved, run sync again to continue."
                    : "The query returned the 20,000-item maximum and could not advance (missing items or one shared timestamp); narrow INGESTION_SINCE or investigate.");
            }

            registry.Register(SourceKind.AzureDevOpsWorkItems, "work-items", CapabilityStatus.Supported);
        }
        catch (AdoSourceException error)
        {
            Failed(result, error.Message);
        }
        catch (CheckpointStoreException error)
        {
            Failed(result, error.Message);
            return;
        }

        // Omitted/failed records may be older than the newest successful record.
        // Never checkpoint beyond an incomplete work-item batch; replay uses the
        // source revision key and the downstream sink must deduplicate.
        // A batch that is complete but capped (B-10) still saves its progress.
        if (result.Status == StreamStatus.Synced || (capped && result.Status == StreamStatus.Partial))
        {
            await SaveCursorAsync(scope, TimeCursor.Advance(cursor, written), result, cancellationToken).ConfigureAwait(false);
        }


        // N-2: deleted items never appear in change queries.

        try
        {
            var observed = time.GetUtcNow();

            var deleted = await workItems
                .GetRecycleBinIdsAsync(project, cancellationToken)
                .ConfigureAwait(false);

            Logging.IngestionLog.DeletionsFound(log, project, deleted.Count);

            var processed = 0;

            foreach (var id in deleted)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // 1. Before processing each deletion
                LogDeletionStart(
                    log, processed + 1, deleted.Count, id, null);

                try
                {
                    await sink.WriteAsync(
                        "work-item-deletions",
                        $"{client.Options.Tenant}/ado/{project}/work-item/{id.ToString(CultureInfo.InvariantCulture)}/deleted",
                        new WorkItemDeletion(
                            id, project, observed, "deleted_in_source"),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 2. When a deletion fails
                    LogDeletionFailed(
                        log, id, processed + 1, deleted.Count, ex);

                    throw;
                }

                processed++;

                // 3. After successful processing
                LogDeletionCompleted(
                    log, processed, deleted.Count, id,
                    stopwatch.ElapsedMilliseconds, null);
            }

            // 4. After all 415 deletions finish
            LogDeletionsFinished(log, processed, null);
        }
        catch (AdoSourceException error)
        {
            result.Notes.Add(
                "Recycle Bin could not be read; deletions not checked this run: "
                + error.Message);
        }


    }


    private async Task SyncPullRequestsAsync(
        string project,
        RepositoryRecord repository,
        SyncRequest request,
        SyncReport report,
        CancellationToken cancellationToken)
    {
        var scope = project + "/" + repository.Name + "/pull-requests";
        var result = report.Add(SyncKind.PullRequests, scope);

        TimeCursor? cursor = null;

        // Checkpoint progress comes only from each batch's completed set, read in the batch's sorted order
        // (CheckpointProgress.SafePrefix below); the parallel workers record nothing else.
        var processed = 0;
        var linksWritten = 0;
        var total = 0;

        try
        {
            LogPullRequestsStart(log, repository.Name, null);

            cursor = await LoadCursorAsync(
                scope, request, cancellationToken).ConfigureAwait(false);

            LogPullRequestsCheckpointLoaded(log, repository.Name, null);

            var all = await repositories.GetPullRequestsAsync(
                project, repository, cancellationToken).ConfigureAwait(false);

            LogPullRequestsFetched(log, repository.Name, all.Count, null);

            // PRs that failed in an earlier run and were quarantined (durable quarantine only): read again
            // whatever the checkpoint says, until they succeed.
            var retry = durableQuarantine
                ? await quarantine.OpenAsync(SourceId, scope, "pull_request", cancellationToken).ConfigureAwait(false)
                : new HashSet<string>(StringComparer.Ordinal);

            // Always refresh active PRs, even when their creation date
            // is older than the saved checkpoint.
            var fresh = all
                .Where(item =>
                    IsActive(item)
                    || retry.Contains(Text(item.Id))
                    || (cursor is null
                        ? request.Since is null
                          || item.LastActivity >= request.Since
                        : cursor.IsNew(
                            item.LastActivity,
                            Text(item.Id))))
                .OrderBy(item => item.LastActivity)
                .ThenBy(item => item.Id)
                .ToList();

            total = fresh.Count;

            // Start conservatively. Never exceed the configured
            // evidence concurrency limit.
            var parallelism = Math.Min(4, evidenceConcurrency);

            // Each PR is processed on its own: a failure is captured with its PR ID and step, the other PRs of
            // the batch still finish, and the checkpoint then covers only what is safe to leave behind.
            const int checkpointBatchSize = 25;
            for (var offset = 0; offset < fresh.Count; offset += checkpointBatchSize)
            {
                var start = offset;
                var count = Math.Min(checkpointBatchSize, fresh.Count - start);
                var completed = new ConcurrentDictionary<int, bool>();
                var failures = new ConcurrentDictionary<int, RecordProcessingException>();

                Exception? interrupted = null;
                try
                {
                    await Parallel.ForEachAsync(
                        Enumerable.Range(start, count),
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = parallelism,
                            CancellationToken = cancellationToken
                        },
                        async (index, token) =>
                        {
                            var pullRequest = fresh[index];
                            LogPullRequestEnrichmentStart(
                                log, repository.Name, index + 1, total, pullRequest.Id, null);
                            try
                            {
                                var linkCount = await ProcessPullRequestAsync(project, repository, pullRequest, token).ConfigureAwait(false);

                                // Mark complete only after all related evidence has been written.
                                completed[pullRequest.Id] = true;
                                Interlocked.Add(ref linksWritten, linkCount);
                                Interlocked.Increment(ref processed);
                                LogPullRequestEnrichmentCompleted(
                                    log, repository.Name, index + 1, total, pullRequest.Id, null);
                            }
                            catch (RecordProcessingException error)
                            {
                                failures[pullRequest.Id] = error;
                                LogRecordFailed(log, "pull request", pullRequest.Id.ToString(CultureInfo.InvariantCulture),
                                    error.Stage, error.InnerException?.GetType().Name ?? nameof(Exception),
                                    error.InnerException?.Message ?? error.Message, error.InnerException);
                            }
                        }).ConfigureAwait(false);
                }
                catch (Exception error) when (error is OperationCanceledException or AggregateException)
                {
                    // Cancellation (or several cancellations at once): save what is complete, then stop.
                    interrupted = error;
                }

                // Record every failure durably where possible, so the checkpoint may move past it and it is
                // retried next run. Without a durable quarantine the checkpoint stops in front of it instead.
                var quarantinedNow = new HashSet<int>();
                foreach (var (id, failure) in failures.OrderBy(pair => pair.Key))
                {
                    Partial(result, failure.Message);
                    if (durableQuarantine && interrupted is null)
                    {
                        var nativeId = id.ToString(CultureInfo.InvariantCulture);
                        await quarantine.AddAsync(new QuarantinedItem(
                            SourceId, scope, "pull_request", nativeId, "processing_failed",
                            Truncate(failure.Message, 2000), time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                        Logging.IngestionLog.Quarantined(log, "pull_request", nativeId, scope, "processing_failed");
                        result.Quarantined++;
                        quarantinedNow.Add(id);
                    }
                }

                // A PR that was quarantined earlier and now succeeded is resolved.
                foreach (var resolvedId in completed.Keys.Select(Text).Where(retry.Contains))
                {
                    await quarantine.ResolveAsync(SourceId, scope, "pull_request", resolvedId, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                    Logging.IngestionLog.QuarantineResolved(log, "pull_request", resolvedId, scope);
                }

                // Safe to leave behind: written; or failed but read again next run anyway (an active PR is
                // always re-selected; a quarantined one is re-selected until it succeeds).
                bool Safe(PullRequestRecord item) =>
                    completed.ContainsKey(item.Id) ||
                    (failures.ContainsKey(item.Id) && (IsActive(item) || quarantinedNow.Contains(item.Id)));

                var batch = fresh.GetRange(start, count);
                var safe = CheckpointProgress.SafePrefix(batch, item => (item.LastActivity, Text(item.Id)), Safe);
                if (safe.Count > 0 && !request.Reconcile)
                {
                    cursor = TimeCursor.Advance(cursor, safe);
                    if (!await SaveCursorAsync(scope, cursor, result,
                        interrupted is null ? cancellationToken : CancellationToken.None).ConfigureAwait(false))
                    {
                        return;
                    }
                }

                if (interrupted is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(interrupted).Throw();
                }

                if (safe.Count < count)
                {
                    // A failed PR the checkpoint cannot pass (no durable quarantine): stop here rather than
                    // write PRs the next run has to read again anyway. The failure is in the stream's reason.
                    Partial(result, "Stopped at the first PR that failed; it and the PRs after it are read again next run.");
                    return;
                }
            }

            LogPullRequestsFinished(
                log,
                repository.Name,
                processed,
                null);

            registry.Register(
                SourceKind.AzureDevOpsRepos,
                "pull-requests",
                CapabilityStatus.Supported);
        }
        catch (AdoSourceException error)
        {
            Failed(result, error.Message);
        }
        catch (CheckpointStoreException error)
        {
            Failed(result, error.Message);
        }
        finally
        {
            // Preserve counts even when the batch fails.
            result.Records = Volatile.Read(ref processed);
            result.Links = Volatile.Read(ref linksWritten);

            // A failure must not be reported as a successful sync.
            // IsolateAsync handles other unexpected exceptions.
        }

        // Each batch has already been checkpointed up to its safe prefix. Reconcile runs
        // intentionally preserve the previous cursor.
    }

    /// <summary>
    /// Enriches one PR and writes its evidence, reviews and links. Any failure is reported as a
    /// <see cref="RecordProcessingException"/> naming the PR and the step that failed. Returns the link count.
    /// </summary>
    private async Task<int> ProcessPullRequestAsync(
        string project, RepositoryRecord repository, PullRequestRecord pullRequest, CancellationToken token)
    {
        var id = Text(pullRequest.Id);
        var stage = "ADO enrichment";

        // Phase 2 instrumentation: every stage of this PR adds its time here, including the Cloud Storage
        // archive, PostgreSQL write and ticket-fix projection inside the sinks; one log line per PR at the end.
        using var timings = Diagnostics.StageTimings.Begin();
        try
        {
            PullRequestRecord enriched;
            using (Diagnostics.StageTimings.Measure("ado_enrich"))
            {
                enriched = await repositories.EnrichAsync(project, repository, pullRequest, token).ConfigureAwait(false);
            }

            stage = "evidence write and projections";
            var version = enriched.Status + "@" + Stamp(enriched.LastActivity);
            using (Diagnostics.StageTimings.Measure("pr_write"))
            {
                await sink.WriteAsync(
                    "pull-requests",
                    Key(project, "pull-request", Text(enriched.Id), version),
                    enriched, token).ConfigureAwait(false);
            }

            stage = "review projection";
            using (Diagnostics.StageTimings.Measure("reviews"))
            {
                await PullRequestReviewProjection.WriteAsync(sink, client.Options.Tenant, enriched, token).ConfigureAwait(false);
            }

            stage = "link write";
            var links = LinkExtractor.FromPullRequest(enriched);
            int linkCount;
            using (Diagnostics.StageTimings.Measure("links"))
            {
                linkCount = await WriteLinksAsync(project, links, token).ConfigureAwait(false);
            }

            if (log.IsEnabled(LogLevel.Information))
            {
                var size = string.Create(CultureInfo.InvariantCulture,
                    $"workItems={enriched.WorkItemIds.Count} commits={enriched.CommitIds.Count} reviewers={enriched.Reviewers.Count} links={linkCount}");
                LogPullRequestTimings(log, repository.Name, pullRequest.Id, size, timings.Timings.Summary(), null);
            }

            return linkCount;
        }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
        {
            throw new RecordProcessingException("pull request", id, stage, error);
        }
    }

    private static bool IsActive(PullRequestRecord item) =>
        item.Status.Equals("active", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];


    private async Task SyncCommitsAsync(string project, RepositoryRecord repository, SyncRequest request, SyncReport report, CancellationToken cancellationToken)
    {
        var scope = project + "/" + repository.Name + "/commits";
        var result = report.Add(SyncKind.Commits, scope);
        TimeCursor? cursor = null;
        var written = new List<(DateTimeOffset At, string Id)>();
        const int checkpointBatchSize = 25;
        try
        {
            cursor = await LoadCursorAsync(scope, request, cancellationToken).ConfigureAwait(false);

            // The committer date is the developer's clock, not when the commit reached this branch, so a commit
            // merged after the checkpoint passed its date would never match "from the checkpoint" again. Re-read a
            // window behind the checkpoint and ingest the commits in it that are not stored yet.
            // A zero look-back keeps the previous behaviour: read from the checkpoint only.
            var lookBack = cursor is not null && commitLookback > TimeSpan.Zero;
            var from = cursor is null ? request.Since : lookBack ? cursor.At - commitLookback : cursor.At;
            if (lookBack && request.Since is { } since && from < since)
            {
                from = since; // never read before the configured INGESTION_SINCE
            }

            var all = await commits.GetCommitsAsync(project, repository, from, cancellationToken).ConfigureAwait(false);
            var behindCursor = lookBack
                ? all.Where(item => !cursor!.IsNew(item.CommitterDate, item.CommitId)).ToList()
                : [];
            var late = behindCursor;
            if (behindCursor.Count > 0)
            {
                var stored = await sink.FindStoredCommitIdsAsync(
                    project, repository.Id, behindCursor.Select(item => item.CommitId).ToList(), cancellationToken).ConfigureAwait(false);
                // null: the sink cannot tell, so the whole window is written again (idempotent, never skipped).
                late = stored is null ? behindCursor : behindCursor.Where(item => !stored.Contains(item.CommitId)).ToList();
            }

            if (late.Count > 0)
            {
                LogLateCommits(log, repository.Name, late.Count, null);
            }

            var lateIds = late.Select(item => item.CommitId).ToHashSet(StringComparer.Ordinal);
            var fresh = all
                .Where(item => cursor is null || cursor.IsNew(item.CommitterDate, item.CommitId) || lateIds.Contains(item.CommitId))
                .OrderBy(item => item.CommitterDate)
                .ThenBy(item => item.CommitId, StringComparer.Ordinal);

            var now = time.GetUtcNow();
            foreach (var commit in fresh)
            {
                var enriched = await commits.EnrichAsync(project, repository, commit, cancellationToken).ConfigureAwait(false);
                await sink.WriteAsync("commits", Key(project, "commit", enriched.CommitId, null), enriched, cancellationToken).ConfigureAwait(false);
                result.Links += await WriteLinksAsync(project, LinkExtractor.FromCommit(enriched), cancellationToken).ConfigureAwait(false);
                result.Records++;
                // Review R1-02: CommitterDate is the developer's own clock, set by their Git client, not
                // a server timestamp. A future-dated commit must never move the checkpoint ahead of real
                // time, or every later commit (with a correct date) would be filtered out as "already
                // seen" by TimeCursor.IsNew forever. The stored record keeps the real, unclamped date.
                var checkpointAt = enriched.CommitterDate > now ? now : enriched.CommitterDate;
                written.Add((checkpointAt, enriched.CommitId));
                if (written.Count >= checkpointBatchSize && !request.Reconcile)
                {
                    cursor = TimeCursor.Advance(cursor, written);
                    await SaveCursorAsync(scope, cursor, result, cancellationToken).ConfigureAwait(false);
                    if (result.Status != StreamStatus.Synced)
                    {
                        return;
                    }

                    written.Clear();
                }
            }

            registry.Register(SourceKind.AzureDevOpsRepos, "commits", CapabilityStatus.Supported);
        }
        catch (AdoSourceException error)
        {
            Failed(result, error.Message);
        }
        catch (CheckpointStoreException error)
        {
            Failed(result, error.Message);
            return;
        }

        if (result.Status == StreamStatus.Synced)
        {
            await SaveCursorAsync(scope, TimeCursor.Advance(cursor, written), result, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SyncBuildsAsync(string project, SyncRequest request, SyncReport report, CancellationToken cancellationToken)
    {
        var scope = project + "/builds";
        var result = report.Add(SyncKind.Builds, scope);
        TimeCursor? cursor = null;
        var written = new List<(DateTimeOffset At, string Id)>();
        const int checkpointBatchSize = 25;
        try
        {
            cursor = await LoadCursorAsync(scope, request, cancellationToken).ConfigureAwait(false);
            var all = await builds.GetBuildsAsync(project, cursor?.At ?? request.Since, client.Options.PipelineIds, cancellationToken).ConfigureAwait(false);
            var fresh = all
                .Where(item => item.FinishTime is { } finished && (cursor is null || cursor.IsNew(finished, Text(item.Id))))
                .OrderBy(item => item.FinishTime)
                .ThenBy(item => item.Id);

            foreach (var build in fresh)
            {
                var enriched = await builds.EnrichAsync(project, build, cancellationToken).ConfigureAwait(false);
                await sink.WriteAsync("builds", Key(project, "build", Text(enriched.Id), null), enriched, cancellationToken).ConfigureAwait(false);
                result.Links += await WriteLinksAsync(project, LinkExtractor.FromBuild(enriched), cancellationToken).ConfigureAwait(false);
                result.Records++;
                written.Add((enriched.FinishTime!.Value, Text(enriched.Id)));
                if (written.Count >= checkpointBatchSize && !request.Reconcile)
                {
                    cursor = TimeCursor.Advance(cursor, written);
                    await SaveCursorAsync(scope, cursor, result, cancellationToken).ConfigureAwait(false);
                    if (result.Status != StreamStatus.Synced)
                    {
                        return;
                    }

                    written.Clear();
                }
            }

            registry.Register(SourceKind.AzureDevOpsPipelines, "builds", CapabilityStatus.Supported);
        }
        catch (AdoSourceException error)
        {
            Failed(result, error.Message);
        }
        catch (CheckpointStoreException error)
        {
            Failed(result, error.Message);
            return;
        }

        if (result.Status == StreamStatus.Synced)
        {
            await SaveCursorAsync(scope, TimeCursor.Advance(cursor, written), result, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<int> WriteLinksAsync(string project, IReadOnlyList<SourceLink> links, CancellationToken cancellationToken)
    {
        foreach (var link in links)
        {
            await sink.WriteAsync("links", Key(project, "link", link.Key, null), link, cancellationToken).ConfigureAwait(false);
        }

        return links.Count;
    }

    private async Task<TimeCursor?> LoadCursorAsync(string scope, SyncRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var checkpoint = await checkpoints.GetAsync(SourceId, scope, cancellationToken).ConfigureAwait(false);
            var stored = TimeCursor.Parse(checkpoint?.Cursor);
            if (request.IgnoreCheckpoints)
            {
                // --full re-reads from INGESTION_SINCE, but the stored checkpoint is kept: saving this run's
                // (older) progress over it would be refused by the store's monotonic guard and fail the stream
                // after the first batch. SaveCursorAsync only saves once this run has caught up with it.
                storedCheckpointAt[scope] = stored?.At;
                if (log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                {
                    var from = request.Since?.ToString("O", CultureInfo.InvariantCulture) ?? "all history";
                    Logging.IngestionLog.Checkpoint(log, scope, "ignored for this run", from);
                }

                return null;
            }

            Logging.IngestionLog.Checkpoint(log, scope, "loaded", checkpoint?.Cursor ?? "none (first run)");
            return stored;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new CheckpointStoreException(
                "Checkpoint store unavailable: " + error.Message + " (for the database store, run: python -m db.manage migrate).",
                error);
        }
    }

    private bool reconciling;
    private bool ignoringCheckpoints;

    /// <summary>For a --full run: the timestamp of each scope's stored checkpoint, which this run must not regress.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset?> storedCheckpointAt = new(StringComparer.Ordinal);
    private readonly ISyncQuarantine quarantine;
    private readonly Microsoft.Extensions.Logging.ILogger log;

    /// <summary>Saves the checkpoint; false (and the stream marked failed) when it could not be saved.</summary>
    private async Task<bool> SaveCursorAsync(string scope, TimeCursor? cursor, StreamResult result, CancellationToken cancellationToken)
    {
        if (cursor is null || reconciling)
        {
            return true; // a reconciliation sweep never moves the delta checkpoint (LRN-05)
        }

        if (ignoringCheckpoints &&
            storedCheckpointAt.TryGetValue(scope, out var storedAt) &&
            storedAt is { } stored &&
            cursor.At < stored)
        {
            // A --full run behind the stored checkpoint: everything up to here is written, and the stored
            // checkpoint already covers it. Keep it; this is not a failure.
            LogCheckpointKept(log, scope, cursor.At.ToString("O", CultureInfo.InvariantCulture),
                stored.ToString("O", CultureInfo.InvariantCulture), null);
            return true;
        }

        try
        {
            // Review R1-04: everything written for this stream must be durable before the checkpoint
            // moves past it, or a crash between the two loses records the next run will skip.
            await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
            await checkpoints.SaveAsync(new SyncCheckpoint(SourceId, scope, cursor.Format(), time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            LogCheckpointSaved(log, scope, null);
            if (log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
            {
                var saved = cursor.Format();
                Logging.IngestionLog.Checkpoint(log, scope, "saved", saved);
            }

            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Failed(result, "Records were written but the checkpoint could not be saved: " + error.Message);
            return false;
        }
    }

    private string Key(string project, string kind, string externalId, string? revision) =>
        client.Options.Tenant + "/ado/" + project + "/" + kind + "/" + externalId + (revision is null ? string.Empty : "@" + revision);

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Stamp(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// B-4: one stream's unexpected failure (sink, database, archive, parsing) must not stop the other
    /// streams. The failed stream is reported and its checkpoint is not advanced; caller cancellation
    /// still stops the run.
    /// </summary>
    private static async Task IsolateAsync(SyncReport report, SyncKind kind, string fallbackScope, Func<Task> work, CancellationToken cancellationToken)
    {
        var before = report.Streams.Count;
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var result = report.Streams.Count > before ? report.Streams[^1] : report.Add(kind, fallbackScope);
            Failed(result, $"Unexpected {error.GetType().Name}: {error.Message} Checkpoint not advanced.");
        }
    }

    private static void Partial(StreamResult result, string reason)
    {
        if (result.Status == StreamStatus.Synced)
        {
            result.Status = StreamStatus.Partial;
        }

        result.Reason = result.Reason is null ? reason : result.Reason + " " + reason;
    }

    private static void Failed(StreamResult result, string reason)
    {
        result.Status = result.Records > 0 ? StreamStatus.Partial : StreamStatus.NotSynced;
        result.Reason = result.Reason is null ? reason : result.Reason + " " + reason;
    }

    private static void NotSynced(StreamResult result, string reason)
    {
        result.Status = StreamStatus.NotSynced;
        result.Reason = reason;
    }
}
