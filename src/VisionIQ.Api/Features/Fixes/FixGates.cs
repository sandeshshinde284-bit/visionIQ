namespace VisionIQ.Api.Features.Fixes;

public enum FixOverall
{
    /// <summary>Every required gate passed for the head commit.</summary>
    Green,

    /// <summary>A required gate failed.</summary>
    Red,

    /// <summary>Nothing failed, but a required gate has not passed (not run, inconclusive, stale, in review).</summary>
    NotGreen,
}

public sealed record FixGateSummary(FixOverall Overall, IReadOnlyList<string> Blocking);

/// <summary>
/// S15-12 / S11-17 / S13-06: "green means green" (v1.11 §8.6.1, §8.6.3, §11.1.1). The same rules as
/// <c>app/defect/gates.py</c>, applied here to the pull request's current head:
/// <list type="bullet">
/// <item>GREEN needs every required gate to be PASS. NOT_RUN, INCONCLUSIVE, UNAVAILABLE and REVIEW_REQUIRED are
/// never shown as passed.</item>
/// <item>A result belongs to the commit it was measured on. After a new commit every earlier result is stale and
/// counts as NOT_RUN until it is measured again.</item>
/// </list>
/// </summary>
public static class FixGates
{
    public const string Pass = "PASS";
    public const string Fail = "FAIL";
    public const string NotRun = "NOT_RUN";
    public const string NotApplicable = "NOT_APPLICABLE";

    /// <summary>The statuses VisionIQ posts. Native CI and the engineer's review belong to the platform.</summary>
    private static readonly (string Gate, string Check)[] CheckNames =
    [
        ("reproduction", "visioniq/reproduction"),
        ("candidate_test", "visioniq/fix-test"),
        ("historical_regression", "visioniq/historical-regression"),
        ("touched_suite", "visioniq/touched-suite"),
        ("performance", "visioniq/performance"),
        ("patch_policy", "visioniq/patch-policy"),
    ];

    /// <summary>The gate as it stands for <paramref name="headSha"/>: a result for another commit is stale.</summary>
    public static FixGate Effective(FixGate gate, string? headSha)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (headSha is null || string.Equals(gate.CommitSha, headSha, StringComparison.OrdinalIgnoreCase))
        {
            return gate;
        }

        if (gate.CommitSha is null)
        {
            // A result that names no commit cannot be shown to belong to this head. Only a PASS loses anything:
            // every other status is already "not passed".
            return gate.Status == Pass
                ? gate with { Status = NotRun, Detail = "The result names no commit, so it cannot be applied to this head." }
                : gate;
        }

        return gate with
        {
            Status = NotRun,
            Detail = $"Stale: measured on {Short(gate.CommitSha)}, the head is now {Short(headSha)}.",
        };
    }

    public static FixGateSummary Summarize(IEnumerable<FixGate> gates, string? headSha)
    {
        ArgumentNullException.ThrowIfNull(gates);
        var required = gates.Where(gate => gate is not null).Select(gate => Effective(gate, headSha)).Where(gate => gate.Required).ToList();
        var blocking = required.Where(gate => gate.Status != Pass).Select(gate => gate.Name).ToList();
        var overall = required.Exists(gate => gate.Status == Fail) ? FixOverall.Red
            : blocking.Count == 0 && required.Count > 0 ? FixOverall.Green
            : FixOverall.NotGreen;
        return new FixGateSummary(overall, blocking);
    }

    /// <summary>
    /// The sandbox results rebound to the commit that was pushed. The pushed commit has another SHA than the
    /// sandbox candidate but, once its tree is verified to be the same, the same content: the results apply to it.
    /// </summary>
    public static IReadOnlyList<FixGate> ForPushedCommit(IEnumerable<FixGate> gates, string pushedSha)
    {
        ArgumentNullException.ThrowIfNull(gates);
        return [.. gates.Select(gate => gate.CommitSha is null ? gate : gate with { CommitSha = pushedSha })];
    }

    /// <summary>S13-06: the PR statuses for <paramref name="headSha"/>. Anything short of PASS is never succeeded.</summary>
    public static IReadOnlyList<FixCheck> Checks(IEnumerable<FixGate> gates, string? headSha)
    {
        ArgumentNullException.ThrowIfNull(gates);
        var effective = gates.Where(gate => gate is not null).Select(gate => Effective(gate, headSha)).ToList();
        var checks = new List<FixCheck>();
        // Always the same six statuses, in the same order. A gate the result does not have is posted as
        // pending, so an earlier green status under that name never stays on the pull request; and when a
        // result names a gate twice, the worse of the two is shown.
        foreach (var (gateName, name) in CheckNames)
        {
            var gate = effective.Where(candidate => candidate.Name == gateName).OrderBy(Rank).FirstOrDefault();
            if (gate is null)
            {
                checks.Add(new FixCheck(name, "pending", NotRun + ": the fix result has no result for this check."));
                continue;
            }

            var state = gate.Status switch
            {
                Pass => "succeeded",
                Fail => "failed",
                NotApplicable => "not_applicable",
                _ => "pending",
            };
            var description = gate.Status + ": " + gate.Detail;
            checks.Add(new FixCheck(name, state, description.Length <= 400 ? description : description[..400]));
        }

        return checks;
    }

    /// <summary>Worst first: a failure, then anything that is not a pass, then not applicable, then a pass.</summary>
    private static int Rank(FixGate gate) => gate.Status switch
    {
        Fail => 0,
        Pass => 3,
        NotApplicable => 2,
        _ => 1,
    };

    private static string Short(string sha) => sha.Length <= 12 ? sha : sha[..12];
}
