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
    IReadOnlyCollection<string> approvedProjects, string tenant, Microsoft.Extensions.Logging.ILogger? logger = null)
{
    private readonly Microsoft.Extensions.Logging.ILogger log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public const string Route = "/webhooks/ado/work-items";

    /// <summary>LRN-12: URL for PR Service Hook subscriptions.</summary>
    public const string PullRequestRoute = "/webhooks/ado/pull-requests";
    private const long MaxBody = 1024 * 1024;

    public async Task<IResult> HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!credentials.IsValid(context.Request.Headers.Authorization.ToString()))
        {
            IngestorLogging.WebhookRejected(log, "invalid or missing credentials"); // never the presented value
            return Results.Unauthorized();
        }

        if (context.Request.ContentLength is > MaxBody)
        {
            IngestorLogging.WebhookRejected(log, "body larger than 1 MiB");
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        }
        catch (JsonException)
        {
            IngestorLogging.WebhookRejected(log, "body is not valid JSON");
            return Results.BadRequest(new { status = "invalid" });
        }

        using (document)
        {
            var root = document.RootElement;
            string Field(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "?"
                : "?";
            switch (AdoServiceHook.Parse(root, organization, approvedProjects, tenant, out var change))
            {
                case ServiceHookOutcome.Invalid:
                    IngestorLogging.WebhookRejected(log, "not a recognizable work-item event");
                    return Results.BadRequest(new { status = "invalid" });
                case ServiceHookOutcome.Ignored:
                    if (log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Information))
                    {
                        var ignoredId = Field("id");
                        var ignoredType = Field("eventType");
                        IngestorLogging.WebhookIgnored(log, ignoredId, ignoredType);
                    }

                    return Results.Ok(new { status = "ignored" });
            }

            using var scope = log.BeginScope(new Dictionary<string, object> { ["eventId"] = change!.EventId });
            var status = await processor.ProcessAsync(change, context.RequestAborted);
            var result = Respond(status);
            if (log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Information))
            {
                var project = change.SourceId[(change.SourceId.LastIndexOf('/') + 1)..];
                var outcome = status.ToString();
                var code = (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                IngestorLogging.WebhookProcessed(log, change.EventId, change.ProviderKind, project,
                    change.NativeId, change.SourceRevision, outcome, code, elapsed);
            }
            return result;
        }
    }

    private static IResult Respond(ChangeProcessingStatus status) =>
            status switch
            {
                ChangeProcessingStatus.Completed or ChangeProcessingStatus.AlreadyCompleted => Results.Ok(new { status = "processed" }),
                // Dead-lettered: stop retries; the scheduled reconciliation sweep will pick the item up.
                ChangeProcessingStatus.DeadLettered => Results.Ok(new { status = "dead-lettered" }),
                ChangeProcessingStatus.InProgress => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
                ChangeProcessingStatus.IdentityConflict => Results.Conflict(new { status = "conflict" }),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
}
