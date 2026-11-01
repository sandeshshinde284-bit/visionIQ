using Microsoft.Extensions.Logging;

namespace VisionIQ.Ingestor.Cli;

/// <summary>
/// Logging for the ingestor CLI and webhook host: JSON lines by default (Cloud Logging), single-line text with
/// LOG_FORMAT=text for local runs. LOG_LEVEL sets the minimum level (Debug adds every source call).
/// </summary>
public static partial class IngestorLogging
{
    public static LogLevel Level(IReadOnlyDictionary<string, string?> configuration) =>
        Enum.TryParse<LogLevel>(configuration.GetValueOrDefault("LOG_LEVEL"), true, out var level) ? level : LogLevel.Information;

    public static void Configure(ILoggingBuilder logging, IReadOnlyDictionary<string, string?> configuration)
    {
        ArgumentNullException.ThrowIfNull(logging);
        logging.ClearProviders();
        if (string.Equals(configuration.GetValueOrDefault("LOG_FORMAT"), "text", StringComparison.OrdinalIgnoreCase))
        {
            logging.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.IncludeScopes = true;
                options.UseUtcTimestamp = true;
                options.TimestampFormat = "O ";
            });
        }
        else
        {
            logging.AddJsonConsole(options =>
            {
                options.IncludeScopes = true;
                options.UseUtcTimestamp = true;
                options.TimestampFormat = "O";
            });
        }

        logging.SetMinimumLevel(Level(configuration));
    }

    public static ILoggerFactory Create(IReadOnlyDictionary<string, string?> configuration) =>
        LoggerFactory.Create(logging => Configure(logging, configuration));

    [LoggerMessage(EventId = 400, Level = LogLevel.Information, Message = "Graph projection {Kind}: {Count} assertion(s) processed")]
    public static partial void Projected(ILogger logger, string kind, int count);

    [LoggerMessage(EventId = 401, Level = LogLevel.Warning, Message = "Graph projection {Kind} failed for {Item}")]
    public static partial void ProjectionFailed(ILogger logger, string kind, string item);

    [LoggerMessage(EventId = 402, Level = LogLevel.Information, Message = "Automated test unresolved: {Detail}")]
    public static partial void TestUnresolved(ILogger logger, string detail);

    [LoggerMessage(EventId = 403, Level = LogLevel.Error, Message = "Graph projection stopped ({ErrorType}); evidence and audit rows are kept")]
    public static partial void ProjectionStopped(ILogger logger, string errorType);

    [LoggerMessage(EventId = 404, Level = LogLevel.Information, Message = "Sync report written to {Path}")]
    public static partial void ReportWritten(ILogger logger, string path);

    // Webhooks
    [LoggerMessage(EventId = 410, Level = LogLevel.Warning, Message = "Webhook request rejected: {Reason}")]
    public static partial void WebhookRejected(ILogger logger, string reason);

    [LoggerMessage(EventId = 411, Level = LogLevel.Information, Message = "Webhook event {EventId} {EventType} ignored: not an approved organization, project or event type")]
    public static partial void WebhookIgnored(ILogger logger, string eventId, string eventType);

    [LoggerMessage(EventId = 412, Level = LogLevel.Information, Message = "Webhook event {EventId} {Kind} {Project}/{WorkItemId} rev {Revision}: {Outcome}, HTTP {StatusCode} in {ElapsedMs:0} ms")]
    public static partial void WebhookProcessed(ILogger logger, string eventId, string kind, string project, string workItemId, string revision, string outcome, int statusCode, double elapsedMs);
}
