using Microsoft.Extensions.Logging;

namespace VisionIQ.Ingestor.Logging;

/// <summary>
/// Structured ingestion events. Source calls log the method and PATH only (never query strings, headers, tokens or
/// bodies). Item IDs, scopes, counts and reason codes are logged; evidence content never is.
/// </summary>
public static partial class IngestionLog
{
    // Source calls
    [LoggerMessage(EventId = 300, Level = LogLevel.Debug, Message = "{Source} {Method} {Path} responded {StatusCode} in {ElapsedMs:0.0} ms (attempt {Attempt})")]
    public static partial void Call(ILogger logger, string source, string method, string path, int statusCode, double elapsedMs, int attempt);

    [LoggerMessage(EventId = 301, Level = LogLevel.Warning, Message = "{Source} {Method} {Path} attempt {Attempt} failed ({Cause}); retrying in {DelayMs:0} ms")]
    public static partial void Retry(ILogger logger, string source, string method, string path, int attempt, string cause, double delayMs);

    [LoggerMessage(EventId = 302, Level = LogLevel.Error, Message = "{Source} {Method} {Path} failed after {Attempts} attempt(s): {Cause}")]
    public static partial void CallFailed(ILogger logger, string source, string method, string path, int attempts, string cause);

    // Runs and streams
    [LoggerMessage(EventId = 310, Level = LogLevel.Information, Message = "Sync run {RunId} started: sources {Kinds}, mode {Mode}, since {Since}")]
    public static partial void RunStarted(ILogger logger, Guid runId, string kinds, string mode, string since);

    [LoggerMessage(EventId = 311, Level = LogLevel.Information, Message = "Sync run {RunId} finished in {ElapsedMs:0} ms: {Streams} stream(s), all synced: {AllSynced}")]
    public static partial void RunFinished(ILogger logger, Guid runId, double elapsedMs, int streams, bool allSynced);

    [LoggerMessage(EventId = 312, Level = LogLevel.Information, Message = "Stream {Stream} {Scope}: {Status}, {Records} stored, {NotStored} not stored, {Quarantined} quarantined, {Links} links")]
    public static partial void StreamSynced(ILogger logger, string stream, string scope, string status, int records, int notStored, int quarantined, int links);

    [LoggerMessage(EventId = 313, Level = LogLevel.Warning, Message = "Stream {Stream} {Scope}: {Status}, {Records} stored, {NotStored} not stored, {Quarantined} quarantined: {Reason}")]
    public static partial void StreamDegraded(ILogger logger, string stream, string scope, string status, int records, int notStored, int quarantined, string reason);

    [LoggerMessage(EventId = 314, Level = LogLevel.Information, Message = "Stream {Stream} {Scope} note: {Note}")]
    public static partial void StreamNote(ILogger logger, string stream, string scope, string note);

    // Checkpoints, quarantine, deletions
    [LoggerMessage(EventId = 320, Level = LogLevel.Debug, Message = "Checkpoint {Scope}: {Action} {Cursor}")]
    public static partial void Checkpoint(ILogger logger, string scope, string action, string cursor);

    [LoggerMessage(EventId = 321, Level = LogLevel.Warning, Message = "Quarantined {Kind} {NativeId} in {Scope}: {ReasonCode}")]
    public static partial void Quarantined(ILogger logger, string kind, string nativeId, string scope, string reasonCode);

    [LoggerMessage(EventId = 322, Level = LogLevel.Information, Message = "Quarantine resolved for {Kind} {NativeId} in {Scope}")]
    public static partial void QuarantineResolved(ILogger logger, string kind, string nativeId, string scope);

    [LoggerMessage(EventId = 323, Level = LogLevel.Information, Message = "Recycle Bin for {Project}: {Count} deleted work item(s) recorded")]
    public static partial void DeletionsFound(ILogger logger, string project, int count);
}
