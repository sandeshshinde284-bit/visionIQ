using System.Text.Json;
using System.Text.RegularExpressions;

namespace VisionIQ.Api.Features.Fixes;

/// <summary>
/// The stored fix result as the API reads it (contracts/fixes/fix-result.v1.schema.json, written by Intelligence
/// into <c>knowledge.fix_runs.document</c>). Only the parts needed to open the draft pull request are mapped;
/// unknown fields are ignored so an additive change on the Python side does not break delivery.
/// </summary>
public sealed record FixDocument(
    int SchemaVersion,
    Guid FixId,
    Guid RunId,
    FixTicket Ticket,
    string RepositoryId,
    string BaseSha,
    string State,
    string Verdict,
    string? CandidateSha = null,
    FixReproduction? Reproduction = null,
    FixPatch? Patch = null,
    IReadOnlyList<FixGate>? Gates = null,
    FixPullRequestDraft? PullRequest = null)
{
    public const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>Reads a stored fix result; null when it is not a version-1 result this service can use.</summary>
    public static FixDocument? Parse(string json)
    {
        try
        {
            var fix = JsonSerializer.Deserialize<FixDocument>(json, Json);
            return fix is
            {
                SchemaVersion: SupportedSchemaVersion, Ticket: not null, RepositoryId: not null, BaseSha: not null,
                State: not null, Verdict: not null,
            }
                ? fix
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every file of the candidate commit: the reproduction test and the fix. Null when the result does not carry
    /// what is needed to recreate the commit (then nothing is pushed).
    /// </summary>
    public IReadOnlyList<FixFileChange>? CommitFiles()
    {
        if (Reproduction?.Test is not { Path.Length: > 0, Content: not null } test || Patch?.Files is not { Count: > 0 } files ||
            files.Any(file => file is null || string.IsNullOrEmpty(file.Path) || file.Content is null || file.Change is not ("added" or "modified")))
        {
            return null;
        }

        return [new FixFileChange(test.Path, "added", test.Content), .. files];
    }
}

public sealed record FixTicket(string Project, string Id, int Revision, string? Title = null);

public sealed record FixReproduction(string Reproducibility, FixGeneratedTest? Test = null);

public sealed record FixGeneratedTest(string Path, string TestId, string Content);

public sealed record FixPatch(
    IReadOnlyList<FixFileChange> Files, string PolicyStatus, string? CandidateSha = null, string? TreeSha = null,
    string? Branch = null);

/// <summary>One file of the candidate with its complete new content. <c>Change</c> is added or modified.</summary>
public sealed record FixFileChange(string Path, string Change, string Content);

/// <summary>One piece of evidence; <c>CommitSha</c> is the commit it was measured on.</summary>
public sealed record FixGate(
    string Name, bool Required, string Status, string Detail, string? CommitSha = null,
    IReadOnlyList<string>? ExecutionIds = null);

public sealed record FixPullRequestDraft(
    string SourceBranch, string TargetBranch, string BaseSha, string HeadSha, string WorkItemId, string Title,
    string Description, bool IsDraft = true);

/// <summary>One pull-request status VisionIQ posts: succeeded, failed, pending or not_applicable.</summary>
public sealed record FixCheck(string Name, string State, string Description);

/// <summary>What a value must look like before it is sent to, or accepted from, Azure DevOps.</summary>
internal static partial class FixPatterns
{
    /// <summary>The only branches the bot may create: under visioniq/fix/, plain characters.</summary>
    [GeneratedRegex("^visioniq/fix/[A-Za-z0-9_-]{1,80}$")]
    public static partial Regex FixBranch();

    [GeneratedRegex(@"^(?!.*\.\.)[A-Za-z0-9_][A-Za-z0-9_./-]{0,200}$")]
    public static partial Regex TargetBranch();

    /// <summary>A full commit or tree id, lower-case (SHA-1 or SHA-256).</summary>
    [GeneratedRegex("^[0-9a-f]{40}([0-9a-f]{24})?$")]
    public static partial Regex Sha();
}
