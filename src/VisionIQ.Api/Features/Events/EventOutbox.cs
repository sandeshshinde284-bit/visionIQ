using Npgsql;
using NpgsqlTypes;

namespace VisionIQ.Api.Features.Events;

/// <summary>
/// Transactional outbox (S03-08). <see cref="EnqueueAsync"/> runs inside the caller's transaction, so the event
/// exists exactly when the change that caused it was committed. A crash before publishing only delays the event;
/// a crash after publishing but before marking it sent re-publishes it, which the consumer's inbox absorbs.
/// </summary>
public static class EventOutbox
{
    /// <summary>
    /// Failed publishes before a row is parked. With the backoff below that is about 2.3 hours, long enough to
    /// ride out a broker outage; a missing topic or permission is parked for a human instead of retried forever.
    /// </summary>
    public const int MaxPublishAttempts = 20;

    private const int MaxBackoffSeconds = 600;

    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(10);

    public static async Task EnqueueAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, EventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        EventContracts.Validate(envelope);
        await using var command = new NpgsqlCommand(
            "INSERT INTO workflow.event_outbox (event_id, event_type, schema_version, topic, trace_id, payload) " +
            "VALUES ($1, $2, $3, $4, $5, $6)",
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = envelope.EventId });
        command.Parameters.Add(new NpgsqlParameter { Value = envelope.EventType });
        command.Parameters.Add(new NpgsqlParameter { Value = envelope.SchemaVersion });
        command.Parameters.Add(new NpgsqlParameter { Value = EventTopology.TopicFor(envelope) });
        command.Parameters.Add(new NpgsqlParameter { Value = envelope.TraceId });
        command.Parameters.Add(new NpgsqlParameter { Value = envelope.ToUtf8Json(), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Publishes due rows. Rows are claimed in a short statement that leases them (SKIP LOCKED, so replicas never
    /// claim the same row), then published with no transaction or lock held, and each outcome is recorded on its
    /// own. A crash or shutdown mid-batch only re-publishes the rows whose outcome was not yet recorded, once their
    /// lease runs out; the consumer's inbox absorbs the repeat. Returns the number of events published.
    /// </summary>
    public static async Task<int> DispatchPendingAsync(
        NpgsqlDataSource dataSource, IEventPublisher publisher, ILogger logger, int batchSize = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(publisher);

        var due = new List<(Guid EventId, string Topic, byte[] Payload, int Attempts)>();
        await using (var claim = dataSource.CreateCommand(
            "UPDATE workflow.event_outbox o SET available_at = now() + make_interval(secs => $2) " +
            "FROM (SELECT event_id FROM workflow.event_outbox " +
            "      WHERE published_at IS NULL AND parked_at IS NULL AND available_at <= now() " +
            "      ORDER BY available_at LIMIT $1 FOR UPDATE SKIP LOCKED) due " +
            "WHERE o.event_id = due.event_id " +
            "RETURNING o.event_id, o.topic, o.payload::text, o.attempt_count"))
        {
            claim.Parameters.Add(new NpgsqlParameter { Value = batchSize });
            // The lease outlasts a whole batch of timed-out publishes, so a row is never claimed twice at once.
            claim.Parameters.Add(new NpgsqlParameter { Value = (batchSize * PublishTimeout.TotalSeconds) + 60 });
            await using var reader = await claim.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                due.Add((reader.GetGuid(0), reader.GetString(1), System.Text.Encoding.UTF8.GetBytes(reader.GetString(2)), reader.GetInt32(3)));
            }
        }

        var sent = 0;
        foreach (var (eventId, topic, payload, attempts) in due)
        {
            EventEnvelope envelope;
            try
            {
                envelope = EventEnvelope.Parse(payload);
            }
            catch (EventContractException)
            {
                // A row that no longer matches its contract can never be sent: take it out of the queue so it stops
                // blocking the rows behind it and stays unpublished for a human to inspect.
                await ExecuteAsync(
                    dataSource,
                    "UPDATE workflow.event_outbox SET attempt_count = attempt_count + 1, last_error_code = $2, " +
                    "parked_at = now() WHERE event_id = $1",
                    cancellationToken, eventId, nameof(EventContractException));
                EventLog.Unpublishable(logger, eventId);
                continue;
            }

            using var scope = logger.BeginScope(envelope.LogScope());
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PublishTimeout);
            try
            {
                await publisher.PublishAsync(topic, envelope, timeout.Token);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                var errorCode = ErrorCode(error);
                if (RetryDelaySeconds(attempts) is not { } delay)
                {
                    await ExecuteAsync(
                        dataSource,
                        "UPDATE workflow.event_outbox SET attempt_count = attempt_count + 1, last_error_code = $2, " +
                        "parked_at = now() WHERE event_id = $1",
                        cancellationToken, eventId, errorCode);
                    EventLog.PublishGaveUp(logger, topic, errorCode, attempts + 1);
                    continue;
                }

                await ExecuteAsync(
                    dataSource,
                    "UPDATE workflow.event_outbox SET attempt_count = attempt_count + 1, last_error_code = $2, " +
                    "available_at = now() + make_interval(secs => $3) WHERE event_id = $1",
                    cancellationToken, eventId, errorCode, (double)delay);
                EventLog.PublishFailed(logger, topic, errorCode, attempts + 1, delay);
                continue;
            }

            await ExecuteAsync(
                dataSource,
                "UPDATE workflow.event_outbox SET attempt_count = attempt_count + 1, published_at = now(), " +
                "last_error_code = NULL WHERE event_id = $1",
                cancellationToken, eventId);
            EventLog.Published(logger, topic);
            sent++;
        }

        return sent;
    }

    /// <summary>
    /// Seconds to wait after a failed publish, given the failures before it; null once the row has used up
    /// <see cref="MaxPublishAttempts"/> and should be parked.
    /// </summary>
    public static int? RetryDelaySeconds(int previousAttempts) =>
        previousAttempts + 1 >= MaxPublishAttempts
            ? null
            : Math.Min(MaxBackoffSeconds, 5 * (1 << Math.Min(previousAttempts, 10)));

    /// <summary>The exception type name, reduced to what workflow.event_outbox.last_error_code accepts.</summary>
    public static string ErrorCode(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var code = new string(error.GetType().Name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.').ToArray());
        return code.Length == 0 ? "Unknown" : code[..Math.Min(code.Length, 120)];
    }

    private static async Task ExecuteAsync(
        NpgsqlDataSource dataSource, string sql, CancellationToken cancellationToken, params object[] values)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
