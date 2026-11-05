using VisionIQ.Ingestor.Checkpoints;
using VisionIQ.Ingestor.Sources;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Tests;

/// <summary>
/// Review R1-04: a checkpoint may only move past records that are already on disk. Before the fix the
/// JSONL writer was flushed only on Dispose, so a crash after a checkpoint save lost the buffered lines
/// and the next run started after them.
/// </summary>
public sealed class CheckpointDurabilityTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "visioniq-r104-" + Guid.NewGuid().ToString("N"));

    /// <summary>At every checkpoint save, records how many lines each JSONL file holds on disk.</summary>
    private sealed class DiskObservingCheckpoints(string directory) : ICheckpointStore
    {
        private readonly InMemoryCheckpointStore inner = new();

        public List<(string Scope, Dictionary<string, int> LinesOnDisk)> Saves { get; } = [];

        public Task<SyncCheckpoint?> GetAsync(string sourceId, string scope, CancellationToken cancellationToken = default) =>
            inner.GetAsync(sourceId, scope, cancellationToken);

        public Task SaveAsync(SyncCheckpoint checkpoint, CancellationToken cancellationToken = default)
        {
            var lines = Directory.GetFiles(directory, "*.jsonl").ToDictionary(
                file => Path.GetFileNameWithoutExtension(file),
                file =>
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                });
            Saves.Add((checkpoint.Scope, lines));
            return inner.SaveAsync(checkpoint, cancellationToken);
        }
    }

    [Fact]
    public async Task Jsonl_records_are_on_disk_before_the_checkpoint_that_covers_them_is_saved()
    {
        var checkpoints = new DiskObservingCheckpoints(directory);
        using var sink = new JsonLinesSink(directory);

        await new SyncRunner(DemoAdo.Client(DemoAdo.Server()), checkpoints, sink, new SourceCapabilityRegistry())
            .RunAsync(new SyncRequest(SyncRequest.AllKinds, null));

        var workItemSave = Assert.Single(checkpoints.Saves, save => save.Scope.Contains("work-items", StringComparison.Ordinal));
        Assert.True(workItemSave.LinesOnDisk.GetValueOrDefault("work-items") > 0,
            "The work-item checkpoint was saved while the work-item records were still only in memory.");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
