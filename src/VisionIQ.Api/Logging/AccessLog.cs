namespace VisionIQ.Api.Logging;

/// <summary>
/// SP-1: why evidence was hidden. Routine denials (not a member, not mapped) are Debug so production logs stay
/// quiet; failures of the check itself (outage, error) are Warning because they hide evidence from everyone.
/// Only opaque keys, providers, kinds and reason codes are logged; never evidence content or identities in clear.
/// </summary>
public static partial class AccessLog
{
    [LoggerMessage(EventId = 200, Level = LogLevel.Debug, Message = "Evidence {ItemKey} hidden: {Reason}")]
    public static partial void Hidden(ILogger logger, string itemKey, string reason);

    [LoggerMessage(EventId = 201, Level = LogLevel.Debug, Message = "{Provider} {Kind} access denied: {Reason}")]
    public static partial void Denied(ILogger logger, string provider, string kind, string reason);

    [LoggerMessage(EventId = 202, Level = LogLevel.Warning, Message = "{Provider} access check failed ({ErrorType}); evidence hidden (fail closed)")]
    public static partial void CheckFailed(ILogger logger, string provider, string errorType);

    [LoggerMessage(EventId = 210, Level = LogLevel.Warning, Message = "Graph API caller denied: {Reason} (calling app {CallerApp})")]
    public static partial void CallerDenied(ILogger logger, string reason, string callerApp);

    [LoggerMessage(EventId = 211, Level = LogLevel.Information, Message = "Graph query {Project}/{WorkItemId} hops {Hops} by {User}: {Outcome} ({Nodes} nodes, {Edges} edges)")]
    public static partial void GraphQuery(ILogger logger, string project, string workItemId, int hops, string user, string outcome, int nodes, int edges);
}
