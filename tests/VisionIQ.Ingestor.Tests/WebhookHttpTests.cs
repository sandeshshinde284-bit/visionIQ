using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using VisionIQ.Api.Features.Backfill;
using VisionIQ.Ingestor.Cli;

namespace VisionIQ.Ingestor.Tests;

/// <summary>
/// Second-pass review (ASP0016): the webhook's status codes must reach the caller over real HTTP. Mapping the
/// handler as a method group used to discard its result and always answer 200, so ADO would never retry.
/// </summary>
public sealed class WebhookHttpTests
{
    private const string Secret = "a-very-long-shared-secret-for-tests-only-1234";
    private const string Updated = """
        {"id":"evt-1","eventType":"workitem.updated","resource":{"id":9,"workItemId":1234,"rev":9,
          "url":"https://dev.azure.com/contoso/_apis/wit/workItems/1234/updates/9",
          "revision":{"id":1234,"rev":9,"fields":{"System.TeamProject":"VisionIQ Demo"}}}}
        """;

    private const string PullRequestUpdated = """
        {"id":"evt-pr-1","eventType":"git.pullrequest.updated","resource":{
          "pullRequestId":42,"status":"active",
          "url":"https://dev.azure.com/contoso/_apis/git/repositories/repo-1/pullRequests/42",
          "repository":{"id":"repo-1","name":"shop","project":{"name":"VisionIQ Demo"}}}}
        """;

    private sealed class Inbox : ISourceChangeInbox
    {
        public Task<ChangeClaim> TryClaimAsync(SourceChangeNotification change, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChangeClaim(ChangeClaimStatus.Claimed, Guid.NewGuid()));

        public Task CompleteAsync(SourceChangeNotification change, Guid token, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task QuarantineAsync(SourceChangeNotification change, Guid token, string code, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Handler(bool fail) : ISourceChangeHandler
    {
        public Task ApplyAsync(SourceChangeNotification change, CancellationToken cancellationToken = default) =>
            fail ? throw new InvalidOperationException("source outage") : Task.CompletedTask;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<HttpStatusCode> PostOverHttp(bool fail, string secret, bool pullRequest = false)
    {
        var port = FreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        await using var app = builder.Build();
        WebhookHost.Map(app, new AdoWebhookEndpoint(new WebhookCredentials("ado-hooks", Secret),
            new SourceChangeProcessor(new Inbox(), new Handler(fail)), new Uri("https://dev.azure.com/contoso"), ["VisionIQ Demo"], "contoso"));
        await app.StartAsync();
        try
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{(pullRequest ? AdoWebhookEndpoint.PullRequestRoute : AdoWebhookEndpoint.Route)}")
            {
                Content = new StringContent(pullRequest ? PullRequestUpdated : Updated, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("ado-hooks:" + secret)));
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task A_processed_event_is_200_over_http() =>
        Assert.Equal(HttpStatusCode.OK, await PostOverHttp(fail: false, Secret));

    [Fact]
    public async Task A_failed_attempt_is_500_over_http_so_ado_retries() =>
        Assert.Equal(HttpStatusCode.InternalServerError, await PostOverHttp(fail: true, Secret));

    [Fact]
    public async Task Pr_service_hook_route_acknowledges_processed_event_over_real_http() =>
        Assert.Equal(HttpStatusCode.OK, await PostOverHttp(fail: false, Secret, pullRequest: true));

    [Fact]
    public async Task Pr_service_hook_route_retries_handler_failure_over_real_http() =>
        Assert.Equal(HttpStatusCode.InternalServerError, await PostOverHttp(fail: true, Secret, pullRequest: true));

    [Fact]
    public async Task Pr_service_hook_route_rejects_wrong_credentials() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await PostOverHttp(fail: false,
            "wrong-secret-wrong-secret-wrong-secret", pullRequest: true));

    [Fact]
    public async Task Wrong_credentials_are_401_over_http() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await PostOverHttp(fail: false, "wrong-secret-wrong-secret-wrong-secret"));
}
