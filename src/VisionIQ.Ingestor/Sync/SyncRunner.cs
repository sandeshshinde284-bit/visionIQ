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

    public SyncRunner(AdoClient client, ICheckpointStore checkpoints, IRecordSink sink, SourceCapabilityRegistry registry, TimeProvider? time = null)
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
        workItems = new AdoWorkItemSource(client);
        repositories = new AdoRepositorySource(client);
        commits = new AdoCommitSource(client);
        builds = new AdoBuildSource(client);
    }

    private string SourceId => client.Options.Tenant + "/ado";

    public async Task<SyncReport> RunAsync(SyncRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var report = new SyncReport { Tenant = client.Options.Tenant, StartedAt = time.GetUtcNow() };

        foreach (var project in client.Options.Projects)
        {
            if (request.Kinds.Contains(SyncKind.WorkItems))
            {
                await SyncWorkItemsAsync(project, request, report, cancellationToken).ConfigureAwait(false);
            }

            var wantsPullRequests = request.Kinds.Contains(SyncKind.PullRequests);
            var wantsCommits = request.Kinds.Contains(SyncKind.Commits);
            if (wantsPullRequests || wantsCommits)
            {
                foreach (var repository in await ResolveRepositoriesAsync(project, wantsPullRequests, wantsCommits, report, cancellationToken).ConfigureAwait(false))
                {
                    if (wantsPullRequests)
                    {
                        await SyncPullRequestsAsync(project, repository, request, report, cancellationToken).ConfigureAwait(false);
                    }

                    if (wantsCommits)
                    {
                        await SyncCommitsAsync(project, repository, request, report, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (request.Kinds.Contains(SyncKind.Builds))
            {
                await SyncBuildsAsync(project, request, report, cancellationToken).ConfigureAwait(false);
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
        var scope = project + "/work-items";
        var result = report.Add(SyncKind.WorkItems, scope);
        TimeCursor? cursor = null;
        var written = new List<(DateTimeOffset At, string Id)>();
        try
        {
            cursor = await LoadCursorAsync(scope, request, cancellationToken).ConfigureAwait(false);
            var ids = await workItems.QueryIdsAsync(project, cursor?.At ?? request.Since, cancellationToken).ConfigureAwait(false);
            var outcomes = await workItems.GetWorkItemsAsync(project, ids, cancellationToken).ConfigureAwait(false);

            var fresh = new List<WorkItemRecord>();
            foreach (var outcome in outcomes)
            {
                if (outcome.Record is null)
                {
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

                result.Links += await WriteLinksAsync(LinkExtractor.FromWorkItem(record), cancellationToken).ConfigureAwait(false);
                result.Records++;
                written.Add((record.ChangedDate, Text(record.Id)));
            }

            if (ids.Count >= AdoWorkItemSource.WiqlLimit)
            {
                Partial(result, "The query returned the 20,000-item maximum; run sync again to continue.");
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

        await SaveCursorAsync(scope, TimeCursor.Advance(cursor, written), result, cancellationToken).ConfigureAwait(false);
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

            // A pull request appears when it is created, and again (as a new version) when it is closed.
            var fresh = all
                .Where(item => cursor is null
                    ? request.Since is null || item.LastActivity >= request.Since
                    : cursor.IsNew(item.LastActivity, Text(item.Id)))
                .OrderBy(item => item.LastActivity)
                .ThenBy(item => item.Id);

            foreach (var pullRequest in fresh)
            {
                var enriched = await repositories.EnrichAsync(project, repository, pullRequest, cancellationToken).ConfigureAwait(false);
                var version = enriched.Status + "@" + Stamp(enriched.LastActivity);
                await sink.WriteAsync("pull-requests", Key(project, "pull-request", Text(enriched.Id), version), enriched, cancellationToken).ConfigureAwait(false);
                result.Links += await WriteLinksAsync(LinkExtractor.FromPullRequest(enriched), cancellationToken).ConfigureAwait(false);
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

            foreach (var commit in fresh)
            {
                var enriched = await commits.EnrichAsync(project, repository, commit, cancellationToken).ConfigureAwait(false);
                await sink.WriteAsync("commits", Key(project, "commit", enriched.CommitId, null), enriched, cancellationToken).ConfigureAwait(false);
                result.Links += await WriteLinksAsync(LinkExtractor.FromCommit(enriched), cancellationToken).ConfigureAwait(false);
                result.Records++;
                written.Add((enriched.CommitterDate, enriched.CommitId));
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
                result.Links += await WriteLinksAsync(LinkExtractor.FromBuild(enriched), cancellationToken).ConfigureAwait(false);
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

    private async Task<int> WriteLinksAsync(IReadOnlyList<SourceLink> links, CancellationToken cancellationToken)
    {
        foreach (var link in links)
        {
            await sink.WriteAsync("links", link.Key, link, cancellationToken).ConfigureAwait(false);
        }

        return links.Count;
    }

    private async Task<TimeCursor?> LoadCursorAsync(string scope, SyncRequest request, CancellationToken cancellationToken)
    {
        if (request.IgnoreCheckpoints)
        {
            return null;
        }

        try
        {
            var checkpoint = await checkpoints.GetAsync(SourceId, scope, cancellationToken).ConfigureAwait(false);
            return TimeCursor.Parse(checkpoint?.Cursor);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new CheckpointStoreException(
                "Checkpoint store unavailable: " + error.Message + " (for the database store, run: python -m db.manage migrate).",
                error);
        }
    }

    private async Task SaveCursorAsync(string scope, TimeCursor? cursor, StreamResult result, CancellationToken cancellationToken)
    {
        if (cursor is null)
        {
            return;
        }

        try
        {
            await checkpoints.SaveAsync(new SyncCheckpoint(SourceId, scope, cursor.Format(), time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
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
