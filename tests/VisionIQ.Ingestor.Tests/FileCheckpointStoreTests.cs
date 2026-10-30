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
}
