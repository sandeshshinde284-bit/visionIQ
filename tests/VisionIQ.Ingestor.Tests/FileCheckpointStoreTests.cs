using VisionIQ.Ingestor.Checkpoints;

namespace VisionIQ.Ingestor.Tests;

public sealed class FileCheckpointStoreTests
{
    [Fact]
    public async Task Checkpoints_survive_a_new_process()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "nested", "checkpoints.json");
            var saved = new SyncCheckpoint("contoso/ado", "VisionIQ Demo/work-items", "2026-10-20T10:15:00.0000000Z|1234", new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));

            await new FileCheckpointStore(path).SaveAsync(saved);

            Assert.Equal(saved, await new FileCheckpointStore(path).GetAsync("contoso/ado", "VisionIQ Demo/work-items"));
            Assert.Null(await new FileCheckpointStore(path).GetAsync("contoso/ado", "VisionIQ Demo/builds"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_checkpoint_behind_the_one_already_saved_is_rejected_not_silently_overwritten()
    {
        // Simulates two overlapping runs for the same scope: a faster one finishes first and saves
        // further progress, then a slower one (started earlier, saw less data) tries to save its own,
        // earlier cursor afterward. That must fail loudly, never silently regress the stored checkpoint.
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "checkpoints.json");
            var store = new FileCheckpointStore(path);
            var ahead = new SyncCheckpoint("contoso/ado", "VisionIQ Demo/work-items", "2026-10-20T10:10:00.0000000Z|200", DateTimeOffset.UtcNow);
            var behind = new SyncCheckpoint("contoso/ado", "VisionIQ Demo/work-items", "2026-10-20T10:05:00.0000000Z|100", DateTimeOffset.UtcNow);

            await store.SaveAsync(ahead);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(behind));

            Assert.Equal(ahead, await store.GetAsync("contoso/ado", "VisionIQ Demo/work-items"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
