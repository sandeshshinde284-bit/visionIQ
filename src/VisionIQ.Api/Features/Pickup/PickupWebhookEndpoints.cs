using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace VisionIQ.Api.Features.Pickup;

/// <summary>What the webhook did with a delivery: queued, duplicate, or ignored with a reason.</summary>
public sealed record PickupResponse(string Status, string? Reason = null, Guid? PickupId = null);

/// <summary>
/// S04-02 ADO service-hook receiver, <c>POST /webhooks/ado</c> (v1.11 §8.2, §10.11). It checks the shared secret,
/// applies the pickup rules, stores an eligible pickup once and answers 202 within milliseconds; no ADO call and
/// no analysis happen on the request thread (<see cref="PickupWorker"/> starts the run).
/// <list type="bullet">
/// <item>401: missing or wrong credentials.</item>
/// <item>400: not a usable work-item service-hook body (ADO shows the subscription as failing).</item>
/// <item>202: queued, duplicate (same ADO event id again), or ignored with a reason code.</item>
/// <item>503: the pickup could not be stored; ADO retries the delivery.</item>
/// </list>
/// </summary>
public static class PickupWebhookEndpoints
{
    public const int MaxBodyBytes = 1 << 20;

    public static IEndpointRouteBuilder MapAdoPickupWebhook(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/webhooks/ado", ReceiveAsync).WithName("AdoPickupWebhook");
        return endpoints;
    }

    /// <summary>
    /// ADO service hooks send "Basic base64(user:password)"; the password must equal PICKUP_WEBHOOK_SECRET and the
    /// user name is ignored. The comparison takes the same time whatever the input.
    /// </summary>
    public static bool IsAuthorized(string? authorization, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        if (!AuthenticationHeaderValue.TryParse(authorization, out var header) ||
            !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) ||
            header.Parameter is null)
        {
            return false;
        }

        string credentials;
        try
        {
            credentials = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
        }
        catch (FormatException)
        {
            return false;
        }

        var separator = credentials.IndexOf(':', StringComparison.Ordinal);
        var password = separator < 0 ? string.Empty : credentials[(separator + 1)..];
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(password)), SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    private static async Task<IResult> ReceiveAsync(
        HttpRequest request, PickupOptions options, ITicketPickupStore store, ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var logger = loggers.CreateLogger(typeof(PickupWebhookEndpoints));
        if (!IsAuthorized(request.Headers.Authorization, options.WebhookSecret))
        {
            PickupLog.WebhookRejected(logger, "unauthorized");
            return Results.Unauthorized();
        }

        var body = await ReadBodyAsync(request, cancellationToken);
        if (body is null)
        {
            PickupLog.WebhookRejected(logger, "body_too_large");
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var delivery = AdoServiceHook.Parse(body, out var problem);
        if (delivery is null)
        {
            PickupLog.WebhookRejected(logger, problem!);
            // Another event type on the same subscription is not an error for ADO to retry.
            return problem == "unsupported_event"
                ? Results.Accepted(value: new PickupResponse("ignored", problem))
                : Results.BadRequest(new PickupResponse("rejected", problem));
        }

        var change = delivery.Change;
        var decision = options.Policy.Evaluate(change);
        if (!decision.Eligible)
        {
            PickupLog.Ignored(logger, change.Project, change.WorkItemId, change.Revision, decision.Reason!);
            return Results.Accepted(value: new PickupResponse("ignored", decision.Reason));
        }

        // Continue the caller's W3C trace when there is one, so the run's trace starts at the webhook.
        var traceId = Activity.Current?.TraceId.ToHexString() ?? ActivityTraceId.CreateRandom().ToHexString();
        var pickup = TicketPickup.From(
            change, decision, options.Policy.Revision, PickupTrigger.AdoWebhook, delivery.EventId,
            Convert.ToHexStringLower(SHA256.HashData(body)), traceId);
        RecordOutcome outcome;
        try
        {
            outcome = await store.RecordAsync(pickup, cancellationToken);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            PickupLog.WebhookRejected(logger, "store_unavailable_" + error.GetType().Name);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        PickupLog.Recorded(logger, pickup.ProjectId, pickup.TicketId, pickup.TicketRevision, "ado_webhook", outcome);
        return outcome == RecordOutcome.Recorded
            ? Results.Accepted(value: new PickupResponse("queued", PickupId: pickup.PickupId))
            : Results.Accepted(value: new PickupResponse("duplicate"));
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
