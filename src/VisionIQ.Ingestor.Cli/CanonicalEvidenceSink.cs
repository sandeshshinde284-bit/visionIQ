using System.Globalization;
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
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async Task WriteAsync(
        string stream,
        string key,
        object record,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(record);

        var observed = time.GetUtcNow();
        var datum = Describe(stream, key, record, observed);
        if (datum is null)
        {
            throw new NotSupportedException(
                $"No reviewed canonical evidence mapping exists for stream '{stream}'.");
        }

        var raw = JsonSerializer.SerializeToUtf8Bytes(record, record.GetType(), Serialization);
        // ADO source access semantics and Confluence ancestor-level AND ACL
        // cannot be represented by a flat principal list yet. Never invent
        // public access, even when the ingesting identity can read the source.
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
            new EvidenceAccess(false, Array.Empty<string>()));

        var result = await service.ImportAsync(envelope, cancellationToken)
            .ConfigureAwait(false);
        if (result.Status == EvidenceImportStatus.Conflict)
        {
            throw new InvalidOperationException(
                "Canonical evidence conflict: one source revision had different content.");
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
                    "work_item_current", Id(item.Id), Id(item.Revision), item.ChangedDate,
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
                    "pull_request", Id(pr.Id), pr.Status + "@" + Stamp(pr.LastActivity),
                    pr.LastActivity,
                    AdoUrl(pr.Project, "_git/" + Uri.EscapeDataString(pr.RepositoryName)
                        + "/pullrequest/" + Id(pr.Id)));

            case CommitRecord commit when stream == "commits":
                return new Datum("azure_devops", adoInstance, commit.Project,
                    commit.RepositoryId, "commit", commit.CommitId, commit.CommitId,
                    commit.CommitterDate,
                    AdoUrl(commit.Project, "_git/"
                        + Uri.EscapeDataString(commit.RepositoryName)
                        + "/commit/" + Uri.EscapeDataString(commit.CommitId)));

            case BuildRecord build when stream == "builds":
                return new Datum("azure_devops", adoInstance, build.Project,
                    build.RepositoryId, "ci_run", Id(build.Id),
                    build.Status + "@" + Stamp(build.FinishTime ?? observed),
                    build.FinishTime ?? observed,
                    AdoUrl(build.Project, "_build/results?buildId=" + Id(build.Id)));

            case SourceLink link when stream == "links":
            {
                var project = AdoProjectFromKey(key, "/link/");
                return new Datum("azure_devops", adoInstance, project, null,
                    "declared_link", link.Key, "1", observed,
                    AdoUrl(project, "_workitems"));
            }

            case ConfluencePageSnapshot page when stream == "confluence-pages":
                return new Datum("confluence", confluenceInstance ?? page.Tenant,
                    page.SpaceKey, null, "design_page", page.Id,
                    Id(page.Version), page.LastModified, WikiUrl(page.Tenant, page.Url));

            case DesignDecisionRecord decision when stream == "design-decisions":
                return new Datum("confluence", confluenceInstance ?? SiteFromKey(key),
                    decision.SpaceKey, null, "design_decision", decision.PageId,
                    Id(decision.Version), decision.LastModified,
                    WikiUrl(SiteFromKey(key), decision.Url));

            case ConfluencePageAcl acl when stream == "confluence-acl":
                // Confluence does not provide an update timestamp for every
                // permission change. Observation time is the only available
                // timestamp for this separate permission snapshot.
                return new Datum("confluence", confluenceInstance ?? SiteFromKey(key),
                    acl.SpaceKey, null, "confluence_acl", acl.PageId,
                    acl.Fingerprint, observed, WikiUrl(SiteFromKey(key), null));

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
