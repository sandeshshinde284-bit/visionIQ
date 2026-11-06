using System.Text.Json;

namespace VisionIQ.Api.Features.Events;

/// <summary>
/// Pub/Sub push endpoint (S03-09). Pub/Sub treats 2xx as ack and anything else as nack; a nacked message is
/// retried with backoff and, after the subscription's max delivery attempts, moved to its dead-letter topic.
/// <list type="bullet">
/// <item>processed or duplicate: 204 (ack; a duplicate changes nothing)</item>
/// <item>malformed, contract-invalid or in conflict with stored state: 400 (retrying cannot fix it; it ends in the
/// dead-letter topic)</item>
/// <item>database or handler failure: 503 (transient; Pub/Sub retries)</item>
/// </list>
/// On Cloud Run the service stays private: Pub/Sub authenticates with an OIDC token for a push service
/// account holding roles/run.invoker, which Cloud Run checks before the request reaches this code.
/// </summary>
public static class PubSubPushEndpoints
{
    public static IEndpointRouteBuilder MapPubSubPush(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost(EventTopology.PushPath, ReceiveAsync).WithName("PubSubPush");
        return endpoints;
    }

    private static async Task<IResult> ReceiveAsync(
        HttpRequest request, IInboundEventProcessor processor, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var logger = loggers.CreateLogger(typeof(PubSubPushEndpoints));
        EventEnvelope envelope;
        string? messageId;
        int? deliveryAttempt;
        try
        {
            using var push = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            var message = push.RootElement.GetProperty("message");
            messageId = message.TryGetProperty("messageId", out var id) ? id.GetString() : null;
            deliveryAttempt = push.RootElement.TryGetProperty("deliveryAttempt", out var attempt) && attempt.TryGetInt32(out var n) ? n : null;
            envelope = EventEnvelope.Parse(message.GetProperty("data").GetBytesFromBase64());
        }
        catch (EventContractException error)
        {
            EventLog.Rejected(logger, "contract_violation", error.Message);
            return Rejected("contract_violation");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            EventLog.Rejected(logger, "malformed_push_message", error.GetType().Name);
            return Rejected("malformed_push_message");
        }

        using var scope = logger.BeginScope(envelope.LogScope());
        EventLog.Received(logger, messageId, deliveryAttempt);
        try
        {
            var outcome = await processor.ProcessAsync(envelope, cancellationToken);
            EventLog.Processed(logger, outcome);
            return Results.NoContent();
        }
        catch (EventContractException error)
        {
            EventLog.Rejected(logger, "not_consumed_here", error.Message);
            return Rejected("not_consumed_here");
        }
        catch (EventRejectedException error)
        {
            EventLog.Rejected(logger, error.Reason, error.Reason);
            return Rejected(error.Reason);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            EventLog.ProcessingFailed(logger, error.GetType().Name);
            return Results.Json(new { status = "retry" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static IResult Rejected(string reason) =>
        Results.Json(new { status = "rejected", reason }, statusCode: StatusCodes.Status400BadRequest);
}
