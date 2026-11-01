using System.Globalization;
using VisionIQ.Api.Features.Evidence;
using VisionIQ.Api.Features.Lessons;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Cli;

/// <summary>
/// LRN-10: after the evidence is stored, records which files each fix CHANGED (PR cumulative changes, commit
/// changes) so lessons compare real code overlap. A PR whose changes could not be read records nothing (unknown
/// is never stored as "no files"). Used by both the sync and the webhook chains.
/// </summary>
public sealed class FixChangeSetProjectionSink(
    ICanonicalEvidenceSink evidence, IFixChangeSetStore changeSets, Uri adoOrganization, TimeProvider? timeProvider = null)
    : ICanonicalEvidenceSink
{
    private readonly string sourceInstance = adoOrganization.AbsoluteUri.TrimEnd('/');
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async Task WriteAsync(string stream, string key, object record, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        ArgumentNullException.ThrowIfNull(record);
        await evidence.WriteAsync(stream, key, record, cancellationToken).ConfigureAwait(false);

        FixChangeSet? changeSet = record switch
        {
            PullRequestRecord { ChangedFiles: { Count: > 0 } files } pr when stream == "pull-requests" => new FixChangeSet(
                sourceInstance, pr.Project, pr.RepositoryId, "pull_request", pr.Id.ToString(CultureInfo.InvariantCulture),
                files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), false,
                new EvidenceIdentity("azure_devops", sourceInstance, pr.Project, pr.RepositoryId, "pull_request",
                    pr.Id.ToString(CultureInfo.InvariantCulture)).ItemKey(), time.GetUtcNow()),
            CommitRecord { ChangedFiles.Count: > 0 } commit when stream == "commits" => new FixChangeSet(
                sourceInstance, commit.Project, commit.RepositoryId, "commit", commit.CommitId.ToLowerInvariant(),
                commit.ChangedFiles.Select(file => file.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                commit.ChangedFilesTruncated,
                new EvidenceIdentity("azure_devops", sourceInstance, commit.Project, commit.RepositoryId, "commit", commit.CommitId).ItemKey(),
                time.GetUtcNow()),
            _ => null,
        };
        if (changeSet is not null)
        {
            await changeSets.UpsertAsync(changeSet, cancellationToken).ConfigureAwait(false);
        }
    }
}
