using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionIQ.Api.Features.Events;

/// <summary>
/// The shared event envelope (contracts/events/envelope.v1.schema.json, S03-01). The JSON Schemas are the
/// source of truth; <see cref="EventContracts"/> applies the same rules here and the contract tests prove
/// that .NET output passes the schemas and that every invalid golden fixture is rejected.
/// </summary>
public sealed record EventEnvelope(
    Guid EventId,
    string EventType,
    int SchemaVersion,
    DateTimeOffset OccurredAt,
    string Producer,
    string TenantId,
    string CorrelationId,
    string TraceId,
    Guid? RunId,
    string? SourceRevision,
    JsonElement Data)
{
    public const string ApiProducer = "VisionIQ.Api";

    /// <summary>Strict snake_case JSON: unknown members and missing required members are errors.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };

    /// <summary>Creates an event produced by this service.</summary>
    public static EventEnvelope Create<TData>(
        string eventType, string tenantId, string correlationId, string traceId, Guid? runId,
        string? sourceRevision, TData data, TimeProvider? clock = null) => new(
            Guid.NewGuid(),
            eventType,
            SchemaVersion: 1,
            (clock ?? TimeProvider.System).GetUtcNow(),
            ApiProducer,
            tenantId,
            correlationId,
            traceId,
            runId,
            sourceRevision,
            JsonSerializer.SerializeToElement(data, Json));

    /// <summary>Parses and validates an event against its contract.</summary>
    /// <exception cref="EventContractException">The event is malformed or has no known contract.</exception>
    public static EventEnvelope Parse(ReadOnlySpan<byte> utf8Json)
    {
        EventEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<EventEnvelope>(utf8Json, Json);
        }
        catch (JsonException error)
        {
            // Path only: the message of a JsonException can quote payload values.
            throw new EventContractException($"envelope is not valid JSON for the contract ({error.Path ?? "<root>"})");
        }

        if (envelope is null)
        {
            throw new EventContractException("envelope is null");
        }

        EventContracts.Validate(envelope);
        return envelope;
    }

    public byte[] ToUtf8Json() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <summary>Reads the payload as its contract type (strict: unknown or missing members fail).</summary>
    public TData ReadData<TData>()
    {
        try
        {
            return Data.Deserialize<TData>(Json) ?? throw new EventContractException("data is null");
        }
        catch (JsonException error)
        {
            throw new EventContractException($"data does not match {EventType} v{SchemaVersion} ({error.Path ?? "<root>"})");
        }
    }

    /// <summary>Log scope so every line about this event carries the trace and correlation IDs.</summary>
    public Dictionary<string, object> LogScope() => new()
    {
        ["traceId"] = TraceId,
        ["correlationId"] = CorrelationId,
        ["runId"] = RunId?.ToString() ?? string.Empty,
        ["eventId"] = EventId.ToString(),
        ["eventType"] = EventType,
    };
}

/// <summary>The event does not match a known contract; retrying cannot fix it.</summary>
public sealed class EventContractException : Exception
{
    public EventContractException()
    {
    }

    public EventContractException(string message)
        : base(message)
    {
    }

    public EventContractException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
