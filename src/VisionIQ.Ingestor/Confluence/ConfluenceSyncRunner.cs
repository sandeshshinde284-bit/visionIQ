using System.Globalization;
using VisionIQ.Ingestor.Checkpoints;
using VisionIQ.Ingestor.Sources;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Confluence;

/// <summary>
/// Reads the approved spaces (LRN-02). Permissions are re-read for every page on every run, because a restriction
/// can change without a new page version. Page content is written once per version, so older versions are kept.
/// </summary>
public sealed class ConfluenceSyncRunner
{
    private readonly IConfluenceSource source;
    private readonly ICheckpointStore checkpoints;
    private readonly IRecordSink sink;
    private readonly SourceCapabilityRegistry registry;
    private readonly TimeProvider time;

    public ConfluenceSyncRunner(IConfluenceSource source, ICheckpointStore checkpoints, IRecordSink sink, SourceCapabilityRegistry registry, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(registry);
        this.source = source;
        this.checkpoints = checkpoints;
        this.sink = sink;
        this.registry = registry;
        this.time = time ?? TimeProvider.System;
    }

    private string SourceId => source.Tenant + "/confluence";

    public async Task<SyncReport> RunAsync(SyncRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var report = new SyncReport { Tenant = source.Tenant, StartedAt = time.GetUtcNow() };
        if (!request.Kinds.Contains(SyncKind.Confluence))
        {
            report.FinishedAt = time.GetUtcNow();
            return report;
        }

        foreach (var space in source.SpaceKeys)
        {
            await SyncSpaceAsync(space, request, report.Add(SyncKind.Confluence, space + "/pages"), cancellationToken).ConfigureAwait(false);
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
        try
        {
            var pages = await source.GetPagesAsync(space, cancellationToken).ConfigureAwait(false);
            var acls = ConfluenceAccess.ComputeAcls(pages);
            var byId = pages.ToDictionary(page => page.Id, StringComparer.Ordinal);

            foreach (var page in pages)
            {
                var acl = acls[page.Id];
                await sink.WriteAsync("confluence-acl", Key(space, "page", page.Id, null) + "#" + acl.Fingerprint, acl, cancellationToken).ConfigureAwait(false);
            }

            var hidden = acls.Values.Count(acl => !acl.Complete);
            if (hidden > 0)
            {
                result.Notes.Add(hidden.ToString(CultureInfo.InvariantCulture) + " page(s) have unclear permissions and are hidden from everyone.");
            }

            var fresh = pages
                .Where(page => cursor is null
                    ? request.Since is null || page.LastModified >= request.Since
                    : cursor.IsNew(page.LastModified, page.Id + "@" + page.Version.ToString(CultureInfo.InvariantCulture)))
                .OrderBy(page => page.LastModified)
                .ThenBy(page => page.Id, StringComparer.Ordinal);

            var withheld = 0;
            foreach (var page in fresh)
            {
                var pageAcl = acls[page.Id];
                // JSONL is not an ACL-aware evidence store. Never write
                // restricted or unverified page bodies into its files or
                // downstream search/briefs. ACL fingerprints remain available.
                // Persist these bodies only after LRN-03 can enforce the
                // Confluence ancestor-level AND policy.
                if (!pageAcl.Complete || pageAcl.IsRestricted)
                {
                    withheld++;
                    continue;
                }

                var enriched = await source.EnrichAsync(page, cancellationToken).ConfigureAwait(false);
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
                    Acl = acls[page.Id],
                };

                var version = page.Version.ToString(CultureInfo.InvariantCulture);
                await sink.WriteAsync("confluence-pages", Key(space, "page", page.Id, version), snapshot, cancellationToken).ConfigureAwait(false);
                if (DesignDecisionDetector.Detect(snapshot) is { } decision)
                {
                    await sink.WriteAsync("design-decisions", Key(space, "decision", page.Id, version), decision, cancellationToken).ConfigureAwait(false);
                    result.Links++;
                }

                result.Records++;
                written.Add((page.LastModified, page.Id + "@" + version));
            }

            if (withheld > 0)
            {
                Fail(result, $"{withheld} page(s) withheld from JSONL export due to restricted or unknown access. Secure LRN-03 ingestion is required.");
            }

            registry.Register(SourceKind.Confluence, "pages", CapabilityStatus.Supported);
            registry.Register(SourceKind.Confluence, "page-restrictions", CapabilityStatus.Supported);
        }
        catch (ConfluenceSourceException error)
        {
            Fail(result, error.Message);
        }

        // A withheld page could become public without a content revision.
        // Retain the cursor so the next scan can safely reevaluate every page.
        if (result.Status == StreamStatus.Synced && TimeCursor.Advance(cursor, written) is { } next)
        {
            try
            {
                await checkpoints.SaveAsync(new SyncCheckpoint(SourceId, scope, next.Format(), time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Fail(result, "Pages were written but the checkpoint could not be saved: " + error.Message);
            }
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
    public static IConfluenceSource Create(HttpClient http, ConfluenceOptions options, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var client = new ConfluenceHttp(http, options, delay);
        return options.Deployment == ConfluenceDeployment.Cloud
            ? new ConfluenceCloudSource(client)
            : new ConfluenceDataCenterSource(client);
    }
}
