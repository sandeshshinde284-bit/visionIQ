namespace VisionIQ.Api.Features.Pickup;

/// <summary>
/// Source-generated pickup log messages. Only project, work-item id, revision and fixed reason codes are logged;
/// ticket text and identities never are.
/// </summary>
internal static partial class PickupLog
{
    [LoggerMessage(EventId = 600, Level = LogLevel.Warning, Message = "ADO webhook rejected: {Reason}")]
    public static partial void WebhookRejected(ILogger logger, string reason);

    [LoggerMessage(EventId = 601, Level = LogLevel.Information, Message = "ADO webhook for {Project} #{WorkItemId} rev {Revision} ignored: {Reason}")]
    public static partial void Ignored(ILogger logger, string project, int workItemId, int revision, string reason);

    [LoggerMessage(EventId = 602, Level = LogLevel.Information, Message = "Pickup of {ProjectId} #{TicketId} rev {Revision} via {Trigger}: {Outcome}")]
    public static partial void Recorded(ILogger logger, string projectId, string ticketId, int revision, string trigger, RecordOutcome outcome);

    [LoggerMessage(EventId = 603, Level = LogLevel.Information, Message = "Pickup of {ProjectId} #{TicketId} rev {Revision}: {Outcome} run {RunId}")]
    public static partial void RunStarted(ILogger logger, string projectId, string ticketId, int revision, StartOutcome outcome, Guid runId);

    [LoggerMessage(EventId = 604, Level = LogLevel.Warning, Message = "Pickup of {ProjectId} #{TicketId} rev {Revision} deferred ({Reason}, {ErrorType}), attempt {Attempt}")]
    public static partial void Deferred(ILogger logger, string projectId, string ticketId, int revision, string reason, string errorType, int attempt);

    [LoggerMessage(EventId = 605, Level = LogLevel.Error, Message = "Pickup of {ProjectId} #{TicketId} rev {Revision} failed: {Reason}")]
    public static partial void Failed(ILogger logger, string projectId, string ticketId, int revision, string reason);

    [LoggerMessage(EventId = 606, Level = LogLevel.Information, Message = "Pickup sweep of {Project}: {Found} in pickup state, {Recovered} missed pickups recovered")]
    public static partial void Swept(ILogger logger, string project, int found, int recovered);

    [LoggerMessage(EventId = 607, Level = LogLevel.Error, Message = "Pickup sweep of {Project} failed ({ErrorType})")]
    public static partial void SweepFailed(ILogger logger, string project, string errorType);

    [LoggerMessage(EventId = 608, Level = LogLevel.Error, Message = "Pickup worker failed ({ErrorType})")]
    public static partial void WorkerFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 609, Level = LogLevel.Information, Message = "Ticket pickup on (policy {PolicyRevision}, {Projects} projects, sweep every {SweepMinutes} min)")]
    public static partial void Enabled(ILogger logger, string policyRevision, int projects, double sweepMinutes);

    [LoggerMessage(EventId = 610, Level = LogLevel.Warning, Message = "Pickup sweep is off (no ADO_ORG_URL/ADO_READ_PAT, or PICKUP_SWEEP_INTERVAL_MINUTES is 0): missed webhooks are not recovered")]
    public static partial void SweepOff(ILogger logger);

    [LoggerMessage(EventId = 611, Level = LogLevel.Information, Message = "Ticket pickup is limited to {Count} work items (PICKUP_ALLOWED_WORK_ITEM_IDS): {WorkItemIds}")]
    public static partial void Restricted(ILogger logger, int count, string workItemIds);

    [LoggerMessage(EventId = 612, Level = LogLevel.Warning, Message = "Ticket pickup is not limited to chosen work items: every eligible item of the configured projects starts a run")]
    public static partial void Unrestricted(ILogger logger);

    [LoggerMessage(EventId = 613, Level = LogLevel.Error, Message = "Pickup of {ProjectId} #{TicketId} rev {Revision} could not be processed ({ErrorType}); it is retried when its lease expires")]
    public static partial void ItemFailed(ILogger logger, string projectId, string ticketId, int revision, string errorType);
}
