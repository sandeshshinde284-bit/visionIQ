using VisionIQ.Api.Features.Briefs;
using VisionIQ.Api.Features.Events;
using VisionIQ.Api.Features.Pickup;

namespace VisionIQ.Api.Features.Feedback;

/// <summary>What the webhook did with a rating comment.</summary>
public sealed record FeedbackResponse(
    string Status, string? Reason = null, IReadOnlyList<string>? Recorded = null, IReadOnlyList<string>? Unknown = null);

/// <summary>
/// BRF-06: handles a "work item commented" delivery on <c>POST /webhooks/ado</c>. The engineer rates advice by
/// replying on the ticket ("ADV-001 accepted"); nothing outside Azure DevOps is needed. A comment is used only if
/// it comes from the configured organization and project, was not written by a VisionIQ bot identity, and names
/// advice ids of the brief published on that ticket. Everything else is acknowledged and ignored with a reason:
/// a comment that is not a rating is not an error.
/// </summary>
public static class AdviceFeedbackEndpoint
{
    public static async Task<IResult> HandleAsync(
        CommentDelivery comment, PickupPolicy policy, IServiceProvider services, ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comment);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(services);
        IResult Ignored(string reason)
        {
            BriefLog.Feedback(logger, comment.Project, comment.WorkItemId, reason, 0);
            return Results.Accepted(value: new FeedbackResponse("ignored", reason));
        }

        if (!policy.IsOrganizationAllowed(comment.OrganizationUrl))
        {
            return Ignored("organization_not_allowed");
        }

        if (!policy.IsWorkItemAllowed(comment.WorkItemId))
        {
            return Ignored("work_item_not_allowlisted");
        }

        if (policy.For(comment.Project) is not { } project)
        {
            return Ignored("project_not_configured");
        }

        if (policy.IsBot(comment.AuthorId, comment.AuthorName))
        {
            return Ignored("bot_comment");
        }

        var ratings = AdviceFeedbackParser.Parse(comment.Text);
        if (ratings.Count == 0)
        {
            return Ignored("no_rating_in_comment");
        }

        // "Who" is the ADO identity id; older payloads only carry the unique name.
        var reviewer = comment.AuthorId ?? comment.AuthorName;
        if (string.IsNullOrWhiteSpace(reviewer))
        {
            return Ignored("reviewer_unknown");
        }

        var store = services.GetService<IAdviceFeedbackStore>();
        var eventing = services.GetService<EventingOptions>();
        if (store is null || eventing is null)
        {
            return Ignored("feedback_not_enabled");
        }

        FeedbackOutcome outcome;
        try
        {
            outcome = await store.RecordAsync(
                new FeedbackSubmission(
                    eventing.TenantId,
                    project.ProjectId,
                    comment.WorkItemId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    comment.EventId,
                    reviewer.Length > 200 ? reviewer[..200] : reviewer,
                    comment.CommentedAt ?? DateTimeOffset.UtcNow,
                    ratings),
                cancellationToken);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // ADO retries the delivery; the same event id then records each rating at most once.
            BriefLog.FeedbackFailed(logger, project.ProjectId, comment.WorkItemId, error.GetType().Name);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        BriefLog.Feedback(
            logger, project.ProjectId, comment.WorkItemId, outcome.Reason ?? outcome.Status, outcome.Recorded.Count);
        return Results.Accepted(value: new FeedbackResponse(outcome.Status, outcome.Reason, outcome.Recorded, outcome.Unknown));
    }
}
