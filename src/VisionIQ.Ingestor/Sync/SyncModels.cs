namespace VisionIQ.Ingestor.Sync;

public enum SyncKind
{
    WorkItems,
    PullRequests,
    Commits,
    Builds,
    Confluence,
}

public enum StreamStatus
{
    Synced,
    Partial,
    NotSynced,
}

/// <summary>What to read. <see cref="IgnoreCheckpoints"/> re-reads from <see cref="Since"/> (or all history).</summary>
public sealed record SyncRequest(IReadOnlySet<SyncKind> Kinds, DateTimeOffset? Since, bool IgnoreCheckpoints = false, bool IncludeRevisions = true)
{
    /// <summary>
    /// LRN-05 reconciliation sweep: re-read a recent window (Since, ignoring checkpoints) to catch anything a
    /// webhook missed, WITHOUT saving checkpoints, so the regular delta position never moves backwards.
    /// </summary>
    public bool Reconcile { get; init; }

    /// <summary>Correlates every log line of one sync (ADO and Confluence) with its report.</summary>
    public Guid RunId { get; init; } = Guid.NewGuid();

    public static IReadOnlySet<SyncKind> AllKinds { get; } =
        new HashSet<SyncKind> { SyncKind.WorkItems, SyncKind.PullRequests, SyncKind.Commits, SyncKind.Builds, SyncKind.Confluence };
}

public sealed class StreamResult
{
    public StreamResult(SyncKind stream, string scope)
    {
        Stream = stream;
        Scope = scope;
    }

    public SyncKind Stream { get; }

    public string Scope { get; }

    public StreamStatus Status { get; set; } = StreamStatus.Synced;

    public int Records { get; set; }

    /// <summary>M-12: items the source returned that were not written (e.g. omitted work items).</summary>
    public int NotStored { get; set; }

    /// <summary>G-1: items placed on the quarantine list in this run.</summary>
    public int Quarantined { get; set; }

    public int Links { get; set; }

    /// <summary>Why the stream is partial or not synced. Never contains credentials.</summary>
    public string? Reason { get; set; }

    /// <summary>Information that does not change the status, for example pages hidden because their permissions are unclear.</summary>
    public IList<string> Notes { get; } = new List<string>();
}

/// <summary>The outcome of one sync run: counts per stream, and a reason for anything not synced.</summary>
public sealed class SyncReport
{
    /// <summary>The sync run this report belongs to (also on every log line of the run).</summary>
    public Guid RunId { get; set; }

    private readonly List<StreamResult> streams = [];

    public required string Tenant { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; set; }

    public IReadOnlyList<StreamResult> Streams => streams;

    public bool AllSynced => streams.Count > 0 && streams.TrueForAll(stream => stream.Status == StreamStatus.Synced);

    /// <summary>Combines reports from several runners (for example Azure DevOps and Confluence) into one.</summary>
    public static SyncReport Merge(IReadOnlyList<SyncReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var merged = new SyncReport
        {
            Tenant = string.Join(", ", reports.Select(report => report.Tenant).Distinct(StringComparer.Ordinal)),
            StartedAt = reports.Count == 0 ? DateTimeOffset.UtcNow : reports.Min(report => report.StartedAt),
            FinishedAt = reports.Count == 0 ? null : reports.Max(report => report.FinishedAt),
        };
        foreach (var report in reports)
        {
            merged.streams.AddRange(report.streams);
        }

        return merged;
    }

    internal StreamResult Add(SyncKind stream, string scope)
    {
        var result = new StreamResult(stream, scope);
        streams.Add(result);
        return result;
    }
}

/// <summary>Raised when checkpoints cannot be read or saved, so the run stops instead of re-reading or losing progress.</summary>
public sealed class CheckpointStoreException : Exception
{
    public CheckpointStoreException()
    {
    }

    public CheckpointStoreException(string message)
        : base(message)
    {
    }

    public CheckpointStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Logs the outcome of a sync run: one line per stream, then the run summary.</summary>
public static class SyncRunLogging
{
    public static void LogOutcome(Microsoft.Extensions.Logging.ILogger log, SyncReport report, double elapsedMs)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(report);
        if (!log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Information))
        {
            return; // Warnings for degraded streams are Information-or-above too; nothing to build when disabled
        }

        foreach (var stream in report.Streams)
        {
            var kind = stream.Stream.ToString();
            var status = stream.Status.ToString();
            if (stream.Status == StreamStatus.Synced)
            {
                Logging.IngestionLog.StreamSynced(log, kind, stream.Scope, status, stream.Records, stream.NotStored, stream.Quarantined, stream.Links);
            }
            else
            {
                var reason = stream.Reason ?? "no reason recorded";
                Logging.IngestionLog.StreamDegraded(log, kind, stream.Scope, status, stream.Records, stream.NotStored, stream.Quarantined, reason);
            }

            foreach (var note in stream.Notes)
            {
                Logging.IngestionLog.StreamNote(log, kind, stream.Scope, note);
            }
        }

        Logging.IngestionLog.RunFinished(log, report.RunId, elapsedMs, report.Streams.Count, report.AllSynced);
    }

    public static IDisposable? BeginRun(Microsoft.Extensions.Logging.ILogger log, SyncRequest request, string source)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = log.BeginScope(new Dictionary<string, object> { ["runId"] = request.RunId, ["source"] = source });
        if (log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Information))
        {
            var kinds = string.Join(",", request.Kinds.Order());
            var mode = request.Reconcile ? "reconcile" : request.IgnoreCheckpoints ? "full" : "delta";
            var since = request.Since?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "checkpoint";
            Logging.IngestionLog.RunStarted(log, request.RunId, kinds, mode, since);
        }
        return scope;
    }
}
