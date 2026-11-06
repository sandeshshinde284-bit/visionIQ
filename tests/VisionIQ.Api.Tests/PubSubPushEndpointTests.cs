using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VisionIQ.Api.Features.Events;

namespace VisionIQ.Api.Tests;

/// <summary>S03-07/S03-09: push delivery semantics, without a database (the inbox is replaced by a fake).</summary>
public sealed class PubSubPushEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Duplicate_delivery_is_acknowledged_and_applied_once()
    {
        var processor = new FakeProcessor();
        using var client = CreateClient(processor);
        var body = PushBody(File.ReadAllBytes(EventContractTests.FixturePath("valid", "analysis.completed.v1.json")));

        var first = await client.PostAsJsonAsync("/events/pubsub", body);
        var second = await client.PostAsJsonAsync("/events/pubsub", body);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Single(processor.Applied);
    }

    [Theory]
    [MemberData(nameof(EventContractTests.InvalidFixtures), MemberType = typeof(EventContractTests))]
    public async Task Contract_violations_are_nacked_so_they_reach_the_dead_letter_topic(string fixture)
    {
        var processor = new FakeProcessor();
        using var client = CreateClient(processor);

        var response = await client.PostAsJsonAsync(
            "/events/pubsub", PushBody(File.ReadAllBytes(EventContractTests.FixturePath("invalid", fixture))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(processor.Applied);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"message\":{}}")]
    [InlineData("{\"message\":{\"data\":\"not base64!\"}}")]
    [InlineData("{\"message\":{\"data\":\"e30=\"}}")]
    [InlineData("not json")]
    public async Task Malformed_push_messages_are_rejected(string body)
    {
        using var client = CreateClient(new FakeProcessor());

        var response = await client.PostAsync("/events/pubsub", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Transient_failures_ask_pubsub_to_retry()
    {
        using var client = CreateClient(new FakeProcessor(new TimeoutException()));

        var response = await client.PostAsJsonAsync(
            "/events/pubsub", PushBody(File.ReadAllBytes(EventContractTests.FixturePath("valid", "analysis.completed.v1.json"))));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("unknown_run")]
    [InlineData("tenant_mismatch")]
    [InlineData("run_already_completed")]
    public async Task Conflicting_events_are_rejected_for_dead_lettering(string reason)
    {
        using var client = CreateClient(new FakeProcessor(new EventRejectedException(reason)));

        var response = await client.PostAsJsonAsync(
            "/events/pubsub", PushBody(File.ReadAllBytes(EventContractTests.FixturePath("valid", "analysis.completed.v1.json"))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(reason, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(TimeoutException), "TimeoutException")]
    [InlineData(typeof(System.ComponentModel.Win32Exception), "Win32Exception")]
    public void Outbox_error_codes_fit_the_database_check(Type type, string expected)
    {
        var error = (Exception)Activator.CreateInstance(type)!;

        Assert.Equal(expected, EventOutbox.ErrorCode(error));
        Assert.Matches("^[A-Za-z0-9_.]{1,120}$", EventOutbox.ErrorCode(error));
    }

    [Fact]
    public void Failed_publishes_back_off_then_park_the_row()
    {
        var delays = Enumerable.Range(0, EventOutbox.MaxPublishAttempts).Select(EventOutbox.RetryDelaySeconds).ToList();

        Assert.Equal([5, 10, 20, 40], delays.Take(4));
        Assert.Equal(600, delays.Max());
        Assert.Null(delays[^1]);
        Assert.Single(delays, d => d is null);
    }

    [Fact]
    public void Dispatch_requires_a_project()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["EVENTS_DISPATCH_ENABLED"] = "true" })
            .Build();

        Assert.Throws<InvalidOperationException>(() => EventingOptions.FromConfiguration(configuration));
    }

    private static object PushBody(byte[] eventJson) => new
    {
        message = new { data = Convert.ToBase64String(eventJson), messageId = "1", attributes = new { } },
        subscription = "projects/visioniq-local/subscriptions/test",
        deliveryAttempt = 1,
    };

    private HttpClient CreateClient(IInboundEventProcessor processor) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DB_API_USER"] = "visioniq_api",
                    ["DB_API_PASSWORD"] = "test-only-api-password",
                }));
            builder.ConfigureTestServices(services => services.AddSingleton(processor));
        }).CreateClient();

    private sealed class FakeProcessor(Exception? failure = null) : IInboundEventProcessor
    {
        public List<Guid> Applied { get; } = [];

        public Task<InboundOutcome> ProcessAsync(EventEnvelope envelope, CancellationToken cancellationToken)
        {
            if (failure is not null)
            {
                throw failure;
            }

            if (Applied.Contains(envelope.EventId))
            {
                return Task.FromResult(InboundOutcome.Duplicate);
            }

            Applied.Add(envelope.EventId);
            return Task.FromResult(InboundOutcome.Processed);
        }
    }
}
