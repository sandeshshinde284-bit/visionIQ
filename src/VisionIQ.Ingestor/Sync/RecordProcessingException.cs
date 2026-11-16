namespace VisionIQ.Ingestor.Sync;

/// <summary>
/// One source record could not be processed. Says which record and which step (enrichment, evidence write,
/// projection, links) failed, so a report or log line points straight at the cause instead of a bare
/// "Unexpected InvalidOperationException".
/// </summary>
public sealed class RecordProcessingException : Exception
{
    public RecordProcessingException(string kind, string id, string stage, Exception inner)
        : base(Describe(kind, id, stage, inner), inner)
    {
        Kind = kind;
        Id = id;
        Stage = stage;
    }

    public RecordProcessingException()
    {
        Kind = Id = Stage = string.Empty;
    }

    public RecordProcessingException(string message)
        : base(message)
    {
        Kind = Id = Stage = string.Empty;
    }

    public RecordProcessingException(string message, Exception inner)
        : base(message, inner)
    {
        Kind = Id = Stage = string.Empty;
    }

    public string Kind { get; }

    public string Id { get; }

    public string Stage { get; }

    private static string Describe(string kind, string id, string stage, Exception inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return $"{kind} {id} failed at {stage}: {inner.GetType().Name}: {inner.Message}";
    }
}
