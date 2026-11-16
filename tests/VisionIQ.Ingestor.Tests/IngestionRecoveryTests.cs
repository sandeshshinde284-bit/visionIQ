using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using VisionIQ.Api.Features.TicketFixes;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Checkpoints;
using VisionIQ.Ingestor.Cli;
using VisionIQ.Ingestor.Sources;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Tests;

/// <summary>
/// Correctness of checkpoints and recovery with more than one 25-record batch, through the real (guarded) file
/// checkpoint store: interruption and restart, --full, failed pull requests, late-merged commits, and the
/// ticket-fix projection under parallel writes.
/// </summary>
public sealed class IngestionRecoveryTests : IDisposable
{
    private const string Source = "contoso/ado";
    private const string WorkItemScope = DemoAdo.Project + "/work-items";
    private const string PullRequestScope = DemoAdo.Project + "/payments-service/pull-requests";
    private const string CommitScope = DemoAdo.Project + "/payments-service/commits";
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "visioniq-recovery-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private FileCheckpointStore Checkpoints() => new(Path.Combine(directory, "checkpoints.json"));

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- checkpoint guard

    [Fact]
    public void Only_an_older_timestamp_counts_as_moving_the_checkpoint_back()
    {
        Assert.False(TimeCursor.IsBehind("2026-01-01T00:00:00.0000000Z|10,9", "2026-01-01T00:00:00.0000000Z|9"));
        Assert.False(TimeCursor.IsBehind("2026-01-01T00:00:01.0000000Z|1", "2026-01-01T00:00:00.0000000Z|9"));
        Assert.True(TimeCursor.IsBehind("2026-01-01T00:00:00.0000000Z|9", "2026-01-01T00:00:01.0000000Z|1"));
    }

    [Fact]
    public async Task The_file_store_accepts_ids_that_join_the_same_timestamp_boundary()
    {
        // Work items 9 and 10 share a timestamp but land in different batches: "T|9" grows to "T|10,9", which
        // sorts lower as text and used to be refused as a regression, failing the stream on every run.
        var store = Checkpoints();
        await store.SaveAsync(new SyncCheckpoint(Source, WorkItemScope, "2026-01-01T00:00:00.0000000Z|9", Start));
        await store.SaveAsync(new SyncCheckpoint(Source, WorkItemScope, "2026-01-01T00:00:00.0000000Z|10,9", Start));

        Assert.Equal("2026-01-01T00:00:00.0000000Z|10,9", (await store.GetAsync(Source, WorkItemScope))!.Cursor);

        // A smaller set at the same timestamp (an overlapping run) never shrinks the stored boundary.
        await store.SaveAsync(new SyncCheckpoint(Source, WorkItemScope, "2026-01-01T00:00:00.0000000Z|9", Start));
        Assert.Equal("2026-01-01T00:00:00.0000000Z|10,9", (await store.GetAsync(Source, WorkItemScope))!.Cursor);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveAsync(new SyncCheckpoint(Source, WorkItemScope, "2025-12-31T23:59:59.0000000Z|1", Start)));
    }

    [Fact]
    public void The_database_guard_compares_only_the_timestamp_part()
    {
        Assert.Contains("split_part(evidence.connector_checkpoints.cursor_value, '|', 1) <= split_part(EXCLUDED.cursor_value, '|', 1)",
            DbCheckpointStore.UpsertSql, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- work items: more than 25, interruption

    /// <summary>Work items 1..count. Items 25 and 26 share a timestamp, straddling the first batch boundary.</summary>
    private static DateTimeOffset Changed(int id) => Start.AddSeconds(id == 26 ? 25 : id);

    private static FakeAdoHandler WorkItemServer(int count) => new FakeAdoHandler()
        .On(HttpMethod.Post, "/_apis/wit/wiql", _ => FakeAdoHandler.Ok(
            "{\"workItems\":[" + string.Join(",", Enumerable.Range(1, count).Select(id => $"{{\"id\":{id}}}")) + "]}"))
        .On(HttpMethod.Post, "/_apis/wit/workitemsbatch", request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var items = body.RootElement.GetProperty("ids").EnumerateArray().Select(element => element.GetInt32()).Select(id =>
                string.Create(CultureInfo.InvariantCulture,
                    $"{{\"id\":{id},\"rev\":1,\"fields\":{{\"System.WorkItemType\":\"Bug\",\"System.State\":\"New\",\"System.Title\":\"t\",\"System.ChangedDate\":\"{Changed(id):O}\",\"System.CreatedDate\":\"{Start:O}\"}},\"relations\":[]}}"));
            return FakeAdoHandler.Ok("{\"value\":[" + string.Join(",", items) + "]}");
        });

    private static readonly SyncRequest WorkItemsOnly = new(new HashSet<SyncKind> { SyncKind.WorkItems }, null, IncludeRevisions: false);

    private static HashSet<int> WorkItemIds(MemorySink sink) =>
        sink.Items.Where(item => item.Stream == "work-items").Select(item => ((WorkItemRecord)item.Record).Id).ToHashSet();

    [Fact]
    public async Task An_interrupted_run_keeps_completed_batches_and_the_restart_misses_nothing()
    {
        var checkpoints = Checkpoints();
        var first = new FailingWorkItemSink(failOnId: 30);

        var interrupted = await new SyncRunner(DemoAdo.Client(WorkItemServer(60)), checkpoints, first, new SourceCapabilityRegistry())
            .RunAsync(WorkItemsOnly);

        var stream = Assert.Single(interrupted.Streams);
        Assert.NotEqual(StreamStatus.Synced, stream.Status);
        var cursor = TimeCursor.Parse((await checkpoints.GetAsync(Source, WorkItemScope))!.Cursor)!;
        Assert.False(cursor.IsNew(Changed(25), "25"), "the first batch (1-25) completed and must be kept");
        Assert.True(cursor.IsNew(Changed(30), "30"), "the checkpoint must never pass the failed item");

        var second = new MemorySink();
        var resumed = await new SyncRunner(DemoAdo.Client(WorkItemServer(60)), checkpoints, second, new SourceCapabilityRegistry())
            .RunAsync(WorkItemsOnly);

        Assert.True(resumed.AllSynced);
        var everything = WorkItemIds(first.Inner);
        everything.UnionWith(WorkItemIds(second));
        Assert.Equal(Enumerable.Range(1, 60).ToHashSet(), everything);
        Assert.DoesNotContain(1, WorkItemIds(second)); // completed work is not repeated
        Assert.Contains(30, WorkItemIds(second));
    }

    [Fact]
    public async Task A_cancelled_run_saves_what_it_completed_and_resumes_from_there()
    {
        var checkpoints = Checkpoints();
        using var cancellation = new CancellationTokenSource();
        var first = new CancellingWorkItemSink(cancelOnId: 40, cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SyncRunner(DemoAdo.Client(WorkItemServer(60)), checkpoints, first, new SourceCapabilityRegistry())
                .RunAsync(WorkItemsOnly, cancellation.Token));

        var saved = await checkpoints.GetAsync(Source, WorkItemScope);
        Assert.NotNull(saved);
        var cursor = TimeCursor.Parse(saved!.Cursor)!;
        Assert.False(cursor.IsNew(Changed(25), "25"));

        var second = new MemorySink();
        var resumed = await new SyncRunner(DemoAdo.Client(WorkItemServer(60)), checkpoints, second, new SourceCapabilityRegistry())
            .RunAsync(WorkItemsOnly);

        Assert.True(resumed.AllSynced);
        var everything = WorkItemIds(first.Inner);
        everything.UnionWith(WorkItemIds(second));
        Assert.Equal(Enumerable.Range(1, 60).ToHashSet(), everything);
    }

    [Fact]
    public async Task A_full_run_rereads_everything_without_failing_on_a_newer_stored_checkpoint()
    {
        // --full used to stop after 25 records: its first batch saved an older checkpoint, the store's
        // monotonic guard refused it, and the stream failed.
        var checkpoints = Checkpoints();
        const string newer = "2027-01-01T00:00:00.0000000Z|999";
        await checkpoints.SaveAsync(new SyncCheckpoint(Source, WorkItemScope, newer, Start));
        var sink = new MemorySink();
        var full = new SyncRequest(new HashSet<SyncKind> { SyncKind.WorkItems }, null, IncludeRevisions: false, IgnoreCheckpoints: true);

        var report = await new SyncRunner(DemoAdo.Client(WorkItemServer(60)), checkpoints, sink, new SourceCapabilityRegistry())
            .RunAsync(full);

        var stream = Assert.Single(report.Streams);
        Assert.Equal(StreamStatus.Synced, stream.Status);
        Assert.Equal(60, stream.Records);
        Assert.Equal(Enumerable.Range(1, 60).ToHashSet(), WorkItemIds(sink));
        Assert.Equal(newer, (await checkpoints.GetAsync(Source, WorkItemScope))!.Cursor); // kept, not regressed
    }

    // ---------------------------------------------------------------- pull requests: a failing PR

    private static DateTimeOffset Closed(int id) => Start.AddMinutes(id);

    private static FakeAdoHandler PullRequestServer(int count, int? failingId)
    {
        var handler = new FakeAdoHandler()
            .Json(HttpMethod.Get, "/_apis/git/repositories", "repositories.json")
            .On(HttpMethod.Get, "/repositories/repo-1/pullrequests", _ => FakeAdoHandler.Ok(
                "{\"value\":[" + string.Join(",", Enumerable.Range(1, count).Select(id => string.Create(CultureInfo.InvariantCulture,
                    $"{{\"pullRequestId\":{id},\"status\":\"completed\",\"title\":\"PR {id}\",\"creationDate\":\"{Start:O}\",\"closedDate\":\"{Closed(id):O}\"}}"))) + "]}"));
        if (failingId is { } failing)
        {
            handler.On(HttpMethod.Get, "/pullRequests/" + Text(failing) + "/workitems", _ => FakeAdoHandler.Status(HttpStatusCode.NotFound));
        }

        return handler
            .On(HttpMethod.Get, "/workitems", _ => FakeAdoHandler.Ok("{\"value\":[]}"))
            .On(HttpMethod.Get, "/commits", _ => FakeAdoHandler.Ok("{\"value\":[]}"));
    }

    private static readonly SyncRequest PullRequestsOnly = new(new HashSet<SyncKind> { SyncKind.PullRequests }, null);

    private static HashSet<int> PullRequestIds(MemorySink sink) =>
        sink.Items.Where(item => item.Stream == "pull-requests").Select(item => ((PullRequestRecord)item.Record).Id).ToHashSet();

    [Fact]
    public async Task A_failing_pull_request_is_named_quarantined_and_retried_without_blocking_the_rest()
    {
        var checkpoints = Checkpoints();
        var quarantine = new DurableQuarantine();
        var first = new MemorySink();

        var report = await new SyncRunner(DemoAdo.Client(PullRequestServer(30, failingId: 27)), checkpoints, first,
            new SourceCapabilityRegistry(), quarantine: quarantine).RunAsync(PullRequestsOnly);

        var stream = Assert.Single(report.Streams);
        Assert.Equal(StreamStatus.Partial, stream.Status);
        Assert.Contains("pull request 27 failed at ADO enrichment", stream.Reason, StringComparison.Ordinal);
        Assert.Equal(1, stream.Quarantined);
        Assert.Equal(29, PullRequestIds(first).Count);
        Assert.Contains("27", await quarantine.OpenAsync(Source, PullRequestScope, "pull_request"));
        var cursor = TimeCursor.Parse((await checkpoints.GetAsync(Source, PullRequestScope))!.Cursor)!;
        Assert.False(cursor.IsNew(Closed(30), "30"), "a quarantined PR must not block the checkpoint");

        var second = new MemorySink();
        var retry = await new SyncRunner(DemoAdo.Client(PullRequestServer(30, failingId: null)), checkpoints, second,
            new SourceCapabilityRegistry(), quarantine: quarantine).RunAsync(PullRequestsOnly);

        Assert.True(retry.AllSynced);
        Assert.Equal(new HashSet<int> { 27 }, PullRequestIds(second)); // only the quarantined PR is read again
        Assert.Empty(await quarantine.OpenAsync(Source, PullRequestScope, "pull_request"));
    }

    [Fact]
    public async Task Without_a_durable_quarantine_the_checkpoint_stops_in_front_of_a_failing_pull_request()
    {
        var checkpoints = Checkpoints();

        var report = await new SyncRunner(DemoAdo.Client(PullRequestServer(30, failingId: 27)), checkpoints, new MemorySink(),
            new SourceCapabilityRegistry()).RunAsync(PullRequestsOnly);

        var stream = Assert.Single(report.Streams);
        Assert.Equal(StreamStatus.Partial, stream.Status);
        Assert.Contains("pull request 27 failed at ADO enrichment", stream.Reason, StringComparison.Ordinal);
        var cursor = TimeCursor.Parse((await checkpoints.GetAsync(Source, PullRequestScope))!.Cursor)!;
        Assert.False(cursor.IsNew(Closed(26), "26"));
        Assert.True(cursor.IsNew(Closed(27), "27"));

        var second = new MemorySink();
        var retry = await new SyncRunner(DemoAdo.Client(PullRequestServer(30, failingId: null)), checkpoints, second,
            new SourceCapabilityRegistry()).RunAsync(PullRequestsOnly);

        Assert.True(retry.AllSynced);
        Assert.Equal(new HashSet<int> { 27, 28, 29, 30 }, PullRequestIds(second));
    }

    // ---------------------------------------------------------------- commits merged later

    private static string Sha(char c) => new(c, 40);

    private static FakeAdoHandler CommitServer(params (string Id, DateTimeOffset Date)[] commits) => new FakeAdoHandler()
        .Json(HttpMethod.Get, "/_apis/git/repositories", "repositories.json")
        .On(HttpMethod.Get, "/repositories/repo-1/commits", _ => FakeAdoHandler.Ok(
            "{\"value\":[" + string.Join(",", commits.Select(commit => string.Create(CultureInfo.InvariantCulture,
                $"{{\"commitId\":\"{commit.Id}\",\"comment\":\"c\",\"commentTruncated\":false,\"author\":{{\"date\":\"{commit.Date:O}\"}},\"committer\":{{\"date\":\"{commit.Date:O}\"}}}}"))) + "]}"))
        .Json(HttpMethod.Get, "/changes", "commit-changes.json");

    private static readonly SyncRequest CommitsOnly = new(new HashSet<SyncKind> { SyncKind.Commits }, null);

    [Fact]
    public async Task A_commit_merged_after_the_checkpoint_passed_its_date_is_still_ingested()
    {
        var checkpoints = Checkpoints();
        var a = (Sha('a'), Start.AddHours(9));
        var b = (Sha('b'), Start.AddHours(10));
        var first = new MemorySink();
        await new SyncRunner(DemoAdo.Client(CommitServer(a, b)), checkpoints, first, new SourceCapabilityRegistry())
            .RunAsync(CommitsOnly);
        Assert.Equal(Start.AddHours(10), TimeCursor.Parse((await checkpoints.GetAsync(Source, CommitScope))!.Cursor)!.At);

        // A feature-branch commit made at 09:30 is merged now: its committer date is older than the checkpoint.
        var late = (Sha('c'), Start.AddHours(9.5));
        var second = new MemorySink { StoredCommitIds = first.CommitIds().ToHashSet(StringComparer.Ordinal) };
        var report = await new SyncRunner(DemoAdo.Client(CommitServer(a, late, b)), checkpoints, second, new SourceCapabilityRegistry())
            .RunAsync(CommitsOnly);

        Assert.True(report.AllSynced);
        Assert.Equal([Sha('c')], second.CommitIds()); // only the late commit; stored ones are skipped
        Assert.Equal(Start.AddHours(10), TimeCursor.Parse((await checkpoints.GetAsync(Source, CommitScope))!.Cursor)!.At);
    }

    // ---------------------------------------------------------------- ticket-fix projection

    private static readonly Uri Organization = new("https://dev.azure.com/contoso");

    private static PullRequestRecord Pr(int id, string title, string sha) =>
        new(id, "repo-id", "repo", "Project-A", "completed", title, Start)
        {
            WorkItemIds = [1000 + id],
            CommitIds = [sha],
            MergeCommitId = sha,
            ClosedDate = Start.AddMinutes(id),
        };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_title_revert_is_recorded_whichever_pull_request_arrives_first(bool revertFirst)
    {
        var links = new ConcurrentLinkStore();
        var sink = new TicketFixProjectionSink(new NullEvidenceSink(), links, Organization);
        var original = Pr(1, "Enable alarm", Sha('1'));
        var revert = Pr(2, "Revert \"Enable alarm\"", Sha('2'));

        foreach (var pr in revertFirst ? new[] { revert, original } : [original, revert])
        {
            await sink.WriteAsync("pull-requests", "pr/" + Text(pr.Id), pr);
        }

        var candidate = Assert.Single(links.Candidates);
        Assert.Equal(("1001", Sha('1')), (candidate.TicketId, candidate.CommitSha));
        Assert.Contains(candidate.SourceEvidenceRefs, reference =>
            reference.StartsWith("candidate-revert:pr-title-match:pr2:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_projection_is_safe_under_parallel_writes()
    {
        var links = new ConcurrentLinkStore();
        var sink = new TicketFixProjectionSink(new NullEvidenceSink(), links, Organization);
        var prs = Enumerable.Range(1, 200)
            .Select(id => Pr(id, id % 2 == 1 ? "Change " + Text(id) : "Revert \"Change " + Text(id - 1) + "\"",
                new string((char)('a' + (id % 6)), 39) + (id % 10).ToString(CultureInfo.InvariantCulture)))
            .ToList();

        await Parallel.ForEachAsync(prs, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (pr, token) => await sink.WriteAsync("pull-requests", "pr/" + Text(pr.Id), pr, token));

        // Each of the 100 originals is matched with its revert exactly once, however the writes interleaved.
        Assert.Equal(100, links.Candidates.Count);
        Assert.Equal(100, links.Candidates.Select(claim => claim.PullRequestId).Distinct().Count());
    }

    // ---------------------------------------------------------------- test doubles

    /// <summary>Fails the first write of one work item, then behaves (a transient outage).</summary>
    private sealed class FailingWorkItemSink(int failOnId) : IRecordSink
    {
        private int failed;

        public MemorySink Inner { get; } = new();

        public Task WriteAsync(string stream, string key, object record, CancellationToken cancellationToken = default)
        {
            if (record is WorkItemRecord item && item.Id == failOnId && Interlocked.Exchange(ref failed, 1) == 0)
            {
                throw new InvalidOperationException("injected outage");
            }

            return Inner.WriteAsync(stream, key, record, cancellationToken);
        }
    }

    /// <summary>Cancels the run when it reaches one work item (an operator stopping the process).</summary>
    private sealed class CancellingWorkItemSink(int cancelOnId, CancellationTokenSource cancellation) : IRecordSink
    {
        public MemorySink Inner { get; } = new();

        public Task WriteAsync(string stream, string key, object record, CancellationToken cancellationToken = default)
        {
            if (record is WorkItemRecord item && item.Id == cancelOnId)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Inner.WriteAsync(stream, key, record, cancellationToken);
        }
    }

    /// <summary>A quarantine that survives between runs, like the PostgreSQL one.</summary>
    private sealed class DurableQuarantine : ISyncQuarantine
    {
        private readonly ConcurrentDictionary<(string, string, string, string), bool> open = new();

        public Task<IReadOnlySet<string>> OpenAsync(string sourceId, string scope, string itemKind, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(open.Keys
                .Where(key => key.Item1 == sourceId && key.Item2 == scope && key.Item3 == itemKind)
                .Select(key => key.Item4).ToHashSet(StringComparer.Ordinal));

        public Task AddAsync(QuarantinedItem item, CancellationToken cancellationToken = default)
        {
            open[(item.SourceId, item.Scope, item.ItemKind, item.NativeId)] = true;
            return Task.CompletedTask;
        }

        public Task ResolveAsync(string sourceId, string scope, string itemKind, string nativeId, DateTimeOffset resolvedAt, CancellationToken cancellationToken = default)
        {
            open.TryRemove((sourceId, scope, itemKind, nativeId), out _);
            return Task.CompletedTask;
        }
    }

    private sealed class NullEvidenceSink : ICanonicalEvidenceSink
    {
        public Task WriteAsync(string stream, string key, object record, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>Thread-safe recorder of the link-store calls the sink makes.</summary>
    private sealed class ConcurrentLinkStore : ITicketFixLinkStore
    {
        private readonly ConcurrentQueue<TicketFixClaim> candidates = new();

        public IReadOnlyList<TicketFixClaim> Candidates => candidates.ToList();

        public Task<string> UpsertSourceAsync(TicketFixClaim claim, CancellationToken cancellationToken = default) =>
            Task.FromResult(claim.AssociationKey());

        public Task<string> UpsertCandidateEvidenceAsync(TicketFixClaim claim, CancellationToken cancellationToken = default)
        {
            candidates.Enqueue(claim);
            return Task.FromResult(claim.AssociationKey());
        }

        public Task<int> RecordRevertAsync(string sourceInstance, string projectId, string repositoryId,
            string originalCommitSha, string revertCommitSha, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task ReconcileSourceAsync(string sourceItemKey, string sourceRevisionId, DateTimeOffset sourceRevisionTime,
            bool isCompleteSnapshot, IReadOnlyList<TicketFixClaim> claims, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
