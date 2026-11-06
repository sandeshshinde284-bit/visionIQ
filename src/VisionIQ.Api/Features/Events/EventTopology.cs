namespace VisionIQ.Api.Features.Events;

/// <summary>Topic names from contracts/events/topology.json; a test keeps the two in sync.</summary>
public static class EventTopology
{
    public const string PushPath = "/events/pubsub";

    public static readonly IReadOnlyDictionary<(string Type, int Version), string> Topics =
        new Dictionary<(string Type, int Version), string>
        {
            [(EventContracts.AnalysisRequested, 1)] = "visioniq-analysis-requested-v1",
            [(EventContracts.AnalysisCompleted, 1)] = "visioniq-analysis-completed-v1",
        };

    public static string TopicFor(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return Topics.TryGetValue((envelope.EventType, envelope.SchemaVersion), out var topic)
            ? topic
            : throw new EventContractException($"no topic for {envelope.EventType} v{envelope.SchemaVersion}");
    }
}
