using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using VisionIQ.Api.Features.Pickup;
using static VisionIQ.Api.Tests.PickupTestData;

namespace VisionIQ.Api.Tests;

/// <summary>S04-02: POST /webhooks/ado is verified, stored once and acknowledged without doing the analysis.</summary>
public sealed class PickupWebhookEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly string policyFile = WritePolicy();

    [Fact]
    public async Task An_eligible_pickup_is_stored_once_and_acknowledged()
    {
        var store = new FakePickupStore();
        using var client = CreateClient(store);

        var first = await PostAsync(client, Updated());
        var again = await PostAsync(client, Updated()); // ADO retries with the same event id

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal("queued", (await first.Content.ReadFromJsonAsync<PickupResponse>())!.Status);
        Assert.Equal("duplicate", (await again.Content.ReadFromJsonAsync<PickupResponse>())!.Status);
        var pickup = Assert.Single(store.Pickups);
        Assert.Equal((PickupTrigger.AdoWebhook, "8b6e2c3a-0000-4000-8000-000000000001", "payments-core", "4711", 7),
            (pickup.Trigger, pickup.DeliveryId, pickup.ProjectId, pickup.TicketId, pickup.TicketRevision));
        Assert.Matches("^[0-9a-f]{64}$", pickup.PayloadSha256!);
        Assert.Empty(store.Runs); // the run is started by the worker, not on the request thread
    }

    [Fact]
    public async Task Changes_that_are_not_pickups_are_acknowledged_and_ignored()
    {
        var store = new FakePickupStore();
        using var client = CreateClient(store);

        var response = await PostAsync(client, Updated(oldState: "In Progress"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(new PickupResponse("ignored", "no_pickup_transition"), await response.Content.ReadFromJsonAsync<PickupResponse>());
        Assert.Empty(store.Pickups);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer test-only-webhook-secret-0123456789")]
    [InlineData("Basic not-base64!")]
    [InlineData("wrong")]
    public async Task Requests_without_the_shared_secret_are_refused(string? credentials)
    {
        var store = new FakePickupStore();
        using var client = CreateClient(store);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/ado") { Content = Json(Updated()) };
        if (credentials == "wrong")
        {
            request.Headers.Authorization = Basic("visioniq", "not-the-secret");
        }
        else if (credentials is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", credentials);
        }

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(store.Pickups);
    }

    [Fact]
    public async Task Unusable_bodies_are_rejected_but_other_event_types_are_not_errors()
    {
        using var client = CreateClient(new FakePickupStore());

        var broken = await PostAsync(client, new JsonObject { ["id"] = "e", ["eventType"] = "workitem.updated", ["resource"] = new JsonObject() });
        var push = await PostAsync(client, new JsonObject { ["id"] = "e", ["eventType"] = "git.push", ["resource"] = new JsonObject() });

        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, push.StatusCode);
    }

    [Fact]
    public async Task A_storage_outage_asks_ADO_to_retry()
    {
        using var client = CreateClient(new FakePickupStore { Failure = new TimeoutException() });

        var response = await PostAsync(client, Updated());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_bodies_are_refused()
    {
        using var client = CreateClient(new FakePickupStore());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/ado")
        {
            Content = new StringContent(new string(' ', PickupWebhookEndpoints.MaxBodyBytes + 1), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = Basic("visioniq", Secret);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_policy_the_webhook_does_not_exist()
    {
        using var client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("DB_API_USER", "visioniq_api");
            builder.UseSetting("DB_API_PASSWORD", "test-only-api-password");
        }).CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(client, Updated())).StatusCode);
    }

    [Fact]
    public void A_policy_without_a_strong_secret_stops_start_up()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PICKUP_POLICY_FILE"] = "policy.json", ["PICKUP_WEBHOOK_SECRET"] = "short" })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() => PickupOptions.FromConfiguration(configuration, _ => PolicyJson));

        Assert.Contains("PICKUP_WEBHOOK_SECRET", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PICKUP_WORKER_INTERVAL_SECONDS", "0")]
    [InlineData("PICKUP_SWEEP_INTERVAL_MINUTES", "-1")]
    [InlineData("PICKUP_SWEEP_LOOKBACK_HOURS", "lots")]
    [InlineData("PICKUP_SWEEP_INTERVAL_MINUTES", "1440")] // a 24 h lookback would not overlap daily sweeps
    public void Out_of_range_timings_stop_start_up(string name, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PICKUP_POLICY_FILE"] = "policy.json",
                ["PICKUP_WEBHOOK_SECRET"] = Secret,
                [name] = value,
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => PickupOptions.FromConfiguration(configuration, _ => PolicyJson));
    }

    [Fact]
    public void Only_the_exact_secret_is_accepted()
    {
        Assert.True(PickupWebhookEndpoints.IsAuthorized(Basic("anyone", Secret).ToString(), Secret));
        Assert.True(PickupWebhookEndpoints.IsAuthorized(Basic(string.Empty, Secret).ToString(), Secret));
        Assert.False(PickupWebhookEndpoints.IsAuthorized(Basic("anyone", Secret + "x").ToString(), Secret));
        Assert.False(PickupWebhookEndpoints.IsAuthorized(Basic(Secret, string.Empty).ToString(), Secret));
    }

    public void Dispose() => File.Delete(policyFile);

    private static string WritePolicy()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pickup-policy-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, PolicyJson);
        return path;
    }

    private HttpClient CreateClient(ITicketPickupStore store) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            // UseSetting: Program.cs reads these while starting up to decide whether to map the webhook.
            builder.UseSetting("DB_API_USER", "visioniq_api");
            builder.UseSetting("DB_API_PASSWORD", "test-only-api-password");
            builder.UseSetting("PICKUP_POLICY_FILE", policyFile);
            builder.UseSetting("PICKUP_WEBHOOK_SECRET", Secret);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(store);
                // No worker or sweep in endpoint tests: they would need a database and ADO.
                services.RemoveAll<IHostedService>();
            });
        }).CreateClient();

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, JsonNode body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/ado") { Content = Json(body) };
        request.Headers.Authorization = Basic("visioniq", Secret);
        return await client.SendAsync(request);
    }

    private static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
}
