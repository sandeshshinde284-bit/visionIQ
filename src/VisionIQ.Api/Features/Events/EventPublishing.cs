using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Npgsql;

namespace VisionIQ.Api.Features.Events;

/// <summary>Publishes one event and returns once the broker has accepted it; throws otherwise.</summary>
public interface IEventPublisher
{
    Task PublishAsync(string topic, EventEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>
/// Google Cloud Pub/Sub publisher. Uses Application Default Credentials in Cloud Run, or the local emulator when
/// PUBSUB_EMULATOR_HOST is set. Attributes let subscriptions filter and logs correlate without reading the body.
/// </summary>
public sealed class PubSubEventPublisher(EventingOptions options) : IEventPublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private PublisherServiceApiClient? client;

    public async Task PublishAsync(string topic, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var api = await ClientAsync(cancellationToken);
        var message = new PubsubMessage
        {
            Data = ByteString.CopyFrom(envelope.ToUtf8Json()),
            Attributes =
            {
                ["event_id"] = envelope.EventId.ToString(),
                ["event_type"] = envelope.EventType,
                ["schema_version"] = envelope.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["trace_id"] = envelope.TraceId,
                ["correlation_id"] = envelope.CorrelationId,
            },
        };
        var topicName = TopicName.FromProjectTopic(options.PubSubProjectId, topic);
        await api.PublishAsync(topicName, [message], CallSettings.FromCancellationToken(cancellationToken));
    }

    public ValueTask DisposeAsync()
    {
        gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<PublisherServiceApiClient> ClientAsync(CancellationToken cancellationToken)
    {
        if (client is not null)
        {
            return client;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            client ??= await new PublisherServiceApiClientBuilder
            {
                EmulatorDetection = EmulatorDetection.EmulatorOrProduction,
            }.BuildAsync(cancellationToken);
            return client;
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>Drains workflow.event_outbox on an interval. Enabled with EVENTS_DISPATCH_ENABLED=true.</summary>
public sealed class OutboxDispatcher(
    NpgsqlDataSource dataSource,
    IEventPublisher publisher,
    EventingOptions options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EventLog.DispatcherStarted(logger, options.DispatchInterval.TotalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            var sent = 0;
            try
            {
                sent = await EventOutbox.DispatchPendingAsync(dataSource, publisher, logger, cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                // Type only: provider messages can include connection details.
                EventLog.DispatchFailed(logger, error.GetType().Name);
            }

            if (sent == 0)
            {
                try
                {
                    await Task.Delay(options.DispatchInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
