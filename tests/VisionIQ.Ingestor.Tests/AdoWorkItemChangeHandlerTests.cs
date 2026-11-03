using VisionIQ.Api.Features.Backfill;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Cli;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Tests;

/// <summary>LRN-05 authenticated event-to-source re-fetch integration checks.</summary>
public sealed class AdoWorkItemChangeHandlerTests
{
    /// <summary>Creates a synthetic read-only organization configured for one project.</summary>
    private static AdoOptions Options() => new()
    {
        OrganizationUrl = new Uri("https://dev.azure.com/test-org"),
        Projects = ["Project-A"],
        PersonalAccessToken = "test-only-token",
    };

    /// <summary>Builds the provider-native work-item response.</summary>
    private static WorkItemRecord WorkItem(int revision = 7) =>
        new(123, revision, "Bug", "Closed", "Expected ticket", [], DateTimeOffset.UtcNow);

    /// <summary>Creates a scoped, trusted change notification.</summary>
    private static SourceChangeNotification Change() =>
        new("test-org/ado/Project-A", "event-1", "work_item", "123", "7");

    [Fact]
    public async Task Approved_change_is_refetched_and_archived_with_actual_revision()
    {
        var reader = new FakeReader(new ReadOutcome(WorkItem(), null));
        var sink = new FakeCanonicalSink();
        var handler = new AdoWorkItemChangeHandler(Options(), reader, sink);

        await handler.ApplyAsync(Change());

        Assert.Equal("Project-A", reader.Project);
        Assert.Equal(123, reader.Id);
        Assert.Equal("work-items", sink.Stream);
        Assert.Equal("test-org/ado/Project-A/work-item/123@7", sink.Key);
        Assert.Equal("Project-A", Assert.IsType<WorkItemRecord>(sink.Record).Project);
    }

    [Fact]
    public async Task Unknown_project_is_rejected_before_reading_any_source()
    {
        var reader = new FakeReader(new ReadOutcome(WorkItem(), null));
        var sink = new FakeCanonicalSink();
        var handler = new AdoWorkItemChangeHandler(Options(), reader, sink);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.ApplyAsync(Change() with { SourceId = "test-org/ado/Other-Project" }));
        Assert.Null(reader.Project);
        Assert.Null(sink.Record);
    }

    [Fact]
    public async Task Missing_or_stale_revision_cannot_be_acknowledged_as_success()
    {
        var sink = new FakeCanonicalSink();
        var missing = new AdoWorkItemChangeHandler(Options(),
            new FakeReader(new ReadOutcome(null, "work item omitted")), sink);
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.ApplyAsync(Change()));

        var old = new AdoWorkItemChangeHandler(Options(),
            new FakeReader(new ReadOutcome(WorkItem(6), null)), sink);
        await Assert.ThrowsAsync<InvalidOperationException>(() => old.ApplyAsync(Change()));
        Assert.Null(sink.Record);
    }

    [Fact]
    public async Task Unsupported_kind_and_malformed_native_id_are_not_processed()
    {
        var reader = new FakeReader(new ReadOutcome(WorkItem(), null));
        var handler = new AdoWorkItemChangeHandler(Options(), reader, new FakeCanonicalSink());
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            handler.ApplyAsync(Change() with { ProviderKind = "pull_request" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ApplyAsync(Change() with { NativeId = "untrusted" }));
        Assert.Null(reader.Project);
    }

    [Fact]
    public async Task Failed_canonical_archive_propagates_for_event_quarantine()
    {
        var sink = new FakeCanonicalSink { Throw = true };
        var handler = new AdoWorkItemChangeHandler(Options(),
            new FakeReader(new ReadOutcome(WorkItem(), null)), sink);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.ApplyAsync(Change()));
    }

    /// <summary>Observes which explicit item the trusted handler re-fetches.</summary>
    private sealed class FakeReader(ReadOutcome outcome) : IAdoChangedWorkItemReader
    {
        public string? Project { get; private set; }
        public int Id { get; private set; }

        /// <summary>Returns a fixture only for the requested identity.</summary>
        public Task<ReadOutcome> ReadAsync(
            string project, int id, CancellationToken cancellationToken = default)
        {
            Project = project;
            Id = id;
            return Task.FromResult(outcome);
        }
    }

    /// <summary>Models archive-first canonical persistence without source credentials.</summary>
    private sealed class FakeCanonicalSink : ICanonicalEvidenceSink
    {
        public bool Throw { get; init; }
        public string? Stream { get; private set; }
        public string? Key { get; private set; }
        public object? Record { get; private set; }

        /// <summary>Stores the submitted item or simulates a failed archive.</summary>
        public Task WriteAsync(string stream, string key, object record,
            CancellationToken cancellationToken = default)
        {
            if (Throw)
            {
                throw new InvalidOperationException("Archive unavailable");
            }

            Stream = stream;
            Key = key;
            Record = record;
            return Task.CompletedTask;
        }
    }
}
