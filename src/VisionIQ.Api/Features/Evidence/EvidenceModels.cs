using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VisionIQ.Api.Features.Evidence;

/// <summary>
/// Immutable provider identity. Null repository ID is different from an empty string.
/// Keep provider-native IDs case-sensitive; do not guess cross-project identity.
/// </summary>
public sealed record EvidenceIdentity(
    string Provider,
    string SourceInstance,
    string ProjectId,
    string? RepositoryId,
    string Kind,
    string ExternalId)
{
    public string ItemKey()
    {
        var values = new[] { Provider, SourceInstance, ProjectId, RepositoryId, Kind, ExternalId };
        if (values.Where((value, index) => index != 3).Any(string.IsNullOrWhiteSpace) ||
            RepositoryId is "")
        {
            throw new ArgumentException("Source identity fields must be populated.");
        }

        using var stream = new MemoryStream();
        foreach (var value in values)
        {
            if (value is null)
            {
                WriteLength(stream, -1);
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            WriteLength(stream, bytes.Length);
            stream.Write(bytes);
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteLength(Stream stream, int length)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, length);
        stream.Write(lengthBytes);
    }
}

/// <summary>
/// Access rules are supplied by an authenticated source connector, not caller-provided HTTP claims.
/// Unknown source ACLs always remain unverified and inaccessible to retrieval.
/// </summary>
public sealed record EvidenceAccess(bool Verified, IReadOnlyList<string> PrincipalIds)
{
    public string Json()
    {
        var ids = PrincipalIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return JsonSerializer.Serialize(Verified ? ids : Array.Empty<string>());
    }
}

public sealed record EvidenceEnvelope(
    EvidenceIdentity Identity,
    string ProviderRevisionId,
    DateTimeOffset SourceUpdatedAt,
    DateTimeOffset ObservedAt,
    string SourceUrl,
    string RetrievedUnderPrincipal,
    byte[] RawJson,
    EvidenceAccess Access);

public sealed record ArchivedEvidence(
    EvidenceEnvelope Envelope,
    string ItemKey,
    string ContentSha256,
    string RawSnapshotUri,
    string RedactedJson,
    string RedactionVersion,
    string NormalizerVersion);

public enum EvidenceImportStatus
{
    Created,
    NewRevision,
    Unchanged,
    Conflict,
}

public sealed record EvidenceImportResult(
    string ItemKey,
    string ProviderRevisionId,
    EvidenceImportStatus Status);

/// <summary>
/// Generates a restrictive metadata-only representation. Never indexes source text
/// before provider-specific secret/PII redaction policies have been approved.
/// </summary>
public static class EvidenceNormalizer
{
    private static readonly Dictionary<string, string[]> SafeFields = new(StringComparer.Ordinal)
    {
        // B-13: names must match the camelCase JSON the ingestor actually serializes.
        // The environment fields are copied only when they hold a value from the classifier's closed
        // vocabulary (see ClosedVocabularies). environmentRaw is source text and is never copied.
        ["work_item_current"] = ["state", "workItemType", "environment", "environmentStatus",
            "environmentSource", "environmentReason"],
        ["work_item"] = ["state", "workItemType", "reason"],
        ["pull_request"] = ["status", "isDraft"],
        ["commit"] = [],
        ["declared_link"] = ["linkType"],
        ["review"] = ["status", "vote"],
        ["ci_run"] = ["status", "result", "reason"],
        ["deployment"] = ["status", "result"],
        ["incident"] = ["status", "severity"],
        ["design_page"] = ["status", "version"],
        ["file"] = ["changeType"],
    };

    /// <summary>
    /// Review D-01: fields whose values come from DefectEnvironmentClassifier. Any other value (for
    /// example free text that reached the field) is dropped, so these fields can never carry source text.
    /// environmentSource may list several sources joined by commas (a CONFLICT names every source).
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> ClosedVocabularies = new(StringComparer.Ordinal)
    {
        ["environment"] = new(StringComparer.Ordinal) { "PROD", "UAT", "TEST", "UNKNOWN", "CONFLICT" },
        ["environmentStatus"] = new(StringComparer.Ordinal) { "verified", "candidate", "unknown", "conflict" },
        ["environmentReason"] = new(StringComparer.Ordinal)
        {
            "matched", "not_configured", "no_value", "rules_invalid", "unmapped_value",
            "sources_disagree", "candidates_disagree",
        },
        ["environmentSource"] = new(StringComparer.Ordinal)
        {
            "none", "field", "labels", "path", "title", "description",
        },
    };

    private static bool InClosedVocabulary(string field, string text) =>
        !ClosedVocabularies.TryGetValue(field, out var allowed) ||
        (field == "environmentSource"
            ? text.Split(',').All(allowed.Contains)
            : allowed.Contains(text));

    public const string Version = "metadata-only-v1";

    public static string Redact(string kind, ReadOnlySpan<byte> rawJson)
    {
        using var document = JsonDocument.Parse(rawJson.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Evidence payload must be a JSON object.");
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = kind,
        };

        if (!SafeFields.TryGetValue(kind, out var fields))
        {
            // Unknown source kind: no unreviewed data is copied into searchable metadata.
            return JsonSerializer.Serialize(result);
        }

        foreach (var field in fields)
        {
            if (!document.RootElement.TryGetProperty(field, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            {
                result[field] = value.GetBoolean();
            }
            else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
            {
                result[field] = n;
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (text is { Length: > 0 and <= 40 } &&
                    text.All(character => char.IsAsciiLetterOrDigit(character) ||
                        character is '_' or '-' or ' ' or ',') &&
                    (text.IndexOf(',') < 0 || field == "environmentSource") &&
                    InClosedVocabulary(field, text))
                {
                    result[field] = text;
                }
            }
        }

        return JsonSerializer.Serialize(result);
    }
}
