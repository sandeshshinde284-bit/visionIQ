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

    public int Links { get; set; }

    /// <summary>Why the stream is partial or not synced. Never contains credentials.</summary>
    public string? Reason { get; set; }

    /// <summary>Information that does not change the status, for example pages hidden because their permissions are unclear.</summary>
    public IList<string> Notes { get; } = new List<string>();
}

/// <summary>The outcome of one sync run: counts per stream, and a reason for anything not synced.</summary>
public sealed class SyncReport
{
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
