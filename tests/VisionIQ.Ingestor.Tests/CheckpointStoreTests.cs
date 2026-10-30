using VisionIQ.Ingestor.Checkpoints;

namespace VisionIQ.Ingestor.Tests;

public sealed class CheckpointStoreTests
{
    [Fact]
    public async Task Missing_checkpoint_returns_null()
    {
        var store = new InMemoryCheckpointStore();

        Assert.Null(await store.GetAsync("ado-demo", "work-items"));
    }

    [Fact]
    public async Task Saved_checkpoint_is_returned_for_resume()
    {
        var store = new InMemoryCheckpointStore();
        var saved = new SyncCheckpoint("ado-demo", "work-items", "2026-10-20T10:15:00Z", DateTimeOffset.UtcNow);

        await store.SaveAsync(saved);

        Assert.Equal(saved, await store.GetAsync("ado-demo", "work-items"));
    }

    [Fact]
    public async Task Checkpoints_are_separated_by_source_and_scope()
    {
        var store = new InMemoryCheckpointStore();
        await store.SaveAsync(new SyncCheckpoint("ado-demo", "work-items", "A", DateTimeOffset.UtcNow));

        Assert.Null(await store.GetAsync("ado-demo", "pull-requests"));
        Assert.Null(await store.GetAsync("ado-other", "work-items"));
    }
}
