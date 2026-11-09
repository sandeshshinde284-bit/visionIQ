using System.Text.Json;

namespace VisionIQ.Ingestor.Checkpoints;

/// <summary>Checkpoints in a local JSON file, for runs without the database (CHECKPOINT_STORE=file).</summary>
public sealed class FileCheckpointStore : ICheckpointStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string path;
    private readonly Lock gate = new();

    public FileCheckpointStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = path;
    }

    public Task<SyncCheckpoint?> GetAsync(string sourceId, string scope, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var all = Load();
            // m-3: unambiguous key; fall back to the pre-fix key so existing checkpoint files still load.
            return Task.FromResult<SyncCheckpoint?>(
                all.TryGetValue(Key(sourceId, scope), out var checkpoint) ||
                all.TryGetValue(LegacyKey(sourceId, scope), out checkpoint)
                    ? checkpoint
                    : null);
        }
    }

    public Task SaveAsync(SyncCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        lock (gate)
        {
            var all = Load();
            var key = Key(checkpoint.SourceId, checkpoint.Scope);
            // Same monotonic guard as DbCheckpointStore: Cursor is TimeCursor.Format(), whose fixed-width
            // ISO-8601 prefix sorts lexicographically the same as chronologically. Two processes sharing
            // one checkpoint file (e.g. a manual run started while a scheduled one is still in flight)
            // must not let the slower one regress what the faster one already recorded.
            if (all.TryGetValue(key, out var existing) &&
                string.CompareOrdinal(checkpoint.Cursor, existing.Cursor) < 0)
            {
                throw new InvalidOperationException(
                    "Checkpoint not saved: the file already has a checkpoint for " + checkpoint.SourceId + "/"
                    + checkpoint.Scope + " that is further ahead (likely another run in progress). This run's "
                    + "records were already written; only this checkpoint write was skipped.");
            }

            all[key] = checkpoint;
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write then rename, so an interrupted run never leaves a half-written file.
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, Json));
            File.Move(temporary, path, overwrite: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>m-3: escaping makes ("a:", ":b") and ("a", "::b") different keys.</summary>
    public static string Key(string sourceId, string scope) =>
        Uri.EscapeDataString(sourceId) + "::" + Uri.EscapeDataString(scope);

    private static string LegacyKey(string sourceId, string scope) => sourceId + "::" + scope;

    private Dictionary<string, SyncCheckpoint> Load() =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, SyncCheckpoint>>(File.ReadAllText(path), Json) ?? new()
            : new();
}
