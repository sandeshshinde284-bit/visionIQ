using System.Text.Json;

namespace VisionIQ.Api.Features.Briefs;

/// <summary>
/// The stored brief as the API reads it (contracts/briefs/brief.v1.schema.json, written by Intelligence into
/// <c>knowledge.briefs.document</c>). Only the parts shown on the ticket are mapped; unknown fields are ignored so
/// an additive change on the Python side does not break delivery.
/// </summary>
public sealed record BriefDocument(
    int SchemaVersion,
    BriefTicket Ticket,
    string State,
    BriefRisk Risk,
    IReadOnlyList<string>? ReasonCodes = null,
    IReadOnlyList<BriefScopeItem>? PredictedScope = null,
    IReadOnlyList<BriefAdvice>? Advice = null,
    IReadOnlyList<BriefEvidence>? Evidence = null,
    IReadOnlyList<BriefDisagreement>? Disagreements = null,
    IReadOnlyList<string>? Limitations = null,
    IReadOnlyList<BriefInjectionFlag>? InjectionFlags = null,
    string? Kind = null,
    IReadOnlyList<BriefGuidanceSection>? Guidance = null)
{
    public const int SupportedSchemaVersion = 1;

    /// <summary>
    /// ENH-01: true for an enhancement, whose advice is shown in the eight sections of master design 8.5. A brief
    /// written before the field existed has no kind and is a defect brief.
    /// </summary>
    public bool IsEnhancementGuidance =>
        string.Equals(Kind, "enhancement", StringComparison.Ordinal) && Guidance is { Count: > 0 };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>Reads a stored brief; null when it is not a version-1 brief this service can render.</summary>
    public static BriefDocument? Parse(string json)
    {
        try
        {
            var brief = JsonSerializer.Deserialize<BriefDocument>(json, Json);
            return brief is { SchemaVersion: SupportedSchemaVersion, Ticket: not null, Risk: not null, State: not null }
                ? brief
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record BriefTicket(string Project, string Id, int Revision, string? Title = null);

public sealed record BriefRisk(string Band, string Explanation, IReadOnlyList<BriefRiskFactor>? Factors = null);

public sealed record BriefRiskFactor(
    string Code, string Label, int? Value, bool Missing = false, IReadOnlyList<string>? References = null);

public sealed record BriefScopeItem(string Path, string Basis, string Confidence);

public sealed record BriefAdvice(
    string AdviceId, string Kind, string Statement, IReadOnlyList<string>? EvidenceIds = null, string EvidenceState = "UNVERIFIED",
    string? Section = null);

/// <summary>ENH-01: one of the eight guidance sections; it has no advice ids when nothing supported it.</summary>
public sealed record BriefGuidanceSection(
    string Section, string Title, string Status, IReadOnlyList<string>? AdviceIds = null);

public sealed record BriefEvidence(
    string Id, string Kind, string Title, string? Url, string Relevance, IReadOnlyList<string>? Notes = null);

public sealed record BriefDisagreement(string AdviceId, string Verdict, string Reason);

public sealed record BriefInjectionFlag(string Source, string Pattern);
