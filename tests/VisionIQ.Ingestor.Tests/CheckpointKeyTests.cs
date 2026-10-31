using VisionIQ.Ingestor.Checkpoints;

namespace VisionIQ.Ingestor.Tests;

/// <summary>m-3: file checkpoint keys must not collide.</summary>
public sealed class CheckpointKeyTests
{
    [Fact]
    public void Ambiguous_pairs_get_different_keys() =>
        Assert.NotEqual(FileCheckpointStore.Key("a:", ":b"), FileCheckpointStore.Key("a", "::b"));

    [Fact]
    public async Task Checkpoints_saved_under_the_old_key_format_still_load()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var legacy = new SyncCheckpoint("contoso/ado", "Demo/work-items", "2026-10-01T00:00:00.0000000Z|1", DateTimeOffset.UnixEpoch);
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, SyncCheckpoint> { ["contoso/ado::Demo/work-items"] = legacy }));

            var loaded = await new FileCheckpointStore(path).GetAsync("contoso/ado", "Demo/work-items");

            Assert.Equal(legacy.Cursor, loaded!.Cursor);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
