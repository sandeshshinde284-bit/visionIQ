using System.Text.Json;
using Microsoft.AspNetCore.Http;
using VisionIQ.Api.Features.Backfill;

namespace VisionIQ.Ingestor.Cli;

/// <summary>
/// LRN-05 webhook: authenticates, learns which work item changed, and runs it through the idempotent inbox, whose
/// handler re-fetches the item from ADO. Responses tell ADO whether to retry: done, duplicate or dead-lettered is
/// 200; a failed attempt is 500 (ADO retries); a concurrent attempt is 503.
/// </summary>
public sealed class AdoWebhookEndpoint(
    WebhookCredentials credentials, SourceChangeProcessor processor, Uri organization,
    IReadOnlyCollection<string> approvedProjects, string tenant)
{
    public const string Route = "/webhooks/ado/work-items";
    private const long MaxBody = 1024 * 1024;

    public async Task<IResult> HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!credentials.IsValid(context.Request.Headers.Authorization.ToString()))
        {
            return Results.Unauthorized();
        }

        if (context.Request.ContentLength is > MaxBody)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { status = "invalid" });
        }

        using (document)
        {
            switch (AdoServiceHook.Parse(document.RootElement, organization, approvedProjects, tenant, out var change))
            {
                case ServiceHookOutcome.Invalid:
                    return Results.BadRequest(new { status = "invalid" });
                case ServiceHookOutcome.Ignored:
                    return Results.Ok(new { status = "ignored" });
            }

            var status = await processor.ProcessAsync(change!, context.RequestAborted);
            return status switch
            {
                ChangeProcessingStatus.Completed or ChangeProcessingStatus.AlreadyCompleted => Results.Ok(new { status = "processed" }),
                // Dead-lettered: stop retries; the scheduled reconciliation sweep will pick the item up.
                ChangeProcessingStatus.DeadLettered => Results.Ok(new { status = "dead-lettered" }),
                ChangeProcessingStatus.InProgress => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
                ChangeProcessingStatus.IdentityConflict => Results.Conflict(new { status = "conflict" }),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        }
    }
}
