using System.Globalization;
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
    private readonly AdoClient client;
    private readonly ICheckpointStore checkpoints;
    private readonly IRecordSink sink;
    private readonly SourceCapabilityRegistry registry;
    private readonly TimeProvider time;
    private readonly AdoWorkItemSource workItems;
    private readonly AdoRepositorySource repositories;
    private readonly AdoCommitSource commits;
    private readonly AdoBuildSource builds;

    public SyncRunner(AdoClient client, ICheckpointStore checkpoints, IRecordSink sink, SourceCapabilityRegistry registry, TimeProvider? time = null, Defects.DefectEnvironmentClassifier? environments = null, ISyncQuarantine? quarantine = null, Microsoft.Extensions.Logging.ILogger? logger = null)
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
        log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        repositories = new AdoRepositorySource(client);
        commits = new AdoCommitSource(client);
        builds = new AdoBuildSource(client);
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
            found = await repositories.GetRepositoriesAsync(project, cancellationToken).ConfigureAwait(false);
            registry.Register(SourceKind.AzureDevOpsRepos, "repositories", CapabilityStatus.Supported);
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
        TimeCursor? cursor = null;
        var written = new List<(DateTimeOffset At, string Id)>();
        var capped = false;
        try
        {
            cursor = await LoadCursorAsync(scope, request, cancellationToken).ConfigureAwait(false);
            // Review R1-01: "asOf" is the instant the WIQL query itself was evaluated at. Passing it to
            // the batch read too makes both calls agree on one snapshot, so an item changed while this
            // (possibly long) query was running is read again next run instead of silently skipped.
            var query = await workItems.QueryIdsAsync(project, cursor?.At ?? request.Since, cancellationToken).ConfigureAwait(false);
            var ids = query.Ids;
            var outcomes = await workItems.GetWorkItemsAsync(project, ids, query.AsOf, cancellationToken).ConfigureAwait(false);
            var open = await quarantine.OpenAsync(SourceId, scope, "work_item", cancellationToken).ConfigureAwait(false);

            var fresh = new List<WorkItemRecord>();
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

            foreach (var record in fresh.OrderBy(item => item.ChangedDate).ThenBy(item => item.Id))
            {
                await sink.WriteAsync("work-items", Key(project, "work-item", Text(record.Id), Text(record.Revision)), record, cancellationToken).ConfigureAwait(false);
                if (request.IncludeRevisions)
                {
                    foreach (var revision in await workItems.GetRevisionsAsync(project, record.Id, cancellationToken).ConfigureAwait(false))
                    {
                        await sink.WriteAsync("work-item-revisions", Key(project, "work-item", Text(revision.Id), Text(revision.Revision)), revision, cancellationToken).ConfigureAwait(false);
                    }
                }

                result.Links += await WriteLinksAsync(project, LinkExtractor.FromWorkItem(record), cancellationToken).ConfigureAwait(false);
                result.Records++;
                // Review R1-01 defense-in-depth: even though GetWorkItemsAsync was asked for field
                // values as of the query's own snapshot, never let the saved checkpoint move past that
                // snapshot. ChangedDate stored in the record itself is never clamped (real source data).
                var checkpointAt = query.AsOf is { } asOf && record.ChangedDate > asOf ? asOf : record.ChangedDate;
                written.Add((checkpointAt, Text(record.Id)));
            }

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

        // N-2: deleted items never appear in change queries, so read the Recycle Bin explicitly.
        try
        {
            var observed = time.GetUtcNow();
            var deleted = await workItems.GetRecycleBinIdsAsync(project, cancellationToken).ConfigureAwait(false);
            Logging.IngestionLog.DeletionsFound(log, project, deleted.Count);
            foreach (var id in deleted)
            {
                await sink.WriteAsync("work-item-deletions",
                    $"{client.Options.Tenant}/ado/{project}/work-item/{id.ToString(CultureInfo.InvariantCulture)}/deleted",
                    new WorkItemDeletion(id, project, observed, "deleted_in_source"), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (AdoSourceException error)
        {
            result.Notes.Add("Recycle Bin could not be read; deletions not checked this run: " + error.Message);
        }
    }

    private async Task SyncPullRequestsAsync(string project, RepositoryRecord repository, SyncRequest request, SyncReport report, CancellationToken cancellationToken)
    {
        var scope = project + "/" + repository.Name + "/pull-requests";
        var result = report.Add(SyncKind.PullRequests, scope);
        TimeCursor? cursor = null;
        var written = new List<(DateTimeOffset At, string Id)>();
        try
        {
            cursor = await LoadCursorAsync(scope, request, cancellationToken).ConfigureAwait(false);
            var all = await repositories.GetPullRequestsAsync(project, repository, cancellationToken).ConfigureAwait(false);

            // Review R1-06: LastActivity (ClosedDate ?? CreatedDate) never changes while a PR stays
            // active, so a new push, vote, comment or linked work item on an open PR was otherwise
            // never picked up again after its creation until it closed or a webhook happened to fire.
            // Every active PR is re-enriched and re-written on every run regardless of the cursor;
            // ContentRevision hashes the PR's full fetched content (commits, threads, work items,
            // policies), so a re-fetch of an unchanged active PR is a no-op at the evidence layer and
            // only a genuine change produces a new revision. This trades some per-run cost (one
            // re-enrich per currently-open PR) for actually-current data on open work.
            var fresh = all
                .Where(item => item.Status.Equals("active", StringComparison.OrdinalIgnoreCase)
                    || (cursor is null
                        ? request.Since is null || item.LastActivity >= request.Since
                        : cursor.IsNew(item.LastActivity, Text(item.Id))))
                .OrderBy(item => item.LastActivity)
                .ThenBy(item => item.Id);

            foreach (var pullRequest in fresh)
            {
                var enriched = await repositories.EnrichAsync(project, repository, pullRequest, cancellationToken).ConfigureAwait(false);
                var version = enriched.Status + "@" + Stamp(enriched.LastActivity);
                await sink.WriteAsync("pull-requests", Key(project, "pull-request", Text(enriched.Id), version), enriched, cancellationToken).ConfigureAwait(false);
                result.Links += await WriteLinksAsync(project, LinkExtractor.FromPullRequest(enriched), cancellationToken).ConfigureAwait(false);
                result.Records++;
                written.Add((enriched.LastActivity, Text(enriched.Id)));
            }

            registry.Register(SourceKind.AzureDevOpsRepos, "pull-requests", CapabilityStatus.Supported);
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

        await SaveCursorAsync(scope, TimeCursor.Advance(cursor, written), result, cancellationToken).ConfigureAwait(false);
    }

    private async Task SyncCommitsAsync(string project, RepositoryRecord repository, SyncRequest request, SyncReport report, CancellationToken cancellationToken)
    {
        var scope = project + "/" + repository.Name + "/commits";
        var result = report.Add(SyncKind.Commits, scope);
        TimeCursor? cursor = null;
        var written = new List<(DateTimeOffset At, string Id)>();
        try
        {
            cursor = await LoadCursorAsync(scope, request, cancellationToken).ConfigureAwait(false);
            var all = await commits.GetCommitsAsync(project, repository, cursor?.At ?? request.Since, cancellationToken).ConfigureAwait(false);
            var fresh = all
                .Where(item => cursor is null || cursor.IsNew(item.CommitterDate, item.CommitId))
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

        await SaveCursorAsync(scope, TimeCursor.Advance(cursor, written), result, cancellationToken).ConfigureAwait(false);
    }

    private async Task SyncBuildsAsync(string project, SyncRequest request, SyncReport report, CancellationToken cancellationToken)
    {
        var scope = project + "/builds";
        var result = report.Add(SyncKind.Builds, scope);
        TimeCursor? cursor = null;
        var written = new List<(DateTimeOffset At, string Id)>();
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

        await SaveCursorAsync(scope, TimeCursor.Advance(cursor, written), result, cancellationToken).ConfigureAwait(false);
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
        if (request.IgnoreCheckpoints)
        {
            if (log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
            {
                var from = request.Since?.ToString("O", CultureInfo.InvariantCulture) ?? "all history";
                Logging.IngestionLog.Checkpoint(log, scope, "ignored for this run", from);
            }
            return null;
        }

        try
        {
            var checkpoint = await checkpoints.GetAsync(SourceId, scope, cancellationToken).ConfigureAwait(false);
            Logging.IngestionLog.Checkpoint(log, scope, "loaded", checkpoint?.Cursor ?? "none (first run)");
            return TimeCursor.Parse(checkpoint?.Cursor);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new CheckpointStoreException(
                "Checkpoint store unavailable: " + error.Message + " (for the database store, run: python -m db.manage migrate).",
                error);
        }
    }

    private bool reconciling;
    private readonly ISyncQuarantine quarantine;
    private readonly Microsoft.Extensions.Logging.ILogger log;

    private async Task SaveCursorAsync(string scope, TimeCursor? cursor, StreamResult result, CancellationToken cancellationToken)
    {
        if (cursor is null || reconciling)
        {
            return; // a reconciliation sweep never moves the delta checkpoint (LRN-05)
        }

        try
        {
            // Review R1-04: everything written for this stream must be durable before the checkpoint
            // moves past it, or a crash between the two loses records the next run will skip.
            await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
            await checkpoints.SaveAsync(new SyncCheckpoint(SourceId, scope, cursor.Format(), time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            if (log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
            {
                var saved = cursor.Format();
                Logging.IngestionLog.Checkpoint(log, scope, "saved", saved);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Failed(result, "Records were written but the checkpoint could not be saved: " + error.Message);
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
