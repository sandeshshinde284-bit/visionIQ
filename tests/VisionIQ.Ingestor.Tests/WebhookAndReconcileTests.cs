using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using VisionIQ.Api.Features.Backfill;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Checkpoints;
using VisionIQ.Ingestor.Cli;
using VisionIQ.Ingestor.Configuration;
using VisionIQ.Ingestor.Defects;
using VisionIQ.Ingestor.Sources;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Tests;

/// <summary>LRN-05: webhook parsing, authentication, retry semantics, the reconciliation sweep and N-8.</summary>
public sealed class WebhookAndReconcileTests
{
    private static readonly Uri Org = new("https://dev.azure.com/contoso");
    private static readonly string[] Projects = ["VisionIQ Demo"];
    private const string Secret = "a-very-long-shared-secret-for-tests-only-1234";

    private const string UpdatedTemplate = """
        {"id":"evt-1","eventType":"TYPE","resource":{"id":9,"workItemId":1234,"rev":9,"url":"URL",
          "revision":{"id":1234,"rev":9,"fields":{"System.TeamProject":"PROJECT","System.Title":"ignored, never trusted"}}}}
        """;

    private static string Updated(string url = "https://dev.azure.com/contoso/_apis/wit/workItems/1234/updates/9", string project = "VisionIQ Demo", string type = "workitem.updated") =>
        UpdatedTemplate.Replace("TYPE", type, StringComparison.Ordinal).Replace("URL", url, StringComparison.Ordinal).Replace("PROJECT", project, StringComparison.Ordinal);

    private static ServiceHookOutcome Parse(string json, out SourceChangeNotification? change)
    {
        using var document = JsonDocument.Parse(json);
        return AdoServiceHook.Parse(document.RootElement, Org, Projects, "contoso", out change);
    }

    [Fact]
    public void An_updated_work_item_event_becomes_a_notification_to_refetch()
    {
        Assert.Equal(ServiceHookOutcome.Accepted, Parse(Updated(), out var change));
        Assert.Equal(new SourceChangeNotification("contoso/ado/VisionIQ Demo", "evt-1", "work_item", "1234", "9"), change);
    }

    [Fact]
    public void A_created_work_item_event_is_accepted()
    {
        const string Created = """
            {"id":"evt-2","eventType":"workitem.created","resource":{"id":77,"rev":1,
              "url":"https://dev.azure.com/contoso/_apis/wit/workItems/77","fields":{"System.TeamProject":"VisionIQ Demo"}}}
            """;

        Assert.Equal(ServiceHookOutcome.Accepted, Parse(Created, out var change));
        Assert.Equal(("77", "1"), (change!.NativeId, change.SourceRevision));
    }

    [Theory]
    [InlineData("https://dev.azure.com/other-org/_apis/wit/workItems/1234/updates/9", "VisionIQ Demo", "workitem.updated")]
    [InlineData("https://dev.azure.com/contoso/_apis/wit/workItems/1234/updates/9", "Secret Project", "workitem.updated")]
    [InlineData("https://dev.azure.com/contoso/_apis/wit/workItems/1234/updates/9", "VisionIQ Demo", "git.push")]
    public void Events_for_other_organizations_projects_or_types_are_ignored(string url, string project, string type) =>
        Assert.Equal(ServiceHookOutcome.Ignored, Parse(Updated(url, project, type), out _));

    [Theory]
    [InlineData("""{"eventType":"workitem.updated","resource":{}}""")]
    [InlineData("""{"id":"e","eventType":"workitem.updated","resource":{"workItemId":1,"url":"https://dev.azure.com/contoso/x"}}""")]
    [InlineData("""[]""")]
    public void Malformed_events_are_invalid(string json) =>
        Assert.Equal(ServiceHookOutcome.Invalid, Parse(json, out _));

    private static string Basic(string user, string secret) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + secret));

    [Fact]
    public void Only_the_exact_configured_credentials_are_accepted()
    {
        var credentials = new WebhookCredentials("ado-hooks", Secret);

        Assert.True(credentials.IsValid(Basic("ado-hooks", Secret)));
        Assert.False(credentials.IsValid(Basic("ado-hooks", Secret + "x")));
        Assert.False(credentials.IsValid(Basic("someone", Secret)));
        Assert.False(credentials.IsValid("Bearer " + Secret));
        Assert.False(credentials.IsValid("Basic not-base64!!"));
        Assert.False(credentials.IsValid(null));
    }

    [Fact]
    public void Weak_or_malformed_credential_configuration_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => new WebhookCredentials("ado-hooks", "short"));
        Assert.Throws<InvalidOperationException>(() => new WebhookCredentials("a:b", Secret));
    }

    private sealed class Inbox(ChangeClaimStatus status) : ISourceChangeInbox
    {
        public string? Outcome { get; private set; }

        public Task<ChangeClaim> TryClaimAsync(SourceChangeNotification change, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChangeClaim(status, status == ChangeClaimStatus.Claimed ? Guid.NewGuid() : null));

        public Task CompleteAsync(SourceChangeNotification change, Guid token, CancellationToken cancellationToken = default)
        {
            Outcome = "completed";
            return Task.CompletedTask;
        }

        public Task QuarantineAsync(SourceChangeNotification change, Guid token, string code, CancellationToken cancellationToken = default)
        {
            Outcome = "quarantined";
            return Task.CompletedTask;
        }
    }

    private sealed class Handler(bool fail) : ISourceChangeHandler
    {
        public int Calls { get; private set; }

        public Task ApplyAsync(SourceChangeNotification change, CancellationToken cancellationToken = default)
        {
            Calls++;
            return fail ? throw new InvalidOperationException("source outage") : Task.CompletedTask;
        }
    }

    private static async Task<int> Post(string body, ChangeClaimStatus claim = ChangeClaimStatus.Claimed, bool fail = false, string? authorization = null, Handler? handler = null)
    {
        handler ??= new Handler(fail);
        var endpoint = new AdoWebhookEndpoint(new WebhookCredentials("ado-hooks", Secret),
            new SourceChangeProcessor(new Inbox(claim), handler), Org, Projects, "contoso");
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = authorization ?? Basic("ado-hooks", Secret);
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var result = await endpoint.HandleAsync(context);
        return ((IStatusCodeHttpResult)result).StatusCode ?? 200;
    }

    [Fact]
    public async Task A_processed_event_is_200() => Assert.Equal(200, await Post(Updated()));

    [Fact]
    public async Task A_failed_attempt_is_500_so_ado_retries() => Assert.Equal(500, await Post(Updated(), fail: true));

    [Fact]
    public async Task A_dead_lettered_event_is_200_so_ado_stops_retrying() =>
        Assert.Equal(200, await Post(Updated(), ChangeClaimStatus.DeadLettered));

    [Fact]
    public async Task A_concurrent_attempt_is_503() => Assert.Equal(503, await Post(Updated(), ChangeClaimStatus.InProgress));

    [Fact]
    public async Task Wrong_credentials_are_401_and_nothing_is_processed()
    {
        var handler = new Handler(false);

        Assert.Equal(401, await Post(Updated(), authorization: Basic("ado-hooks", "wrong-secret-wrong-secret-wrong-secret"), handler: handler));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Invalid_json_is_400_and_ignored_events_are_200_without_processing()
    {
        var handler = new Handler(false);

        Assert.Equal(400, await Post("{not json", handler: handler));
        Assert.Equal(200, await Post(Updated(type: "git.push"), handler: handler));
        Assert.Equal(0, handler.Calls);
    }

    // --- Scheduled reconciliation sweep ------------------------------------------------------------

    [Fact]
    public void Reconcile_days_reads_a_recent_window_ignoring_checkpoints()
    {
        var options = CommandLine.Parse(["sync", "--reconcile-days", "7"]);

        Assert.Null(options.Error);
        Assert.True(options.Reconcile && options.Full);
        var age = DateTimeOffset.UtcNow - options.Since!.Value;
        Assert.True(age > TimeSpan.FromDays(6.99) && age < TimeSpan.FromDays(7.01), $"window was {age}");
        Assert.NotNull(CommandLine.Parse(["sync", "--reconcile-days", "0"]).Error);
        Assert.NotNull(CommandLine.Parse(["sync", "--reconcile-days", "91"]).Error);
    }

    [Fact]
    public async Task A_reconciliation_sweep_never_moves_the_delta_checkpoint()
    {
        var checkpoints = new InMemoryCheckpointStore();
        var runner = new SyncRunner(DemoAdo.Client(DemoAdo.Server()), checkpoints, new MemorySink(), new SourceCapabilityRegistry());

        var report = await runner.RunAsync(new SyncRequest(SyncRequest.AllKinds, DateTimeOffset.UnixEpoch, IgnoreCheckpoints: true) { Reconcile = true });

        Assert.True(report.AllSynced);
        Assert.Null(await checkpoints.GetAsync("contoso/ado", "VisionIQ Demo/work-items"));
    }

    // --- N-8: the webhook path classifies environment exactly like the sync ---------------------------

    private sealed class Capture : ICanonicalEvidenceSink
    {
        public List<object> Records { get; } = [];

        public Task WriteAsync(string stream, string key, object record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Webhook_updates_use_the_same_environment_rules_as_the_sync()
    {
        const string Batch = """
            {"value":[{"id":1234,"rev":9,"fields":{"System.WorkItemType":"Bug","System.State":"Active","System.Title":"t",
              "System.ChangedDate":"2026-01-01T00:00:00Z","System.CreatedDate":"2026-01-01T00:00:00Z","Custom.Env":"Production"},"relations":[]}]}
            """;
        var classifier = DefectEnvironmentClassifier.FromJson(
            """{"providers":{"azure_devops":{"projects":{"VisionIQ Demo":{"sources":[{"type":"field","name":"Custom.Env"}],"values":{"PROD":["Production"]}}}}}}""",
            new Dictionary<string, IReadOnlyCollection<string>> { ["azure_devops"] = Projects });
        var client = DemoAdo.Client(new FakeAdoHandler().On(HttpMethod.Post, "/_apis/wit/workitemsbatch", _ => FakeAdoHandler.Ok(Batch)));
        var capture = new Capture();
        var handler = new AdoWorkItemChangeHandler(DemoAdo.Options(), new AdoChangedWorkItemReader(new AdoWorkItemSource(client, classifier)), capture);

        await handler.ApplyAsync(new SourceChangeNotification("contoso/ado/VisionIQ Demo", "evt-1", "work_item", "1234", "9"));

        var item = Assert.IsType<WorkItemRecord>(Assert.Single(capture.Records));
        Assert.Equal(("PROD", "verified"), (item.Environment, item.EnvironmentStatus));
    }
}
