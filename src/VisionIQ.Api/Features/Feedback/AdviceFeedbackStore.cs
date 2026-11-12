using Npgsql;

namespace VisionIQ.Api.Features.Feedback;

/// <summary>What happened to the ratings of one comment.</summary>
/// <param name="Status">recorded, duplicate, or ignored.</param>
/// <param name="Reason">Why nothing was recorded: no_published_brief or unknown_advice_ids.</param>
/// <param name="Recorded">Advice ids stored by this delivery.</param>
/// <param name="Unknown">Advice ids in the comment that the ticket's brief does not contain.</param>
public sealed record FeedbackOutcome(
    string Status, string? Reason, IReadOnlyList<string> Recorded, IReadOnlyList<string> Unknown, Guid? RunId = null);

/// <summary>A comment's ratings, tied to the ticket and the engineer who wrote them.</summary>
public sealed record FeedbackSubmission(
    string TenantId,
    string ProjectId,
    string TicketId,
    string DeliveryId,
    string ReviewerId,
    DateTimeOffset RatedAt,
    IReadOnlyList<AdviceRating> Ratings);

/// <summary>S10-09: stores each advice rating against the immutable brief revision it belongs to.</summary>
public interface IAdviceFeedbackStore
{
    Task<FeedbackOutcome> RecordAsync(FeedbackSubmission submission, CancellationToken cancellationToken);
}

/// <summary>
/// Ratings go to the brief most recently published on the ticket. They are appended, never updated: a later
/// rating of the same advice is a new row and the latest one is the current disposition, so the history of what
/// the engineer decided can always be replayed. The same ADO delivery never records twice.
/// </summary>
public sealed class PostgresAdviceFeedbackStore(NpgsqlDataSource dataSource) : IAdviceFeedbackStore
{
    public async Task<FeedbackOutcome> RecordAsync(FeedbackSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        Guid runId;
        Guid resultId;
        string[] adviceIds;
        await using (var brief = new NpgsqlCommand(
            "SELECT r.run_id, res.result_id, res.advice_ids " +
            "FROM workflow.analysis_runs r " +
            "JOIN workflow.analysis_results res ON res.run_id = r.run_id " +
            "JOIN workflow.brief_publications p ON p.run_id = r.run_id AND p.status = 'published' " +
            "WHERE r.tenant_id = $1 AND r.project_id = $2 AND r.ticket_id = $3 " +
            "ORDER BY p.published_at DESC LIMIT 1",
            connection,
            transaction))
        {
            brief.Parameters.Add(new NpgsqlParameter { Value = submission.TenantId });
            brief.Parameters.Add(new NpgsqlParameter { Value = submission.ProjectId });
            brief.Parameters.Add(new NpgsqlParameter { Value = submission.TicketId });
            await using var reader = await brief.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new FeedbackOutcome("ignored", "no_published_brief", [], []);
            }

            runId = reader.GetGuid(0);
            resultId = reader.GetGuid(1);
            adviceIds = reader.GetFieldValue<string[]>(2);
        }

        var known = submission.Ratings.Where(r => adviceIds.Contains(r.AdviceId, StringComparer.Ordinal)).ToList();
        var unknown = submission.Ratings.Select(r => r.AdviceId).Except(known.Select(r => r.AdviceId), StringComparer.Ordinal).ToList();
        var recorded = new List<string>();
        foreach (var rating in known)
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO workflow.advice_feedback " +
                "(feedback_id, run_id, result_id, advice_id, disposition, note, reviewer_id, source, delivery_id, rated_at) " +
                "VALUES ($1, $2, $3, $4, $5, $6, $7, 'ado_comment', $8, $9) " +
                "ON CONFLICT (source, delivery_id, advice_id) DO NOTHING",
                connection,
                transaction);
            foreach (var value in new object?[]
            {
                Guid.NewGuid(), runId, resultId, rating.AdviceId, Name(rating.Disposition), rating.Note,
                submission.ReviewerId, submission.DeliveryId, submission.RatedAt.ToUniversalTime(),
            })
            {
                insert.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
            }

            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                recorded.Add(rating.AdviceId);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        if (recorded.Count > 0)
        {
            return new FeedbackOutcome("recorded", null, recorded, unknown, runId);
        }

        return known.Count > 0
            ? new FeedbackOutcome("duplicate", null, [], unknown, runId)
            : new FeedbackOutcome("ignored", "unknown_advice_ids", [], unknown, runId);
    }

    /// <summary>The stored spelling of a disposition (v1.11 §8.10).</summary>
    public static string Name(AdviceDisposition disposition) => disposition switch
    {
        AdviceDisposition.Accepted => "ACCEPTED",
        AdviceDisposition.Modified => "MODIFIED",
        AdviceDisposition.Rejected => "REJECTED",
        _ => "NOT_APPLICABLE",
    };
}
