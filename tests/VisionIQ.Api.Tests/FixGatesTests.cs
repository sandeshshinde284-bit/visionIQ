using VisionIQ.Api.Features.Fixes;
using static VisionIQ.Api.Tests.FixTestData;

namespace VisionIQ.Api.Tests;

/// <summary>
/// S15-12 test quality gate truthfulness and S11-17 stale checks (v1.11 §8.6.1, §8.6.3, §11.1.1): a required
/// check that is failing, NOT_RUN or INCONCLUSIVE is never shown as green, and a new commit on the pull request
/// invalidates earlier results.
/// </summary>
public sealed class FixGatesTests
{
    private const string Head = PushedSha;

    public static TheoryData<string, string> NotPassing() => new()
    {
        { "FAIL", "Red" },
        { "NOT_RUN", "NotGreen" },
        { "INCONCLUSIVE", "NotGreen" },
        { "UNAVAILABLE", "NotGreen" },
        { "REVIEW_REQUIRED", "NotGreen" },
        { "NOT_APPLICABLE", "NotGreen" },
        { "SOMETHING_NEW", "NotGreen" },
    };

    [Fact]
    public void A_fresh_fix_result_is_not_green_while_native_ci_and_the_review_are_open()
    {
        var gates = FixGates.ForPushedCommit(Verified.Gates!, Head);

        var summary = FixGates.Summarize(gates, Head);

        Assert.Equal(FixOverall.NotGreen, summary.Overall);
        Assert.Equal(["native_ci", "human_review"], summary.Blocking);
    }

    [Fact]
    public void Green_needs_every_required_gate_to_pass_on_the_head_commit()
    {
        Assert.Equal(FixOverall.Green, FixGates.Summarize(AllPassed(), Head).Overall);
        Assert.Empty(FixGates.Summarize(AllPassed(), Head).Blocking);
    }

    [Theory]
    [MemberData(nameof(NotPassing))]
    public void A_required_gate_that_did_not_pass_is_never_shown_as_green(string status, string overall)
    {
        foreach (var name in AllPassed().Where(gate => gate.Required).Select(gate => gate.Name))
        {
            // Every other gate passed, native CI is green and an engineer approved: still not green.
            var gates = AllPassed().Select(gate => gate.Name == name ? gate with { Status = status } : gate).ToList();

            var summary = FixGates.Summarize(gates, Head);

            Assert.Equal(overall, summary.Overall.ToString());
            Assert.Contains(name, summary.Blocking);
            Assert.DoesNotContain(
                FixGates.Checks(gates, Head), check => check.Name.EndsWith(CheckSuffix(name), StringComparison.Ordinal) && check.State == "succeeded");
        }
    }

    [Fact]
    public void A_gate_that_is_not_required_and_says_so_does_not_block_and_is_not_called_passed()
    {
        var gates = AllPassed()
            .Select(gate => gate.Name == "performance" ? gate with { Required = false, Status = "NOT_APPLICABLE" } : gate)
            .ToList();

        Assert.Equal(FixOverall.Green, FixGates.Summarize(gates, Head).Overall);
        Assert.Equal("not_applicable", FixGates.Checks(gates, Head).Single(check => check.Name == "visioniq/performance").State);
    }

    [Fact]
    public void With_no_gates_at_all_nothing_is_green()
    {
        Assert.Equal(FixOverall.NotGreen, FixGates.Summarize([], Head).Overall);
    }

    [Fact]
    public void A_result_without_a_gate_still_posts_that_check_as_pending()
    {
        // An earlier status under the same name must be replaced, never left standing as green.
        var withoutSuite = AllPassed().Where(gate => gate.Name != "touched_suite").ToList();

        var missing = FixGates.Checks(withoutSuite, Head);
        var none = FixGates.Checks([], Head);

        Assert.Equal(6, missing.Count);
        Assert.Equal("pending", missing.Single(check => check.Name == "visioniq/touched-suite").State);
        Assert.All(missing.Where(check => check.Name != "visioniq/touched-suite"), check => Assert.Equal("succeeded", check.State));
        Assert.Equal(6, none.Count);
        Assert.All(none, check => Assert.Equal("pending", check.State));
    }

    [Fact]
    public void A_pass_that_names_no_commit_cannot_be_applied_to_a_head()
    {
        var gates = AllPassed().Select(gate => gate.Name == "candidate_test" ? gate with { CommitSha = null } : gate).ToList();

        var summary = FixGates.Summarize(gates, Head);

        Assert.Equal(FixOverall.NotGreen, summary.Overall);
        Assert.Equal(["candidate_test"], summary.Blocking);
        Assert.Equal("pending", FixGates.Checks(gates, Head).Single(check => check.Name == "visioniq/fix-test").State);
        // It is still a pass of the stored result itself, where no head is asked about.
        Assert.Equal("PASS", FixGates.Effective(gates.Single(gate => gate.Name == "candidate_test"), null).Status);
    }

    [Theory]
    [InlineData("FAIL", "PASS", "failed")]
    [InlineData("PASS", "FAIL", "failed")]
    [InlineData("PASS", "INCONCLUSIVE", "pending")]
    [InlineData("NOT_RUN", "PASS", "pending")]
    [InlineData("PASS", "PASS", "succeeded")]
    public void When_a_result_names_a_gate_twice_the_worse_one_is_shown(string first, string second, string state)
    {
        var gates = AllPassed();
        var suite = gates.Single(gate => gate.Name == "touched_suite");
        gates.Remove(suite);
        gates.AddRange([suite with { Status = first }, suite with { Status = second }]);

        var checks = FixGates.Checks(gates, Head);

        Assert.Equal(6, checks.Count); // one status per name: the later one cannot overwrite the earlier
        Assert.Equal(state, checks.Single(check => check.Name == "visioniq/touched-suite").State);
    }

    [Fact]
    public void A_new_commit_invalidates_every_earlier_result()
    {
        var gates = AllPassed();
        Assert.Equal(FixOverall.Green, FixGates.Summarize(gates, Head).Overall);

        var summary = FixGates.Summarize(gates, NewHeadSha); // someone pushed to the pull request

        Assert.Equal(FixOverall.NotGreen, summary.Overall);
        Assert.Equal(gates.Count(gate => gate.Required), summary.Blocking.Count);
        var stale = FixGates.Effective(gates[0], NewHeadSha);
        Assert.Equal("NOT_RUN", stale.Status);
        Assert.Contains("Stale: measured on 111111111111, the head is now 222222222222", stale.Detail, StringComparison.Ordinal);
        Assert.All(FixGates.Checks(gates, NewHeadSha), check => Assert.Equal("pending", check.State));
        Assert.Equal("PASS", gates[0].Status); // the stored result itself is not rewritten
    }

    [Fact]
    public void The_sandbox_results_apply_to_the_pushed_commit_and_to_no_other()
    {
        var sandbox = Verified.Gates!;
        Assert.All(sandbox.Where(gate => gate.CommitSha is not null), gate => Assert.Equal(Verified.CandidateSha, gate.CommitSha));

        var rebound = FixGates.ForPushedCommit(sandbox, Head);

        // Without the rebinding the pushed commit (another SHA, same tree) would look stale.
        Assert.All(FixGates.Checks(sandbox, Head), check => Assert.Equal("pending", check.State));
        Assert.All(FixGates.Checks(rebound, Head), check => Assert.NotEqual("pending", check.State));
        Assert.Null(rebound.Single(gate => gate.Name == "native_ci").CommitSha); // not measured anywhere yet
    }

    [Fact]
    public void The_checks_are_the_sandbox_evidence_and_never_native_ci_or_the_review()
    {
        var checks = FixGates.Checks(FixGates.ForPushedCommit(Verified.Gates!, Head), Head);

        Assert.Equal(
            [
                "visioniq/reproduction", "visioniq/fix-test", "visioniq/historical-regression", "visioniq/touched-suite",
                "visioniq/performance", "visioniq/patch-policy",
            ],
            checks.Select(check => check.Name));
        Assert.All(checks, check => Assert.Equal("succeeded", check.State));
        Assert.All(checks, check => Assert.StartsWith("PASS: ", check.Description, StringComparison.Ordinal));
    }

    [Fact]
    public void The_stored_fix_result_is_read_with_its_draft_and_its_files()
    {
        Assert.Equal(("PUBLISHED", "VERIFIED", "kiosk-demo"), (Verified.State, Verified.Verdict, Verified.RepositoryId));
        Assert.True(Verified.PullRequest!.IsDraft);
        Assert.Equal(("4711", "main"), (Verified.PullRequest.WorkItemId, Verified.PullRequest.TargetBranch));
        Assert.Equal(Verified.CandidateSha, Verified.PullRequest.HeadSha);
        var files = Verified.CommitFiles()!;
        Assert.Equal(
            [("tests/test_captured_payment.py", "added"), ("src/kiosk/retry_policy.py", "modified")],
            files.Select(file => (file.Path, file.Change)));
        Assert.Contains("return not captured and attempt < self.max_attempts", files[1].Content, StringComparison.Ordinal);

        var blocked = FixDocument.Parse(BlockedJson)!;
        Assert.Equal(("BLOCKED", "BLOCKED"), (blocked.State, blocked.Verdict));
        Assert.Null(blocked.PullRequest);
        Assert.Null(blocked.CommitFiles());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"schema_version": 2, "ticket": {"project": "p", "id": "1", "revision": 1}}""")]
    [InlineData("null")]
    public void A_document_that_is_not_a_version_1_fix_result_is_not_used(string json)
    {
        Assert.Null(FixDocument.Parse(json));
    }

    private static List<FixGate> AllPassed() =>
        [.. FixGates.ForPushedCommit(Verified.Gates!, Head).Select(gate => gate with { Status = "PASS", CommitSha = Head })];

    private static string CheckSuffix(string gate) => gate switch
    {
        "candidate_test" => "/fix-test",
        "native_ci" or "human_review" => "/never-posted",
        _ => "/" + gate.Replace('_', '-'),
    };
}
