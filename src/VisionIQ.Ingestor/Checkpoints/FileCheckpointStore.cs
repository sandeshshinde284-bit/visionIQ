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
            return Task.FromResult<SyncCheckpoint?>(Load().TryGetValue(Key(sourceId, scope), out var checkpoint) ? checkpoint : null);
        }
    }

    public Task SaveAsync(SyncCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        lock (gate)
        {
            var all = Load();
            all[Key(checkpoint.SourceId, checkpoint.Scope)] = checkpoint;
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

    private static string Key(string sourceId, string scope) => sourceId + "::" + scope;

    private Dictionary<string, SyncCheckpoint> Load() =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, SyncCheckpoint>>(File.ReadAllText(path), Json) ?? new()
            : new();
}
