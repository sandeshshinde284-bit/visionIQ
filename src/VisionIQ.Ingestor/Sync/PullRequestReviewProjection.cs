using System.Globalization;
using VisionIQ.Ingestor.Ado;

namespace VisionIQ.Ingestor.Sync;

/// <summary>
/// S05-05 / LRN-12: one canonical, versioned review record per ADO reviewer.
/// Both periodic sync and PR service hooks must call this exact projection so
/// a webhook replay and a subsequent reconciliation write identical identities
/// and content. These are observed vote facts, never verified root causes.
/// </summary>
public static class PullRequestReviewProjection
{
    public static async Task WriteAsync(
        IRecordSink sink, string tenant, PullRequestRecord pullRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentNullException.ThrowIfNull(pullRequest);

        var prefix = tenant + "/ado/" + pullRequest.Project + "/pull-request-review/";
        var prId = pullRequest.Id.ToString(CultureInfo.InvariantCulture);
        foreach (var reviewer in pullRequest.Reviewers)
        {
            // The native identity ID is the source of truth. Do not persist names or email.
            // A malformed reviewer ID must never create an invalid canonical record.
            if (string.IsNullOrWhiteSpace(reviewer.Id))
            {
                throw new InvalidOperationException("ADO reviewer identity is missing.");
            }

            var review = new PullRequestReviewRecord(
                reviewer.Id,
                pullRequest.Id,
                pullRequest.RepositoryId,
                pullRequest.RepositoryName,
                pullRequest.Project,
                reviewer.Vote,
                reviewer.IsRequired,
                reviewer.HasDeclined,
                pullRequest.LastActivity);

            var reviewExternalId = prId + ":" + reviewer.Id;
            var reviewVersion = review.Status + "@" + review.Vote.ToString(CultureInfo.InvariantCulture);
            await sink.WriteAsync(
                "pull-request-reviews",
                prefix + reviewExternalId + "@" + reviewVersion,
                review,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
