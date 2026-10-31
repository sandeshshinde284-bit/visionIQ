using VisionIQ.Api.Features.Backfill;

namespace VisionIQ.Api.Tests;

/// <summary>LRN-05 unit coverage of at-least-once source notifications and quarantine.</summary>
public sealed class SourceChangeProcessorTests
{
    /// <summary>Builds a deterministic trusted test notification.</summary>
    private static SourceChangeNotification Event() =>
        new("ado:project-a", "provider-event-1", "work_item", "123", "7");

    [Fact]
    public async Task An_initial_event_is_applied_and_marked_complete()
    {
        var inbox = new StubInbox();
        var handler = new StubHandler();
        var result = await new SourceChangeProcessor(inbox, handler).ProcessAsync(Event());

        Assert.Equal(ChangeProcessingStatus.Completed, result);
        Assert.Equal(1, handler.Applied);
        Assert.Equal(1, inbox.Completed);
        Assert.Equal(0, inbox.Quarantined);
    }

    [Fact]
    public async Task A_completed_event_replay_does_not_reinvoke_the_provider()
    {
        var inbox = new StubInbox { NextStatus = ChangeClaimStatus.AlreadyCompleted };
        var handler = new StubHandler();
        var result = await new SourceChangeProcessor(inbox, handler).ProcessAsync(Event());

        Assert.Equal(ChangeProcessingStatus.AlreadyCompleted, result);
        Assert.Equal(0, handler.Applied);
        Assert.Equal(0, inbox.Completed);
    }

    [Fact]
    public async Task Conflicting_event_identity_never_overwrites_an_existing_revision()
    {
        var inbox = new StubInbox { NextStatus = ChangeClaimStatus.IdentityConflict };
        var handler = new StubHandler();
        var result = await new SourceChangeProcessor(inbox, handler).ProcessAsync(Event());

        Assert.Equal(ChangeProcessingStatus.IdentityConflict, result);
        Assert.Equal(0, handler.Applied);
    }

    [Fact]
    public async Task A_provider_failure_is_quarantined_without_raw_exception_text()
    {
        var inbox = new StubInbox();
        var handler = new StubHandler { Throw = true };
        var result = await new SourceChangeProcessor(inbox, handler).ProcessAsync(Event());

        Assert.Equal(ChangeProcessingStatus.Quarantined, result);
        Assert.Equal("SOURCE_APPLY_FAILED", inbox.ErrorCode);
        Assert.Equal(1, inbox.Quarantined);
        Assert.Equal(0, inbox.Completed);
    }

    [Fact]
    public async Task A_busy_claim_is_not_processed_concurrently()
    {
        var inbox = new StubInbox { NextStatus = ChangeClaimStatus.InProgress };
        var handler = new StubHandler();
        var result = await new SourceChangeProcessor(inbox, handler).ProcessAsync(Event());

        Assert.Equal(ChangeProcessingStatus.InProgress, result);
        Assert.Equal(0, handler.Applied);
    }

    [Fact]
    public async Task Cancellation_does_not_claim_success_or_quarantine_the_event()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var inbox = new StubInbox();
        var handler = new StubHandler { Cancel = true };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SourceChangeProcessor(inbox, handler).ProcessAsync(Event(), cancellation.Token));
        Assert.Equal(0, inbox.Completed);
        Assert.Equal(0, inbox.Quarantined);
    }

    [Fact]
    public void Missing_or_oversized_source_identity_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => (Event() with { SourceId = "" }).Validate());
        Assert.Throws<ArgumentException>(() => (Event() with { EventId = new string('x', 257) }).Validate());
    }

    /// <summary>Models a durable inbox without implementing database access.</summary>
    private sealed class StubInbox : ISourceChangeInbox
    {
        public ChangeClaimStatus NextStatus { get; init; } = ChangeClaimStatus.Claimed;
        public int Completed { get; private set; }
        public int Quarantined { get; private set; }
        public string? ErrorCode { get; private set; }

        /// <summary>Returns the configured claim outcome.</summary>
        public Task<ChangeClaim> TryClaimAsync(
            SourceChangeNotification change, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChangeClaim(NextStatus, NextStatus == ChangeClaimStatus.Claimed ? Guid.NewGuid() : null));

        /// <summary>Counts completed claims.</summary>
        public Task CompleteAsync(
            SourceChangeNotification change, Guid token, CancellationToken cancellationToken = default)
        {
            Completed++;
            return Task.CompletedTask;
        }

        /// <summary>Records an error category without its underlying exception text.</summary>
        public Task QuarantineAsync(
            SourceChangeNotification change, Guid token, string code,
            CancellationToken cancellationToken = default)
        {
            Quarantined++;
            ErrorCode = code;
            return Task.CompletedTask;
        }
    }

    /// <summary>Models provider re-fetch behavior with controlled failures.</summary>
    private sealed class StubHandler : ISourceChangeHandler
    {
        public int Applied { get; private set; }
        public bool Throw { get; init; }
        public bool Cancel { get; init; }

        /// <summary>Emulates a single provider source re-fetch and write.</summary>
        public Task ApplyAsync(
            SourceChangeNotification change, CancellationToken cancellationToken = default)
        {
            if (Cancel)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            Applied++;
            if (Throw)
            {
                throw new InvalidOperationException("PRIVATE ERROR DETAILS");
            }

            return Task.CompletedTask;
        }
    }
}
