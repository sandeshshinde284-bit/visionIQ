namespace VisionIQ.Api.Features.Events;

/// <summary>
/// Source-generated event log messages. Correlation (traceId, correlationId, runId, eventId) comes from the log
/// scope; payload values are never logged.
/// </summary>
internal static partial class EventLog
{
    [LoggerMessage(EventId = 500, Level = LogLevel.Information, Message = "Event received (message {MessageId}, delivery attempt {DeliveryAttempt})")]
    public static partial void Received(ILogger logger, string? messageId, int? deliveryAttempt);

    [LoggerMessage(EventId = 501, Level = LogLevel.Information, Message = "Event {Outcome}")]
    public static partial void Processed(ILogger logger, InboundOutcome outcome);

    [LoggerMessage(EventId = 502, Level = LogLevel.Warning, Message = "Event rejected: {Reason} ({Detail})")]
    public static partial void Rejected(ILogger logger, string reason, string detail);

    [LoggerMessage(EventId = 503, Level = LogLevel.Error, Message = "Event processing failed ({ErrorType}); Pub/Sub will retry")]
    public static partial void ProcessingFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 504, Level = LogLevel.Information, Message = "Event published to {Topic}")]
    public static partial void Published(ILogger logger, string topic);

    [LoggerMessage(EventId = 505, Level = LogLevel.Warning, Message = "Event publish to {Topic} failed ({ErrorType}), attempt {Attempt}; retrying in {DelaySeconds} s")]
    public static partial void PublishFailed(ILogger logger, string topic, string errorType, int attempt, int delaySeconds);

    [LoggerMessage(EventId = 510, Level = LogLevel.Error, Message = "Outbox event {EventId} no longer matches its contract; parked unpublished")]
    public static partial void Unpublishable(ILogger logger, Guid eventId);

    [LoggerMessage(EventId = 511, Level = LogLevel.Error, Message = "Event publish to {Topic} failed ({ErrorType}) on attempt {Attempt}; parked unpublished")]
    public static partial void PublishGaveUp(ILogger logger, string topic, string errorType, int attempt);

    [LoggerMessage(EventId = 506, Level = LogLevel.Information, Message = "Outbox dispatcher started (every {IntervalSeconds} s)")]
    public static partial void DispatcherStarted(ILogger logger, double intervalSeconds);

    [LoggerMessage(EventId = 507, Level = LogLevel.Error, Message = "Outbox dispatch failed ({ErrorType})")]
    public static partial void DispatchFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 508, Level = LogLevel.Information, Message = "Analysis requested")]
    public static partial void AnalysisRequested(ILogger logger);

    [LoggerMessage(EventId = 509, Level = LogLevel.Information, Message = "Analysis completed with {ResultState}")]
    public static partial void AnalysisCompleted(ILogger logger, ResultState resultState);
}
