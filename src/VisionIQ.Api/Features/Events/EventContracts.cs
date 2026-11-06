using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VisionIQ.Api.Features.Events;

/// <summary>analysis.requested v1 payload (S03-02). Produced here, consumed by Intelligence.</summary>
public sealed record AnalysisRequestedV1(
    string ProjectId,
    string TicketId,
    int TicketRevision,
    string RepositoryId,
    string BaseCommitSha,
    AnalysisMode AnalysisMode,
    string SecurityScopeId);

public enum AnalysisMode
{
    Brief,
    Deep,
}

/// <summary>analysis.completed v1 payload (S03-03). Produced by Intelligence, consumed here.</summary>
public sealed record AnalysisCompletedV1(
    Guid ResultId,
    ResultState ResultState,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> AdviceIds,
    IReadOnlyList<string> ReasonCodes);

public enum ResultState
{
    [JsonStringEnumMemberName("PUBLISHED")]
    Published,

    [JsonStringEnumMemberName("PARTIAL")]
    Partial,

    [JsonStringEnumMemberName("BLOCKED")]
    Blocked,
}

/// <summary>
/// The envelope and payload rules of contracts/events. Unknown event types or versions are rejected rather
/// than guessed at (S03-06). Messages name the failing rule, never the offending value.
/// </summary>
public static partial class EventContracts
{
    public const string AnalysisRequested = "analysis.requested";
    public const string AnalysisCompleted = "analysis.completed";

    private static readonly Dictionary<(string Type, int Version), (string Producer, Action<EventEnvelope> Data)> Known = new()
    {
        [(AnalysisRequested, 1)] = (EventEnvelope.ApiProducer, ValidateAnalysisRequested),
        [(AnalysisCompleted, 1)] = ("VisionIQ.Intelligence", ValidateAnalysisCompleted),
    };

    public static void Validate(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!Known.TryGetValue((envelope.EventType ?? string.Empty, envelope.SchemaVersion), out var contract))
        {
            throw new EventContractException($"no contract for {envelope.EventType} v{envelope.SchemaVersion}");
        }

        Require(envelope.EventId != Guid.Empty, "event_id");
        Require(envelope.Producer == contract.Producer, "producer");
        Require(TenantId().IsMatch(envelope.TenantId ?? string.Empty), "tenant_id");
        Require(CorrelationId().IsMatch(envelope.CorrelationId ?? string.Empty), "correlation_id");
        Require(IsTraceId(envelope.TraceId), "trace_id");
        Require(envelope.RunId is { } run && run != Guid.Empty, "run_id");
        Require(envelope.SourceRevision is null || envelope.SourceRevision.Length <= 256, "source_revision");
        contract.Data(envelope);
    }

    public static bool IsTraceId(string? value) =>
        value is not null && TraceId().IsMatch(value) && value.Any(c => c != '0');

    private static void ValidateAnalysisRequested(EventEnvelope envelope)
    {
        var data = envelope.ReadData<AnalysisRequestedV1>();
        Require(Identifier().IsMatch(data.ProjectId ?? string.Empty), "data/project_id");
        Require(Identifier().IsMatch(data.TicketId ?? string.Empty), "data/ticket_id");
        Require(data.TicketRevision >= 1, "data/ticket_revision");
        Require(Identifier().IsMatch(data.RepositoryId ?? string.Empty), "data/repository_id");
        Require(CommitSha().IsMatch(data.BaseCommitSha ?? string.Empty), "data/base_commit_sha");
        Require(Enum.IsDefined(data.AnalysisMode), "data/analysis_mode");
        Require(Identifier().IsMatch(data.SecurityScopeId ?? string.Empty), "data/security_scope_id");
    }

    private static void ValidateAnalysisCompleted(EventEnvelope envelope)
    {
        var data = envelope.ReadData<AnalysisCompletedV1>();
        Require(data.ResultId != Guid.Empty, "data/result_id");
        Require(Enum.IsDefined(data.ResultState), "data/result_state");
        Require(UniqueIdentifiers(data.EvidenceIds), "data/evidence_ids");
        Require(UniqueIdentifiers(data.AdviceIds), "data/advice_ids");
        Require(
            data.ReasonCodes is not null && data.ReasonCodes.Distinct().Count() == data.ReasonCodes.Count &&
            data.ReasonCodes.All(code => ReasonCode().IsMatch(code ?? string.Empty)),
            "data/reason_codes");
        Require(data.ResultState == ResultState.Published || data.ReasonCodes is { Count: > 0 }, "data/reason_codes: minItems");
    }

    private static bool UniqueIdentifiers(IReadOnlyList<string>? values) =>
        values is not null && values.Distinct().Count() == values.Count &&
        values.All(value => Identifier().IsMatch(value ?? string.Empty));

    private static void Require(bool condition, string rule)
    {
        if (!condition)
        {
            throw new EventContractException($"contract violation: {rule}");
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex TraceId();

    [GeneratedRegex("^[A-Za-z0-9_.:-]{1,128}$")]
    private static partial Regex TenantId();

    [GeneratedRegex("^[A-Za-z0-9_.:-]{8,128}$")]
    private static partial Regex CorrelationId();

    [GeneratedRegex("^[A-Za-z0-9_.:/-]{1,256}$")]
    private static partial Regex Identifier();

    [GeneratedRegex("^[0-9a-f]{40}([0-9a-f]{24})?$")]
    private static partial Regex CommitSha();

    [GeneratedRegex("^[a-z][a-z0-9_]{1,79}$")]
    private static partial Regex ReasonCode();
}
