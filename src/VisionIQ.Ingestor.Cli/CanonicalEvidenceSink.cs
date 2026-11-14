using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using VisionIQ.Api.Features.Evidence;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Confluence;
using VisionIQ.Ingestor.Links;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Cli;

/// <summary>
/// Trusted LRN-01/LRN-02 -> LRN-03 handoff. Archives the original source record
/// before the canonical revision write. Access is deliberately UNVERIFIED until
/// the provider's current effective ACL is mapped and enforced by a reader.
/// </summary>
public sealed class CanonicalEvidenceSink(
    EvidenceIngestionService service,
    Uri organizationUrl,
    string organizationId,
    Uri? confluenceSite = null,
    TimeProvider? timeProvider = null) : ICanonicalEvidenceSink
{
    private static readonly JsonSerializerOptions Serialization = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// B-9: for kinds whose native revision does not cover every stored field (an active PR's commits,
    /// a page's labels and permissions, build artifacts, a renamed repository), the revision is
    /// "native#content-hash". Identical content stays idempotent; changed content becomes a new
    /// revision instead of an immutable-content conflict that failed every re-sync.
    /// </summary>
    public static string ContentRevision(string nativeRevision, object record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeRevision);
        ArgumentNullException.ThrowIfNull(record);
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(record, record.GetType(), Serialization));
        return nativeRevision + "#" + Convert.ToHexStringLower(hash)[..16];
    }
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    /// <summary>N-2: where deletions are recorded; without it deletion records are ignored.</summary>
    public IEvidenceTombstones? Tombstones { get; init; }

    public async Task WriteAsync(
        string stream,
        string key,
        object record,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(record);

        if (stream == "sync-quarantine")
        {
            return; // G-1: operational record, persisted by the quarantine store; not source evidence
        }

        if (stream == "work-item-deletions" && record is WorkItemDeletion deletion)
        {
            // N-2: hide both the current item and its revision history.
            if (Tombstones is { } tombstones)
            {
                var id = deletion.Id.ToString(CultureInfo.InvariantCulture);
                foreach (var kind in new[] { "work_item_current", "work_item" })
                {
                    await tombstones.TombstoneAsync(
                        new EvidenceIdentity("azure_devops", organizationUrl.AbsoluteUri.TrimEnd('/'), deletion.Project, null, kind, id).ItemKey(),
                        deletion.ObservedAt, deletion.Reason, cancellationToken).ConfigureAwait(false);
                }
            }

            return;
        }

        var observed = time.GetUtcNow();
        var datum = Describe(stream, key, record, observed);
        if (datum is null)
        {
            throw new NotSupportedException(
                $"No reviewed canonical evidence mapping exists for stream '{stream}'.");
        }

        var raw = JsonSerializer.SerializeToUtf8Bytes(record, record.GetType(), Serialization);
        // B-5: ADO evidence is readable at project level; the stored principal names the project and
        // AdoProjectAccessVerifier re-checks live membership on every read. Confluence's
        // ancestor-level AND restrictions cannot be flattened into principals, so Confluence
        // evidence stays unverified (hidden) until a Confluence-specific read path exists.
        var access = datum.Provider == "azure_devops"
            ? new EvidenceAccess(true, [AdoAccessPrincipals.Project(datum.Instance, datum.Project)])
            : datum.Provider == "confluence" && datum.Kind is "design_page"
                // LRN-02: page evidence names its space; Confluence's live page permission check decides.
                ? new EvidenceAccess(true, [ConfluenceAccessPrincipals.Space(datum.Instance, datum.Project)])
                : new EvidenceAccess(false, Array.Empty<string>());
        var envelope = new EvidenceEnvelope(
            new EvidenceIdentity(
                datum.Provider, datum.Instance, datum.Project, datum.Repository,
                datum.Kind, datum.Id),
            datum.Revision,
            datum.SourceTimestamp,
            observed,
            datum.Url,
            "visioniq-source-connector",
            raw,
            access);

        var result = await service.ImportAsync(envelope, cancellationToken)
            .ConfigureAwait(false);
        if (result.Status == EvidenceImportStatus.Conflict)
        {
            throw new InvalidOperationException(
                "Canonical evidence conflict: one native source revision (for example an ADO work-item revision) had different content.");
        }

        // Review R1-03: a work item moved to another ADO project never matches its old project's WIQL
        // filter again, so its old project's copy is never otherwise revisited. The moment it is
        // observed under its new project, the old project's copy is tombstoned the same way an actual
        // deletion is (N-2): hidden, never silently left stale and still readable by the old project's
        // members.
        if (datum.Kind == "work_item_current" && Tombstones is { } movedItemTombstones)
        {
            foreach (var kind in new[] { "work_item_current", "work_item" })
            {
                var stale = await movedItemTombstones.FindOtherProjectItemKeysAsync(
                    datum.Provider, datum.Instance, kind, datum.Id, datum.Project, cancellationToken).ConfigureAwait(false);
                foreach (var staleKey in stale)
                {
                    await movedItemTombstones.TombstoneAsync(staleKey, observed, "missing_in_source", cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private sealed record Datum(
        string Provider,
        string Instance,
        string Project,
        string? Repository,
        string Kind,
        string Id,
        string Revision,
        DateTimeOffset SourceTimestamp,
        string Url);

    private Datum? Describe(string stream, string key, object record, DateTimeOffset observed)
    {
        var adoInstance = organizationUrl.AbsoluteUri.TrimEnd('/');
        var confluenceInstance = confluenceSite?.AbsoluteUri.TrimEnd('/');

        string AdoUrl(string project, string tail) =>
            adoInstance + "/" + Uri.EscapeDataString(project) + "/" + tail;

        string WikiUrl(string tenant, string? candidate) =>
            Uri.TryCreate(candidate, UriKind.Absolute, out var valid)
                && valid.Scheme == Uri.UriSchemeHttps
                    ? valid.AbsoluteUri
                    : confluenceInstance ?? "https://" + tenant + "/";

        static string Id(int n) => n.ToString(CultureInfo.InvariantCulture);
        static string Stamp(DateTimeOffset timestamp) =>
            timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        switch (record)
        {
            case WorkItemRecord item when stream == "work-items":
                return new Datum("azure_devops", adoInstance, item.Project, null,
                    "work_item_current", Id(item.Id), ContentRevision(Id(item.Revision), item), item.ChangedDate,
                    AdoUrl(item.Project, "_workitems/edit/" + Id(item.Id)));

            case WorkItemRevisionRecord revision when stream == "work-item-revisions":
                {
                    var project = AdoProjectFromKey(key, "/work-item/");
                    return new Datum("azure_devops", adoInstance, project, null,
                        "work_item", Id(revision.Id), Id(revision.Revision), revision.ChangedDate,
                        AdoUrl(project, "_workitems/edit/" + Id(revision.Id)));
                }

            case PullRequestRecord pr when stream == "pull-requests":
                return new Datum("azure_devops", adoInstance, pr.Project, pr.RepositoryId,
                    "pull_request", Id(pr.Id), ContentRevision(pr.Status + "@" + Stamp(pr.LastActivity), pr),
                    pr.LastActivity,
                    AdoUrl(pr.Project, "_git/" + Uri.EscapeDataString(pr.RepositoryName)
                        + "/pullrequest/" + Id(pr.Id)));

            case PullRequestReviewRecord review when stream == "pull-request-reviews":
                return new Datum("azure_devops", adoInstance, review.Project, review.RepositoryId,
                    "review", Id(review.PullRequestId) + ":" + review.ReviewerId,
                    ContentRevision(review.Status + "@" + Id(review.Vote), review), review.Observed,
                    AdoUrl(review.Project, "_git/" + Uri.EscapeDataString(review.RepositoryName)
                        + "/pullrequest/" + Id(review.PullRequestId)));

            case CommitRecord commit when stream == "commits":
                return new Datum("azure_devops", adoInstance, commit.Project,
                    commit.RepositoryId, "commit", commit.CommitId, ContentRevision(commit.CommitId, commit),
                    commit.CommitterDate,
                    AdoUrl(commit.Project, "_git/"
                        + Uri.EscapeDataString(commit.RepositoryName)
                        + "/commit/" + Uri.EscapeDataString(commit.CommitId)));

            case BuildRecord build when stream == "builds":
                return new Datum("azure_devops", adoInstance, build.Project,
                    build.RepositoryId, "ci_run", Id(build.Id),
                    ContentRevision(build.Status + "@" + Stamp(build.FinishTime ?? observed), build),
                    build.FinishTime ?? observed,
                    AdoUrl(build.Project, "_build/results?buildId=" + Id(build.Id)));

            case SourceLink link when stream == "links":
                {
                    var project = AdoProjectFromKey(key, "/link/");
                    return new Datum("azure_devops", adoInstance, project, null,
                        // m-10: the source record's time, not the ingestion time (fallback only when absent).
                        "declared_link", link.Key, ContentRevision("1", link), link.SourceTimestamp ?? observed,
                        AdoUrl(project, "_workitems"));
                }

            case ConfluencePageSnapshot page when stream == "confluence-pages":
                return new Datum("confluence", confluenceInstance ?? page.Tenant,
                    page.SpaceKey, null, "design_page", page.Id,
                    ContentRevision(Id(page.Version), page), page.LastModified, WikiUrl(page.Tenant, page.Url));

            default:
                return null;
        }
    }

    private string AdoProjectFromKey(string key, string marker)
    {
        var prefix = organizationId + "/ado/";
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Source identity is not in the expected ADO tenant.", nameof(key));
        }

        var position = key.IndexOf(marker, prefix.Length, StringComparison.Ordinal);
        if (position <= prefix.Length)
        {
            throw new ArgumentException("Source identity lacks a project scope.", nameof(key));
        }

        return key[prefix.Length..position];
    }

    private static string SiteFromKey(string key)
    {
        var index = key.IndexOf("/confluence/", StringComparison.Ordinal);
        if (index <= 0)
        {
            throw new ArgumentException("Source identity lacks a Confluence site.", nameof(key));
        }

        return key[..index];
    }
}
