using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using VisionIQ.Api.Features.Backfill;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Checkpoints;
using VisionIQ.Ingestor.Cli;
using VisionIQ.Ingestor.Confluence;
using VisionIQ.Ingestor.Sources;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Tests;

/// <summary>Collects formatted log lines with level and scopes.</summary>
public sealed class LogCapture : ILogger
{
    private readonly List<object> scopes = [];

    public List<(LogLevel Level, string Message, string Scopes)> Lines { get; } = [];

    /// <summary>Everything that would reach a log sink, for leak assertions.</summary>
    public string All => string.Join("\n", Lines.Select(l => $"{l.Level} {l.Message} {l.Scopes}"));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        scopes.Add(state);
        return new Pop(scopes);
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add((logLevel, formatter(state, exception), string.Join(";", scopes.Select(s =>
            s is IEnumerable<KeyValuePair<string, object>> pairs ? string.Join(",", pairs.Select(p => p.Key + "=" + p.Value)) : s.ToString()))));

    private sealed class Pop(List<object> scopes) : IDisposable
    {
        public void Dispose() => scopes.RemoveAt(scopes.Count - 1);
    }
}

/// <summary>Logging phase 2: every source call, sync run, stream and webhook event is logged; nothing sensitive is.</summary>
public sealed class IngestorLoggingTests
{
    private const string Pat = "test-token-not-real";

    private static AdoClient Client(FakeAdoHandler handler, LogCapture log) =>
        new(new HttpClient(handler), DemoAdo.Options(), (_, _) => Task.CompletedTask, log);

    private static void NoSecrets(LogCapture log)
    {
        Assert.DoesNotContain(Pat, log.All, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + Pat)), log.All, StringComparison.Ordinal); // Authorization header
        Assert.DoesNotContain("api-version", log.All, StringComparison.Ordinal); // query strings are never logged
        Assert.DoesNotContain("continuationToken", log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transient_failure_logs_a_retry_then_the_successful_call_with_the_path_only()
    {
        var calls = 0;
        var handler = new FakeAdoHandler().On(HttpMethod.Get, "/_apis/projects", _ =>
            ++calls == 1 ? FakeAdoHandler.Status(HttpStatusCode.ServiceUnavailable) : FakeAdoHandler.Ok("{}"));
        var log = new LogCapture();
        var client = Client(handler, log);

        await client.GetAsync(client.BuildUri(null, "_apis/projects", [new("continuationToken", "abc")]));

        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("ADO GET /contoso/_apis/projects attempt 1 failed (HTTP 503)", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Debug && l.Message.Contains("responded 200", StringComparison.Ordinal) && l.Message.Contains("(attempt 2)", StringComparison.Ordinal));
        NoSecrets(log);
    }

    [Fact]
    public async Task A_timeout_is_logged_as_a_retry_cause()
    {
        var calls = 0;
        var handler = new FakeAdoHandler().On(HttpMethod.Get, "/_apis/projects", _ =>
            ++calls == 1 ? throw new TaskCanceledException("timeout") : FakeAdoHandler.Ok("{}"));
        var log = new LogCapture();
        var client = Client(handler, log);

        await client.GetAsync(client.BuildUri(null, "_apis/projects"));

        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("failed (timeout)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_final_failure_is_an_error_with_the_status_and_no_secrets()
    {
        var log = new LogCapture();
        var client = Client(new FakeAdoHandler().On(HttpMethod.Get, "/_apis/projects", _ => FakeAdoHandler.Status(HttpStatusCode.Unauthorized)), log);

        await Assert.ThrowsAsync<AdoSourceException>(() => client.GetAsync(client.BuildUri(null, "_apis/projects")));

        Assert.Contains(log.Lines, l => l.Level == LogLevel.Error && l.Message.Contains("failed after 1 attempt(s): HTTP 401", StringComparison.Ordinal));
        NoSecrets(log);
    }

    [Fact]
    public async Task A_sync_run_logs_its_run_id_streams_checkpoints_quarantine_and_deletions_but_never_content()
    {
        var log = new LogCapture();
        var server = DemoAdo.Server()
            .On(HttpMethod.Get, "/_apis/wit/recyclebin", _ => FakeAdoHandler.Ok("""{"value":[{"id":77}]}"""));
        var request = new SyncRequest(new HashSet<SyncKind> { SyncKind.WorkItems }, null, IncludeRevisions: false);

        var report = await new SyncRunner(Client(server, log), new InMemoryCheckpointStore(), new MemorySink(), new SourceCapabilityRegistry(), logger: log).RunAsync(request);

        Assert.Equal(request.RunId, report.RunId);
        Assert.Contains(log.Lines, l => l.Message.StartsWith($"Sync run {request.RunId} started", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Message.StartsWith($"Sync run {request.RunId} finished", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Message.StartsWith("Stream WorkItems VisionIQ Demo/work-items: Synced, 2 stored", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Message.Contains("Checkpoint VisionIQ Demo/work-items: loaded none (first run)", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Message.Contains("Checkpoint VisionIQ Demo/work-items: saved", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Message.Contains("1 deleted work item(s) recorded", StringComparison.Ordinal));
        Assert.All(log.Lines.Where(l => l.Message.StartsWith("Stream", StringComparison.Ordinal)), l => Assert.Contains("runId=" + request.RunId, l.Scopes, StringComparison.Ordinal));
        Assert.DoesNotContain("Retry loop never stops", log.All, StringComparison.Ordinal); // work-item titles are content
        NoSecrets(log);
    }

    [Fact]
    public async Task A_quarantined_item_is_a_warning()
    {
        var log = new LogCapture();
        const string Item = """{"id":1,"rev":1,"fields":{"System.WorkItemType":"Bug","System.State":"New","System.Title":"SECRET TITLE","System.ChangedDate":"2026-01-01T00:00:00Z","System.CreatedDate":"2026-01-01T00:00:00Z"},"relations":[]}""";
        var server = new FakeAdoHandler()
            .On(HttpMethod.Post, "/_apis/wit/wiql", _ => FakeAdoHandler.Ok("""{"workItems":[{"id":1},{"id":2}]}"""))
            .On(HttpMethod.Post, "/_apis/wit/workitemsbatch", _ => FakeAdoHandler.Ok("{\"value\":[" + Item + "]}"));

        await new SyncRunner(Client(server, log), new InMemoryCheckpointStore(), new MemorySink(), new SourceCapabilityRegistry(), logger: log)
            .RunAsync(new SyncRequest(new HashSet<SyncKind> { SyncKind.WorkItems }, null, IncludeRevisions: false));

        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("Quarantined work_item 2", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Message.StartsWith("Stream WorkItems", StringComparison.Ordinal));
        Assert.DoesNotContain("SECRET TITLE", log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confluence_calls_log_the_path_only_and_never_the_token()
    {
        var log = new LogCapture();
        var source = ConfluenceSources.Create(new HttpClient(DemoConfluence.CloudServer()), DemoConfluence.CloudOptions(), (_, _) => Task.CompletedTask, log);

        await source.GetPagesAsync("ENG");

        Assert.Contains(log.Lines, l => l.Message.StartsWith("Confluence GET /wiki/", StringComparison.Ordinal));
        Assert.DoesNotContain("conf-token-not-real", log.All, StringComparison.Ordinal);
        Assert.DoesNotContain("cursor=", log.All, StringComparison.Ordinal);
    }

    private const string Secret = "a-very-long-shared-secret-for-tests-only-1234";

    private sealed class Inbox : ISourceChangeInbox
    {
        public Task<ChangeClaim> TryClaimAsync(SourceChangeNotification change, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChangeClaim(ChangeClaimStatus.Claimed, Guid.NewGuid()));

        public Task CompleteAsync(SourceChangeNotification change, Guid token, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task QuarantineAsync(SourceChangeNotification change, Guid token, string code, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Handler : ISourceChangeHandler
    {
        public Task ApplyAsync(SourceChangeNotification change, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private const string EventTemplate = """
        {"id":"evt-7","eventType":"TYPE","resource":{"id":9,"workItemId":1234,"rev":9,
          "url":"https://dev.azure.com/contoso/_apis/wit/workItems/1234/updates/9",
          "revision":{"rev":9,"fields":{"System.TeamProject":"VisionIQ Demo","System.Title":"SECRET TITLE"}}}}
        """;

    private static async Task Post(LogCapture log, string secret, string eventType)
    {
        var endpoint = new AdoWebhookEndpoint(new WebhookCredentials("ado-hooks", Secret), new SourceChangeProcessor(new Inbox(), new Handler()),
            new Uri("https://dev.azure.com/contoso"), ["VisionIQ Demo"], "contoso", log);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ado-hooks:" + secret));
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(EventTemplate.Replace("TYPE", eventType, StringComparison.Ordinal)));
        await endpoint.HandleAsync(context);
    }

    [Fact]
    public async Task Webhook_events_are_logged_with_outcome_and_never_the_secret_or_content()
    {
        var log = new LogCapture();

        await Post(log, Secret, "workitem.updated");
        await Post(log, Secret, "git.push");
        await Post(log, "wrong-secret-wrong-secret-wrong-secret-wrong", "workitem.updated");

        Assert.Contains(log.Lines, l => l.Message.StartsWith("Webhook event evt-7 work_item VisionIQ Demo/1234 rev 9: Completed, HTTP 200", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Message.StartsWith("Webhook event evt-7 git.push ignored", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("invalid or missing credentials", StringComparison.Ordinal));
        Assert.DoesNotContain(Secret, log.All, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-secret", log.All, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET TITLE", log.All, StringComparison.Ordinal);
    }
}
