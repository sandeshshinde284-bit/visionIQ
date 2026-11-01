using System.Globalization;
using System.Text.RegularExpressions;
using VisionIQ.Api.Features.BuildLinks;
using VisionIQ.Api.Features.Evidence;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Cli;

/// <summary>
/// LRN-07: after a build is stored as evidence, records which commit it built (full SHA only) and which pull
/// request triggered it. Builds without a repository or without either reference record nothing.
/// </summary>
public sealed partial class BuildLinkProjectionSink(
    ICanonicalEvidenceSink evidence,
    IBuildLinkStore buildLinks,
    Uri adoOrganization) : ICanonicalEvidenceSink
{
    private readonly string sourceInstance = adoOrganization.AbsoluteUri.TrimEnd('/');

    public async Task WriteAsync(string stream, string key, object record, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        ArgumentNullException.ThrowIfNull(record);

        await evidence.WriteAsync(stream, key, record, cancellationToken).ConfigureAwait(false);
        if (stream == "builds" && record is BuildRecord build && Link(sourceInstance, build) is { } link)
        {
            await buildLinks.UpsertAsync(link, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The build's link, citing the exact stored build evidence revision; null when it states none. Only
    /// finished builds are linked: an in-progress build's evidence revision includes the ingestion time, which
    /// cannot be cited exactly, so its link is recorded once it finishes.
    /// </summary>
    public static BuildLink? Link(string sourceInstance, BuildRecord build)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (build.FinishTime is not { } finished)
        {
            return null;
        }

        var sha = build.SourceVersion is { } version && FullSha().IsMatch(version) ? version.ToLowerInvariant() : null;
        var pullRequest = build.PullRequestNumber?.ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(build.RepositoryId) || (sha is null && pullRequest is null))
        {
            return null;
        }

        var id = build.Id.ToString(CultureInfo.InvariantCulture);
        var itemKey = new EvidenceIdentity("azure_devops", sourceInstance, build.Project, build.RepositoryId, "ci_run", id).ItemKey();
        var revision = CanonicalEvidenceSink.ContentRevision(build.Status + "@" + finished.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), build);
        return new BuildLink(sourceInstance, build.Project, id, build.RepositoryId, sha, pullRequest, "evidence:" + itemKey + ":" + revision, finished);
    }

    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    private static partial Regex FullSha();
}
