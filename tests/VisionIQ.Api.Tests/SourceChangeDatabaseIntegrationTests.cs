using VisionIQ.Api.Configuration;
using VisionIQ.Api.Features.Backfill;

namespace VisionIQ.Api.Tests;

/// <summary>Opt-in durable event lease and replay checks on disposable PostgreSQL.</summary>
public sealed class SourceChangeDatabaseIntegrationTests
{
    [Fact]
    public async Task Inbox_rejects_conflicting_replay_and_retries_quarantine_without_duplicates()
    {
        if (Environment.GetEnvironmentVariable("VISIONIQ_CHANGE_DB_INTEGRATION") != "1")
        {
            return;
        }

        var password = Environment.GetEnvironmentVariable("DB_API_PASSWORD");
        Assert.False(string.IsNullOrWhiteSpace(password));
        var options = new DatabaseOptions(
            Environment.GetEnvironmentVariable("DB_HOST") ?? "127.0.0.1",
            5432,
            Environment.GetEnvironmentVariable("DB_NAME") ?? "visioniq",
            Environment.GetEnvironmentVariable("DB_API_USER") ?? "visioniq_api",
            password!, "disable");

        await using var inbox = new PostgresSourceChangeInbox(options);
        var identifier = Guid.NewGuid().ToString("N");
        var change = new SourceChangeNotification(
            "ci-source", identifier, "work_item", "123", "7");

        var first = await inbox.TryClaimAsync(change);
        Assert.Equal(ChangeClaimStatus.Claimed, first.Status);
        Assert.NotNull(first.Token);
        Assert.Equal(ChangeClaimStatus.InProgress,
            (await inbox.TryClaimAsync(change)).Status);
        Assert.Equal(ChangeClaimStatus.IdentityConflict,
            (await inbox.TryClaimAsync(change with { SourceRevision = "8" })).Status);
        await inbox.QuarantineAsync(change, first.Token!.Value, "SOURCE_APPLY_FAILED");

        var retry = await inbox.TryClaimAsync(change);
        Assert.Equal(ChangeClaimStatus.Claimed, retry.Status);
        Assert.NotEqual(first.Token, retry.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            inbox.CompleteAsync(change, first.Token!.Value));
        await inbox.CompleteAsync(change, retry.Token!.Value);
        Assert.Equal(ChangeClaimStatus.AlreadyCompleted,
            (await inbox.TryClaimAsync(change)).Status);
    }
}
