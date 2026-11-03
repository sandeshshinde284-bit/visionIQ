namespace VisionIQ.Ingestor.Checkpoints;

/// <summary>Where a sync stopped, so an interrupted run resumes without duplicates.</summary>
public sealed record SyncCheckpoint(
    string SourceId,
    string Scope,
    string Cursor,
    DateTimeOffset UpdatedAt);

public interface ICheckpointStore
{
    Task<SyncCheckpoint?> GetAsync(string sourceId, string scope, CancellationToken cancellationToken = default);

    Task SaveAsync(SyncCheckpoint checkpoint, CancellationToken cancellationToken = default);
}

/// <summary>In-memory store for tests and local runs. The PostgreSQL store comes with S04-14 on the database.</summary>
public sealed class InMemoryCheckpointStore : ICheckpointStore
{
    private readonly Dictionary<(string SourceId, string Scope), SyncCheckpoint> items = new();

    public Task<SyncCheckpoint?> GetAsync(string sourceId, string scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        items.TryGetValue((sourceId, scope), out var checkpoint);
        return Task.FromResult(checkpoint);
    }

    public Task SaveAsync(SyncCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        items[(checkpoint.SourceId, checkpoint.Scope)] = checkpoint;
        return Task.CompletedTask;
    }
}
