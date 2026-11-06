using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using VisionIQ.Api.Configuration;
using VisionIQ.Api.Features.Events;
using VisionIQ.Api.Features.Pickup;
using static VisionIQ.Api.Tests.PickupTestData;

namespace VisionIQ.Api.Tests;

/// <summary>
/// BRF-01 completion evidence on disposable PostgreSQL (after <c>python -m db.manage all</c>): a ticket moved to
/// In Progress starts exactly one run, and a dropped webhook is recovered by the sweep without a second run.
/// </summary>
public sealed class PickupDatabaseIntegrationTests
{
    private const string Flag = "VISIONIQ_PICKUP_DB_INTEGRATION";
    private static readonly EventingOptions Eventing = new("visioniq-pickup-test", null, false, TimeSpan.FromSeconds(2));

    [DatabaseFact(Flag)]
    public async Task Webhook_redelivery_and_sweep_of_one_pickup_start_exactly_one_run()
    {
        await using var dataSource = NpgsqlDataSource.Create(Options().CreateConnectionStringBuilder().ConnectionString);
        var store = new PostgresTicketPickupStore(dataSource);
        var ticket = Random.Shared.Next(100_000, int.MaxValue);
        var change = Change() with { WorkItemId = ticket };
        var decision = Policy.Evaluate(change);
        TicketPickup Delivery(PickupTrigger trigger, string id) =>
            TicketPickup.From(change, decision, Policy.Revision, trigger, id, null, "0af7651916cd43dd8448eb211c80319c");

        var webhook = Delivery(PickupTrigger.AdoWebhook, Guid.NewGuid().ToString());
        Assert.Equal(RecordOutcome.Recorded, await store.RecordAsync(webhook, default));
        Assert.Equal(RecordOutcome.Duplicate, await store.RecordAsync(webhook with { PickupId = Guid.NewGuid() }, default));
        Assert.True(await store.IsKnownAsync("payments-core", webhook.TicketId, 7, default));
        // The sweep found it too (e.g. it ran before the webhook was processed).
        await store.RecordAsync(Delivery(PickupTrigger.AdoSweep, PickupSweep.DeliveryId("payments-core", webhook.TicketId, 7, Policy.Revision)), default);

        var processor = new PickupProcessor(store, null, Policy, Eventing, NullLogger.Instance);
        while (await processor.ProcessPendingAsync(batchSize: 1000) > 0)
        {
        }

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var runs = new NpgsqlCommand(
            "SELECT count(*) FROM workflow.analysis_runs WHERE project_id = 'payments-core' AND ticket_id = $1", connection);
        runs.Parameters.Add(new NpgsqlParameter { Value = webhook.TicketId });
        Assert.Equal(1L, await runs.ExecuteScalarAsync());

        await using var pickups = new NpgsqlCommand(
            "SELECT string_agg(status, ',' ORDER BY status), count(DISTINCT run_id) FROM workflow.ticket_pickups WHERE ticket_id = $1",
            connection);
        pickups.Parameters.Add(new NpgsqlParameter { Value = webhook.TicketId });
        await using var reader = await pickups.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(("duplicate,started", 1L), (reader.GetString(0), reader.GetInt64(1)));
        await reader.CloseAsync();

        await using var outbox = new NpgsqlCommand(
            "SELECT count(*) FROM workflow.event_outbox WHERE event_type = 'analysis.requested' AND payload->'data'->>'ticket_id' = $1",
            connection);
        outbox.Parameters.Add(new NpgsqlParameter { Value = webhook.TicketId });
        Assert.Equal(1L, await outbox.ExecuteScalarAsync());
    }

    private static DatabaseOptions Options()
    {
        var password = Environment.GetEnvironmentVariable("DB_API_PASSWORD");
        Assert.False(string.IsNullOrWhiteSpace(password));
        return new DatabaseOptions(
            Environment.GetEnvironmentVariable("DB_HOST") ?? "127.0.0.1",
            int.TryParse(Environment.GetEnvironmentVariable("DB_PORT"), out var port) ? port : 5432,
            Environment.GetEnvironmentVariable("DB_NAME") ?? "visioniq",
            Environment.GetEnvironmentVariable("DB_API_USER") ?? "visioniq_api",
            password!,
            "disable");
    }
}
