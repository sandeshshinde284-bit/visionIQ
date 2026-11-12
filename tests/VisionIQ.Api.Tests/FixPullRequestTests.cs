using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionIQ.Api.Features.Fixes;
using static VisionIQ.Api.Tests.FixTestData;

namespace VisionIQ.Api.Tests;

/// <summary>
/// DEF-05 / DEF-06 delivery (S04-05, S11-14, S13-06, S11-17): one draft pull request per verified fix, checks that
/// link to the sandbox evidence, stale checks after a new commit, and no write to Azure DevOps unless explicitly
/// configured. Everything here uses fakes; no test ever contacts a real ADO organization.
/// </summary>
public sealed class FixPullRequestTests
{
    private const string Repository = "https://dev.azure.com/contoso/Payments%20Core/_apis/git/repositories/kiosk-demo";
    private static readonly int[] Attempts = [1, 2, 3, 7, 20];
    private static readonly string[] AllowedCalls = ["/pushes", "/commits/", "/statuses", "/pullrequests?", "/pullrequests/"];

    // ---- The publisher: claim, open, post checks, mark ---------------------------------------------

    [Fact]
    public async Task A_verified_fix_gets_one_draft_pull_request_with_its_checks()
    {
        var store = new FakeFixStore(Pending(VerifiedJson));
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado);
        var publisher = Publisher(store, pullRequests);

        Assert.Equal(1, await publisher.ProcessPendingAsync());
        Assert.Equal(0, await publisher.ProcessPendingAsync()); // nothing left: no second pull request

        var opened = Assert.Single(pullRequests.Published);
        // The run stores the policy's project id; ADO is addressed by the project name.
        Assert.Equal(("Payments Core", false), (opened.Project, opened.MayAlreadyExist));
        Assert.True(opened.Fix.PullRequest!.IsDraft);
        var posted = Assert.Single(pullRequests.Checks);
        Assert.Equal(("Payments Core", "kiosk-demo", "812"), (posted.Project, posted.RepositoryId, posted.PullRequestId));
        Assert.Equal(6, posted.Checks.Count);
        Assert.All(posted.Checks, check => Assert.Equal("succeeded", check.State));
        var (runId, record) = Assert.Single(store.Published);
        Assert.Equal(RunId, runId);
        Assert.Equal((FixPullRequestMode.Ado, "kiosk-demo", "812", PushedSha), (record.Mode, record.RepositoryId, record.PullRequest.PullRequestId, record.PullRequest.HeadSha));
        Assert.StartsWith("visioniq/fix/4711-", record.SourceBranch, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", record.RequestSha256);
        Assert.Null(record.RequestBody); // in ado mode the content lives in the repository, not in our database
    }

    [Fact]
    public async Task Record_mode_keeps_what_would_be_opened_and_calls_nothing_external()
    {
        var store = new FakeFixStore(Pending(VerifiedJson));

        await Publisher(store, new RecordingPullRequestPublisher()).ProcessPendingAsync();

        var record = Assert.Single(store.Published).Record;
        Assert.Equal((FixPullRequestMode.Record, "recorded", Verified.CandidateSha), (record.Mode, record.PullRequest.PullRequestId, record.PullRequest.HeadSha));
        var request = JsonNode.Parse(record.RequestBody!)!;
        Assert.True(request["is_draft"]!.GetValue<bool>());
        Assert.Equal("4711", request["work_item_id"]!.GetValue<string>());
        Assert.Equal(Verified.BaseSha, request["base_sha"]!.GetValue<string>());
        Assert.Equal(2, request["files"]!.AsArray().Count);
        Assert.Equal(6, request["checks"]!.AsArray().Count);
        // File content is referenced by hash; the description carries the evidence trail.
        Assert.DoesNotContain("return not captured and", record.RequestBody, StringComparison.Ordinal);
        Assert.Contains("FAIL_TARGET_SIGNATURE", request["description"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fix_that_was_not_verified_gets_no_pull_request()
    {
        var store = new FakeFixStore(Pending(BlockedJson));
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado);

        await Publisher(store, pullRequests).ProcessPendingAsync();

        Assert.Equal((RunId, "no_draft_pull_request"), Assert.Single(store.Skipped));
        Assert.Empty(pullRequests.Published);
        Assert.Empty(store.Published);
        Assert.Empty(store.Failed);
    }

    [Theory]
    [InlineData(null, "fix_result_unreadable")]
    [InlineData("{ not json", "fix_result_unreadable")]
    public async Task A_result_that_cannot_be_read_is_never_half_delivered(string? json, string reason)
    {
        var store = new FakeFixStore(Pending(json));
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado);

        await Publisher(store, pullRequests).ProcessPendingAsync();

        Assert.Equal((RunId, reason), Assert.Single(store.Failed));
        Assert.Empty(pullRequests.Published);
    }

    [Fact]
    public async Task A_result_that_asks_for_a_non_draft_pull_request_or_lacks_its_files_is_refused()
    {
        var notDraft = JsonNode.Parse(VerifiedJson)!;
        notDraft["pull_request"]!["is_draft"] = false;
        var noFiles = JsonNode.Parse(VerifiedJson)!;
        noFiles["patch"]!["files"] = new JsonArray();
        var store = new FakeFixStore(Pending(notDraft.ToJsonString()), Pending(noFiles.ToJsonString()));
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado);

        await Publisher(store, pullRequests).ProcessPendingAsync();

        Assert.Equal(["pull_request_not_draft", "candidate_files_missing"], store.Failed.Select(failed => failed.Reason));
        Assert.Empty(pullRequests.Published);
    }

    [Fact]
    public async Task A_retry_tells_the_publisher_an_earlier_attempt_may_have_opened_it()
    {
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado);

        await Publisher(new FakeFixStore(Pending(VerifiedJson, attempt: 3)), pullRequests).ProcessPendingAsync();

        Assert.True(Assert.Single(pullRequests.Published).MayAlreadyExist);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_later_with_backoff()
    {
        var store = new FakeFixStore(Pending(VerifiedJson, attempt: 2));
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado) { Failure = new FixPublishException("ado_http_503", permanent: false) };

        await Publisher(store, pullRequests).ProcessPendingAsync();

        Assert.Empty(store.Published);
        Assert.Empty(store.Failed);
        Assert.Equal((RunId, TimeSpan.FromSeconds(30)), Assert.Single(store.Deferred));
        Assert.Equal([15, 30, 60, 900, 900], Attempts.Select(FixPullRequestPublisher.RetryDelaySeconds));
    }

    [Fact]
    public async Task Checks_that_could_not_be_posted_leave_the_delivery_open_for_a_retry()
    {
        var store = new FakeFixStore(Pending(VerifiedJson));
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado) { CheckFailure = new FixPublishException("ado_unreachable", permanent: false) };

        await Publisher(store, pullRequests).ProcessPendingAsync();

        Assert.Single(pullRequests.Published); // the pull request exists; the retry finds it instead of opening another
        Assert.Empty(store.Published);
        Assert.Single(store.Deferred);
    }

    [Theory]
    [InlineData(true, 1, "ado_pull_request_not_permitted")]
    [InlineData(false, FixPullRequestPublisher.MaxAttempts, "publish_attempts_exhausted")]
    public async Task A_permanent_failure_or_too_many_attempts_ends_as_failed(bool permanent, int attempt, string reason)
    {
        var store = new FakeFixStore(Pending(VerifiedJson, attempt));
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado)
        {
            Failure = new FixPublishException(permanent ? "ado_pull_request_not_permitted" : "ado_http_500", permanent),
        };

        await Publisher(store, pullRequests).ProcessPendingAsync();

        Assert.Equal((RunId, reason), Assert.Single(store.Failed));
        Assert.Empty(store.Deferred);
    }

    [Fact]
    public async Task An_unexpected_error_is_retried_and_its_text_is_never_stored()
    {
        var store = new FakeFixStore(Pending(VerifiedJson), Pending(VerifiedJson, FixPullRequestPublisher.MaxAttempts));
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado) { Failure = new InvalidOperationException("token=secret") };

        await Publisher(store, pullRequests).ProcessPendingAsync();

        Assert.Single(store.Deferred);
        Assert.Equal("publish_attempts_exhausted", Assert.Single(store.Failed).Reason);
    }

    // ---- S04-05: the Azure DevOps adapter ----------------------------------------------------------

    [Fact]
    public async Task The_adapter_pushes_one_commit_on_a_new_branch_and_opens_a_draft_linked_to_the_work_item()
    {
        var handler = new FakeHandler(
            Reply($$"""{"commits": [{"commitId": "{{PushedSha}}"}]}"""),
            Reply($$"""{"commitId": "{{PushedSha}}", "treeId": "{{Verified.Patch!.TreeSha}}"}"""),
            Reply("""{"pullRequestId": 812, "isDraft": true}"""),
            Reply($$"""{"lastMergeSourceCommit": {"commitId": "{{PushedSha}}"} }"""),
            Reply($$"""{"treeId": "{{Verified.Patch!.TreeSha}}"}"""));

        var opened = await Ado(handler).PublishAsync("Payments Core", Verified, mayAlreadyExist: false, default);

        Assert.Equal(new PublishedPullRequest("812", PushedSha), opened);
        Assert.Equal(5, handler.Requests.Count);
        var push = handler.Requests[0];
        Assert.Equal((HttpMethod.Post, Repository + "/pushes?api-version=7.1"), (push.Method, push.Uri));
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":bot-pr-token")), push.Authorization);
        var pushed = JsonNode.Parse(push.Body!)!;
        var reference = pushed["refUpdates"]![0]!;
        Assert.Equal("refs/heads/" + Verified.PullRequest!.SourceBranch, reference["name"]!.GetValue<string>());
        Assert.Equal(Verified.BaseSha, reference["oldObjectId"]!.GetValue<string>()); // branches from the pinned baseline
        var changes = pushed["commits"]![0]!["changes"]!.AsArray();
        Assert.Equal(
            [("add", "/tests/test_captured_payment.py"), ("edit", "/src/kiosk/retry_policy.py")],
            changes.Select(change => (change!["changeType"]!.GetValue<string>(), change["item"]!["path"]!.GetValue<string>())));
        Assert.Equal(Verified.Patch.Files[0].Content, changes[1]!["newContent"]!["content"]!.GetValue<string>());
        Assert.Equal("rawtext", changes[1]!["newContent"]!["contentType"]!.GetValue<string>());

        Assert.Equal((HttpMethod.Get, $"{Repository}/commits/{PushedSha}?api-version=7.1"), (handler.Requests[1].Method, handler.Requests[1].Uri));

        var create = handler.Requests[2];
        Assert.Equal((HttpMethod.Post, Repository + "/pullrequests?api-version=7.1"), (create.Method, create.Uri));
        var body = JsonNode.Parse(create.Body!)!;
        Assert.True(body["isDraft"]!.GetValue<bool>());
        Assert.Equal("refs/heads/main", body["targetRefName"]!.GetValue<string>());
        Assert.Equal("4711", body["workItemRefs"]![0]!["id"]!.GetValue<string>());
        // The description is the stored one plus the commit that was really pushed (the sandbox commit it
        // cites does not exist in the repository).
        var description = body["description"]!.GetValue<string>();
        Assert.StartsWith(Verified.PullRequest.Description[..200], description, StringComparison.Ordinal);
        Assert.EndsWith($"Pushed as commit `{PushedSha}` (tree `{Verified.Patch.TreeSha}`, identical to the tested tree).", description, StringComparison.Ordinal);
        Assert.True(description.Length <= 4000);
        // HG-01: the bot does not approve, does not set auto-complete and does not complete.
        Assert.Null(body["autoCompleteSetBy"]);
        Assert.Null(body["completionOptions"]);
        Assert.Null(body["reviewers"]);
        Assert.Null(body["status"]);
    }

    [Fact]
    public async Task A_pushed_commit_whose_tree_differs_from_the_tested_tree_is_never_offered_for_review()
    {
        var handler = new FakeHandler(
            Reply($$"""{"commits": [{"commitId": "{{PushedSha}}"}]}"""),
            Reply($$"""{"commitId": "{{PushedSha}}", "treeId": "{{new string('9', 40)}}"}"""));

        var error = await Assert.ThrowsAsync<FixPublishException>(() =>
            Ado(handler).PublishAsync("Payments Core", Verified, mayAlreadyExist: false, default));

        Assert.Equal(("pushed_tree_differs_from_tested_tree", true), (error.Reason, error.Permanent));
        Assert.DoesNotContain(handler.Requests, sent => sent.Uri.Contains("/pullrequests", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_retry_finds_the_pull_request_an_earlier_attempt_opened_instead_of_opening_a_second()
    {
        var handler = new FakeHandler(
            Reply($$"""{"value": [{"pullRequestId": 812, "targetRefName": "refs/heads/main", "lastMergeSourceCommit": {"commitId": "{{PushedSha}}"} }]}"""),
            Reply($$"""{"treeId": "{{Verified.Patch!.TreeSha}}"}"""));

        var opened = await Ado(handler).PublishAsync("Payments Core", Verified, mayAlreadyExist: true, default);

        Assert.Equal(new PublishedPullRequest("812", PushedSha), opened);
        Assert.Equal(2, handler.Requests.Count);
        var search = handler.Requests[0];
        Assert.Equal(HttpMethod.Get, search.Method);
        Assert.Contains("searchCriteria.sourceRefName=refs%2Fheads%2Fvisioniq%2Ffix%2F4711-", search.Uri, StringComparison.Ordinal);
        // Only an active pull request is taken over; an abandoned or completed one is somebody's decision.
        Assert.Contains("searchCriteria.status=active", search.Uri, StringComparison.Ordinal);
        // Its head is checked against the tested tree like a fresh push.
        Assert.Equal((HttpMethod.Get, $"{Repository}/commits/{PushedSha}?api-version=7.1"), (handler.Requests[1].Method, handler.Requests[1].Uri));
    }

    [Fact]
    public async Task A_retry_never_shows_results_on_a_pull_request_whose_head_was_not_tested()
    {
        // The earlier attempt opened the pull request; before the retry somebody pushed to its branch.
        var handler = new FakeHandler(
            Reply($$"""{"value": [{"pullRequestId": 812, "lastMergeSourceCommit": {"commitId": "{{NewHeadSha}}"} }]}"""),
            Reply($$"""{"treeId": "{{new string('9', 40)}}"}"""),
            Reply("{}"), Reply("{}"), Reply("{}"), Reply("{}"), Reply("{}"), Reply("{}"));

        var error = await Assert.ThrowsAsync<FixPublishException>(() =>
            Ado(handler).PublishAsync("Payments Core", Verified, mayAlreadyExist: true, default));

        Assert.Equal(("pull_request_head_not_tested", true), (error.Reason, error.Permanent));
        // Every check is posted as pending, so a status from the earlier attempt does not stay green.
        var statuses = handler.Requests.Skip(2).ToList();
        Assert.Equal(6, statuses.Count);
        Assert.All(statuses, sent =>
        {
            Assert.EndsWith("/pullRequests/812/statuses?api-version=7.1", sent.Uri, StringComparison.Ordinal);
            Assert.Equal("pending", JsonNode.Parse(sent.Body!)!["state"]!.GetValue<string>());
        });
        Assert.DoesNotContain(handler.Requests, sent => sent.Uri.Contains("/pushes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_retry_that_finds_the_pull_request_without_its_head_reads_the_branch_and_opens_no_second_one()
    {
        var branch = "refs/heads/" + Verified.PullRequest!.SourceBranch;
        var handler = new FakeHandler(
            Reply("""{"value": [{"pullRequestId": 812}]}"""),
            Reply($$"""{"value": [{"name": "{{branch}}", "objectId": "{{PushedSha}}"}]}"""),
            Reply($$"""{"treeId": "{{Verified.Patch!.TreeSha}}"}"""));

        var opened = await Ado(handler).PublishAsync("Payments Core", Verified, mayAlreadyExist: true, default);

        Assert.Equal(new PublishedPullRequest("812", PushedSha), opened);
        Assert.DoesNotContain(handler.Requests, sent => sent.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task A_pull_request_into_another_branch_is_not_taken_over()
    {
        var handler = new FakeHandler(
            Reply($$"""{"value": [{"pullRequestId": 700, "targetRefName": "refs/heads/release", "lastMergeSourceCommit": {"commitId": "{{NewHeadSha}}"} }]}"""),
            Reply("""{"value": []}"""),
            Reply($$"""{"commits": [{"commitId": "{{PushedSha}}"}]}"""),
            Reply($$"""{"treeId": "{{Verified.Patch!.TreeSha}}"}"""),
            Reply("""{"pullRequestId": 812}"""),
            Reply($$"""{"lastMergeSourceCommit": {"commitId": "{{PushedSha}}"} }"""),
            Reply($$"""{"treeId": "{{Verified.Patch!.TreeSha}}"}"""));

        var opened = await Ado(handler).PublishAsync("Payments Core", Verified, mayAlreadyExist: true, default);

        Assert.Equal("812", opened.PullRequestId);
    }

    public static TheoryData<string, string> UnusableResults() => new()
    {
        // Nothing to compare the pushed commit with: the check must not be skipped.
        { "tested_tree_missing", """{"patch": {"tree_sha": null}}""" },
        { "tested_tree_missing", """{"patch": {"tree_sha": "not-a-tree"}}""" },
        // The bot creates branches under visioniq/fix/ only, from a full commit id.
        { "branch_not_allowed", """{"pull_request": {"source_branch": "main"}}""" },
        { "branch_not_allowed", """{"pull_request": {"source_branch": "visioniq/fix/../../main"}}""" },
        { "branch_not_allowed", """{"pull_request": {"target_branch": "a..b"}}""" },
        { "branch_not_allowed", """{"pull_request": {"base_sha": "main"}}""" },
        // A pull request without its work item would be opened silently unlinked.
        { "ticket_id_not_a_work_item", """{"pull_request": {"work_item_id": "KIOSK-12"}}""" },
        { "pull_request_text_too_long", """{"pull_request": {"title": ""}}""" },
    };

    [Theory]
    [MemberData(nameof(UnusableResults))]
    public async Task A_result_that_could_not_be_opened_as_a_pull_request_is_refused_before_anything_is_pushed(string reason, string change)
    {
        var handler = new FakeHandler();

        var error = await Assert.ThrowsAsync<FixPublishException>(() =>
            Ado(handler).PublishAsync("Payments Core", Changed(change), mayAlreadyExist: false, default));

        Assert.Equal((reason, true), (error.Reason, error.Permanent));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_over_long_description_is_refused_before_anything_is_pushed()
    {
        var handler = new FakeHandler();
        var fix = Verified with { PullRequest = Verified.PullRequest! with { Description = new string('x', 4001) } };

        var error = await Assert.ThrowsAsync<FixPublishException>(() => Ado(handler).PublishAsync("Payments Core", fix, false, default));

        Assert.Equal(("pull_request_text_too_long", true), (error.Reason, error.Permanent));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_head_of_a_pull_request_is_what_azure_devops_reports()
    {
        var handler = new FakeHandler(
            Reply($$"""{"pullRequestId": 812, "lastMergeSourceCommit": {"commitId": "{{NewHeadSha.ToUpperInvariant()}}"} }"""),
            Reply("""{"pullRequestId": 812}"""));
        var ado = Ado(handler);

        Assert.Equal(NewHeadSha, await ado.CurrentHeadAsync("Payments Core", "kiosk-demo", "812", default));
        Assert.Null(await ado.CurrentHeadAsync("Payments Core", "kiosk-demo", "812", default));
        Assert.All(handler.Requests, sent =>
            Assert.Equal((HttpMethod.Get, Repository + "/pullrequests/812?api-version=7.1"), (sent.Method, sent.Uri)));
    }

    [Fact]
    public async Task A_retry_reuses_a_branch_that_was_pushed_before_the_failure()
    {
        var branch = "refs/heads/" + Verified.PullRequest!.SourceBranch;
        var handler = new FakeHandler(
            Reply("""{"value": []}"""),
            Reply($$"""{"value": [{"name": "{{branch}}-other", "objectId": "{{NewHeadSha}}"}, {"name": "{{branch}}", "objectId": "{{PushedSha}}"}]}"""),
            Reply($$"""{"treeId": "{{Verified.Patch!.TreeSha}}"}"""),
            Reply("""{"pullRequestId": 813}"""),
            Reply($$"""{"lastMergeSourceCommit": {"commitId": "{{PushedSha}}"} }"""),
            Reply($$"""{"treeId": "{{Verified.Patch!.TreeSha}}"}"""));

        var opened = await Ado(handler).PublishAsync("Payments Core", Verified, mayAlreadyExist: true, default);

        Assert.Equal(new PublishedPullRequest("813", PushedSha), opened);
        Assert.DoesNotContain(handler.Requests, sent => sent.Uri.Contains("/pushes", StringComparison.Ordinal)); // no second commit
    }

    [Fact]
    public async Task Each_check_is_posted_as_a_status_with_the_visioniq_genre()
    {
        var handler = new FakeHandler(Reply("{}"), Reply("{}"));
        FixCheck[] checks =
        [
            new("visioniq/fix-test", "succeeded", "PASS: Outcome PASS."),
            new("visioniq/performance", "not_applicable", "NOT_APPLICABLE: not measured."),
        ];

        await Ado(handler).PostChecksAsync("Payments Core", "kiosk-demo", "812", checks, default);

        Assert.All(handler.Requests, sent =>
            Assert.Equal((HttpMethod.Post, Repository + "/pullRequests/812/statuses?api-version=7.1"), (sent.Method, sent.Uri)));
        var first = JsonNode.Parse(handler.Requests[0].Body!)!;
        Assert.Equal(("succeeded", "fix-test", "visioniq"), (
            first["state"]!.GetValue<string>(), first["context"]!["name"]!.GetValue<string>(), first["context"]!["genre"]!.GetValue<string>()));
        Assert.Equal("notApplicable", JsonNode.Parse(handler.Requests[1].Body!)!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_adapter_only_ever_creates_a_branch_a_pull_request_and_statuses()
    {
        var handler = new FakeHandler(
            Reply($$"""{"commits": [{"commitId": "{{PushedSha}}"}]}"""),
            Reply($$"""{"treeId": "{{Verified.Patch!.TreeSha}}"}"""),
            Reply("""{"pullRequestId": 812}"""),
            Reply($$"""{"lastMergeSourceCommit": {"commitId": "{{PushedSha}}"} }"""),
            Reply($$"""{"treeId": "{{Verified.Patch!.TreeSha}}"}"""),
            Reply("{}"));
        var ado = Ado(handler);

        var opened = await ado.PublishAsync("Payments Core", Verified, mayAlreadyExist: false, default);
        await ado.PostChecksAsync("Payments Core", "kiosk-demo", opened.PullRequestId, [new("visioniq/fix-test", "succeeded", "PASS")], default);

        // No vote (reviewers), no update of the pull request (PATCH completes or abandons it), no deletion.
        Assert.All(handler.Requests, sent => Assert.Contains(sent.Method, new[] { HttpMethod.Get, HttpMethod.Post }));
        Assert.DoesNotContain(handler.Requests, sent => sent.Uri.Contains("/reviewers", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            ["/pushes", "/commits/", "/pullrequests?", "/pullrequests/", "/commits/", "/statuses"],
            handler.Requests.Select(sent => AllowedCalls.First(part => sent.Uri.Contains(part, StringComparison.Ordinal))));
        Assert.DoesNotContain(
            typeof(IFixPullRequestPublisher).GetMethods(),
            method => method.Name.Contains("Approve", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Complete", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Merge", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Vote", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "ado_pull_request_not_permitted", true)]
    [InlineData(HttpStatusCode.Forbidden, "ado_pull_request_not_permitted", true)]
    [InlineData(HttpStatusCode.NonAuthoritativeInformation, "ado_pull_request_not_permitted", true)]
    [InlineData(HttpStatusCode.NotFound, "ado_repository_not_found", true)]
    [InlineData(HttpStatusCode.BadRequest, "ado_request_rejected", true)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "ado_request_rejected", true)]
    [InlineData(HttpStatusCode.Conflict, "ado_http_409", false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ado_http_503", false)]
    public async Task Azure_DevOps_errors_become_fixed_reason_codes(HttpStatusCode status, string reason, bool permanent)
    {
        var error = await Assert.ThrowsAsync<FixPublishException>(() =>
            Ado(new FakeHandler(Reply("{}", status))).PublishAsync("Payments Core", Verified, false, default));

        Assert.Equal((reason, permanent), (error.Reason, error.Permanent));
    }

    [Fact]
    public async Task A_network_failure_is_transient_and_its_message_is_not_kept()
    {
        var error = await Assert.ThrowsAsync<FixPublishException>(() =>
            Ado(new FakeHandler(new HttpRequestException("token=secret leaked"))).PublishAsync("Payments Core", Verified, false, default));

        Assert.Equal(("ado_unreachable", false), (error.Reason, error.Permanent));
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
    }

    // ---- S11-17: a new commit on the pull request ----------------------------------------------------

    [Fact]
    public async Task A_new_commit_on_the_pull_request_makes_every_check_pending_again()
    {
        var store = new FakeFixStore { Opened = OpenedPullRequest() };
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado);
        var watcher = new FixPullRequestHeadWatcher(store, pullRequests, PickupTestData.Policy, NullLogger<FixPullRequestHeadWatcher>.Instance);

        var outcome = await watcher.OnUpdatedAsync(new PullRequestUpdate("repo-guid", "kiosk-demo", "812", NewHeadSha), default);

        Assert.Equal(FixPullRequestHeadWatcher.Invalidated, outcome);
        var posted = Assert.Single(pullRequests.Checks);
        Assert.Equal(("Payments Core", "kiosk-demo", "812"), (posted.Project, posted.RepositoryId, posted.PullRequestId));
        Assert.Equal(6, posted.Checks.Count);
        Assert.All(posted.Checks, check => Assert.Equal("pending", check.State));
        Assert.All(posted.Checks, check => Assert.Contains("Stale: measured on 111111111111", check.Description, StringComparison.Ordinal));
        Assert.Equal((RunId, NewHeadSha), (store.Heads[0].RunId, store.Heads[0].HeadSha));

        // The same delivery again changes nothing; the verified commit coming back restores the results.
        Assert.Equal(FixPullRequestHeadWatcher.Unchanged, await watcher.OnUpdatedAsync(new PullRequestUpdate("repo-guid", "kiosk-demo", "812", NewHeadSha), default));
        Assert.Equal(FixPullRequestHeadWatcher.Restored, await watcher.OnUpdatedAsync(new PullRequestUpdate("repo-guid", "kiosk-demo", "812", PushedSha), default));
        Assert.All(pullRequests.Checks[^1].Checks, check => Assert.Equal("succeeded", check.State));
    }

    [Fact]
    public async Task A_late_delivery_cannot_restore_results_on_a_head_that_has_moved_on()
    {
        // Delivery A (head = the verified commit) was retried by ADO after delivery B (a push) was applied.
        var store = new FakeFixStore { Opened = OpenedPullRequest() with { CurrentHeadSha = NewHeadSha } };
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado) { CurrentHead = NewHeadSha };
        var watcher = new FixPullRequestHeadWatcher(store, pullRequests, PickupTestData.Policy, NullLogger<FixPullRequestHeadWatcher>.Instance);

        var outcome = await watcher.OnUpdatedAsync(new PullRequestUpdate("repo-guid", "kiosk-demo", "812", PushedSha), default);

        // Azure DevOps says the head is still the new commit: nothing is restored, nothing is posted.
        Assert.Equal(FixPullRequestHeadWatcher.Unchanged, outcome);
        Assert.Empty(pullRequests.Checks);
        Assert.Empty(store.Heads);
    }

    [Fact]
    public async Task The_head_azure_devops_reports_wins_over_the_one_in_the_delivery()
    {
        var store = new FakeFixStore { Opened = OpenedPullRequest() };
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado) { CurrentHead = NewHeadSha };
        var watcher = new FixPullRequestHeadWatcher(store, pullRequests, PickupTestData.Policy, NullLogger<FixPullRequestHeadWatcher>.Instance);

        // The body claims the verified commit; the pull request is really on another one.
        var outcome = await watcher.OnUpdatedAsync(new PullRequestUpdate("repo-guid", "kiosk-demo", "812", PushedSha), default);

        Assert.Equal(FixPullRequestHeadWatcher.Invalidated, outcome);
        Assert.All(Assert.Single(pullRequests.Checks).Checks, check => Assert.Equal("pending", check.State));
        Assert.Equal(NewHeadSha, store.Heads[0].HeadSha);
    }

    [Fact]
    public async Task A_result_that_cannot_be_read_any_more_makes_every_check_pending()
    {
        var store = new FakeFixStore { Opened = OpenedPullRequest() with { FixJson = "{ not json" } };
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado);
        var watcher = new FixPullRequestHeadWatcher(store, pullRequests, null, NullLogger<FixPullRequestHeadWatcher>.Instance);

        Assert.Equal(FixPullRequestHeadWatcher.Invalidated, await watcher.OnUpdatedAsync(new PullRequestUpdate("g", "kiosk-demo", "812", NewHeadSha), default));

        var posted = Assert.Single(pullRequests.Checks).Checks;
        Assert.Equal(6, posted.Count); // the old green statuses are replaced, not left standing
        Assert.All(posted, check => Assert.Equal("pending", check.State));
    }

    [Fact]
    public async Task A_result_for_another_repository_or_baseline_than_the_run_is_never_pushed()
    {
        var otherRepository = Pending(VerifiedJson) with { RepositoryId = "another-repo", BaseSha = Verified.BaseSha };
        var otherBaseline = Pending(VerifiedJson) with { RepositoryId = "kiosk-demo", BaseSha = new string('a', 40) };
        var otherTicket = Pending(VerifiedJson) with { TicketId = "9999" };
        var matching = Pending(VerifiedJson) with { RepositoryId = "kiosk-demo", BaseSha = Verified.BaseSha };
        var store = new FakeFixStore(otherRepository, otherBaseline, otherTicket, matching);
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado);

        await Publisher(store, pullRequests).ProcessPendingAsync();

        Assert.Equal(["fix_result_does_not_match_run", "fix_result_does_not_match_run", "fix_result_does_not_match_run"], store.Failed.Select(failed => failed.Reason));
        Assert.Single(pullRequests.Published);
        Assert.Single(store.Published);
    }

    [Fact]
    public async Task Narrowing_the_pilot_list_also_stops_a_delivery_that_was_already_queued()
    {
        var store = new FakeFixStore(Pending(VerifiedJson), Pending(VerifiedJson));
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado);
        var notListed = PickupTestData.Policy with { AllowedWorkItemIds = new HashSet<int> { 9001 } };
        var listed = PickupTestData.Policy with { AllowedWorkItemIds = new HashSet<int> { 4711 } };

        await new FixPullRequestPublisher(store, pullRequests, notListed, NullLogger<FixPullRequestPublisher>.Instance).ProcessPendingAsync(1);
        await new FixPullRequestPublisher(store, pullRequests, listed, NullLogger<FixPullRequestPublisher>.Instance).ProcessPendingAsync(1);

        Assert.Equal("work_item_not_allowlisted", Assert.Single(store.Skipped).Reason);
        Assert.Single(pullRequests.Published);
    }

    [Fact]
    public async Task A_result_with_a_missing_file_entry_fails_once_instead_of_being_retried_for_ever()
    {
        var broken = JsonNode.Parse(VerifiedJson)!;
        broken["patch"]!["files"]!.AsArray().Add(null);
        var store = new FakeFixStore(Pending(broken.ToJsonString()));

        await Publisher(store, new FakePullRequests(FixPullRequestMode.Ado)).ProcessPendingAsync();

        Assert.Equal("candidate_files_missing", Assert.Single(store.Failed).Reason);
    }

    [Fact]
    public async Task A_pull_request_visioniq_did_not_open_is_left_alone()
    {
        var store = new FakeFixStore { Opened = OpenedPullRequest() };
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado);
        var watcher = new FixPullRequestHeadWatcher(store, pullRequests, null, NullLogger<FixPullRequestHeadWatcher>.Instance);

        Assert.Equal(FixPullRequestHeadWatcher.UnknownPullRequest, await watcher.OnUpdatedAsync(new PullRequestUpdate("g", "kiosk-demo", "999", NewHeadSha), default));
        Assert.Equal(FixPullRequestHeadWatcher.UnknownPullRequest, await watcher.OnUpdatedAsync(new PullRequestUpdate("g", "other-repo", "812", NewHeadSha), default));
        Assert.Empty(pullRequests.Checks);
        Assert.Empty(store.Heads);
    }

    [Fact]
    public void A_pull_request_updated_delivery_is_read_and_other_events_are_not()
    {
        var body = $$"""
            {"id": "e1", "eventType": "git.pullrequest.updated",
             "resource": {"pullRequestId": 812, "title": "ignored",
                          "repository": {"id": "repo-guid", "name": "kiosk-demo"},
                          "lastMergeSourceCommit": {"commitId": "{{NewHeadSha}}"} } }
            """;

        Assert.Equal(new PullRequestUpdate("repo-guid", "kiosk-demo", "812", NewHeadSha), AdoPullRequestHook.Parse(Encoding.UTF8.GetBytes(body)));
        Assert.Null(AdoPullRequestHook.Parse(Encoding.UTF8.GetBytes(body.Replace("git.pullrequest.updated", "git.pullrequest.created", StringComparison.Ordinal))));
        Assert.Null(AdoPullRequestHook.Parse(Encoding.UTF8.GetBytes(body.Replace(NewHeadSha, "short", StringComparison.Ordinal))));
        // Only a full commit id is a head: anything else would be refused by the database after the statuses
        // were posted. Azure DevOps may send it in upper case.
        Assert.Null(AdoPullRequestHook.Parse(Encoding.UTF8.GetBytes(body.Replace(NewHeadSha, new string('z', 40), StringComparison.Ordinal))));
        var upper = new string('A', 40);
        Assert.Equal(new string('a', 40), AdoPullRequestHook.Parse(Encoding.UTF8.GetBytes(body.Replace(NewHeadSha, upper, StringComparison.Ordinal)))!.HeadSha);
        Assert.Null(AdoPullRequestHook.Parse("not json"u8));
    }

    // ---- Settings and the fix-run trigger --------------------------------------------------------------

    [Fact]
    public void Pull_requests_are_off_by_default()
    {
        var options = Options();

        Assert.Equal((FixPullRequestMode.Off, false), (options.Mode, options.AutoStart));
        Assert.Equal(FixPullRequestMode.Off, FixPullRequestOptions.Disabled.Mode);
    }

    [Fact]
    public void Ado_mode_needs_its_own_token()
    {
        var ado = Options(("FIX_PR_MODE", "ado"), ("ADO_ORG_URL", "https://dev.azure.com/contoso/"), ("ADO_PR_PAT", "pr-token"),
            ("ADO_READ_PAT", "read-token"), ("ADO_COMMENT_PAT", "comment-token"), ("FIX_AUTO_START", "true"));

        Assert.Equal((FixPullRequestMode.Ado, "pr-token", true), (ado.Mode, ado.PullRequestToken, ado.AutoStart));
        Assert.Equal(FixPullRequestMode.Record, Options(("FIX_PR_MODE", "Record")).Mode);
    }

    [Theory]
    [InlineData("FIX_PR_MODE", "live")]
    [InlineData("FIX_PR_INTERVAL_SECONDS", "0")]
    [InlineData("FIX_AUTO_START", "maybe")]
    public void An_invalid_setting_stops_the_service(string name, string value)
    {
        Assert.Throws<InvalidOperationException>(() => Options((name, value)));
    }

    [Theory]
    [InlineData("ADO_PR_PAT", "")]
    [InlineData("ADO_ORG_URL", "http://dev.azure.com/contoso/")]
    [InlineData("ADO_READ_PAT", "pr-token")] // the read token must not be able to write
    [InlineData("ADO_COMMENT_PAT", "pr-token")] // one token per kind of write
    public void Ado_mode_is_refused_when_incomplete_or_when_a_token_is_shared(string name, string value)
    {
        var settings = new Dictionary<string, string>
        {
            ["FIX_PR_MODE"] = "ado",
            ["ADO_ORG_URL"] = "https://dev.azure.com/contoso/",
            ["ADO_PR_PAT"] = "pr-token",
            [name] = value,
        };

        Assert.Throws<InvalidOperationException>(() => Options([.. settings.Select(pair => (pair.Key, pair.Value))]));
    }

    [Theory]
    [InlineData("2026-11-05.1", "2026-11-05.1.fix")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123", "012345678901234567890123456789012345678901234567890123456789.fix")]
    public void The_fix_run_has_its_own_place_in_the_one_run_per_pickup_index(string policy, string expected)
    {
        var revision = FixRunStarter.FixPolicyRevision(policy);

        Assert.Equal(expected, revision);
        Assert.Matches("^[A-Za-z0-9_.:-]{1,64}$", revision);
    }

    // ---- Logging: every step is traceable, and nothing sensitive is written ---------------------------

    [Fact]
    public async Task Opening_a_pull_request_logs_each_step_with_ids_only()
    {
        var handler = new FakeHandler(
            Reply($$"""{"commits": [{"commitId": "{{PushedSha}}"}]}"""),
            Reply($$"""{"treeId": "{{Verified.Patch!.TreeSha}}"}"""),
            Reply("""{"pullRequestId": 812}"""),
            Reply($$"""{"lastMergeSourceCommit": {"commitId": "{{PushedSha}}"} }"""),
            Reply($$"""{"treeId": "{{Verified.Patch!.TreeSha}}"}"""),
            Reply("{}"));
        var log = new CapturingLogger<AdoPullRequestPublisher>();
        var ado = new AdoPullRequestPublisher(new HttpClient(handler), new Uri("https://dev.azure.com/contoso/"), "bot-pr-token", log);

        var opened = await ado.PublishAsync("Payments Core", Verified, mayAlreadyExist: false, default);
        await ado.PostChecksAsync("Payments Core", "kiosk-demo", opened.PullRequestId, [new("visioniq/fix-test", "succeeded", "PASS: ok")], default);

        Assert.Equal(
            [
                (LogLevel.Information, $"Branch {Verified.PullRequest!.SourceBranch} pushed to kiosk-demo from {Verified.BaseSha}: commit {PushedSha}, 2 file(s)"),
                (LogLevel.Debug, $"Pushed commit {PushedSha} has the tested tree {Verified.Patch.TreeSha}"),
                (LogLevel.Information, "Draft pull request 812 created in kiosk-demo for work item 4711"),
                (LogLevel.Debug, "Pull request 812 status visioniq/fix-test: succeeded"),
            ],
            log.Lines.Select(line => (line.Level, line.Message)));
        // No token, no code, no ticket or description text.
        Assert.DoesNotContain("bot-pr-token", log.All, StringComparison.Ordinal);
        Assert.DoesNotContain("not captured", log.All, StringComparison.Ordinal);
        Assert.DoesNotContain("charged twice", log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_azure_devops_call_is_logged_with_its_operation_and_reason_code()
    {
        var log = new CapturingLogger<AdoPullRequestPublisher>();
        var ado = new AdoPullRequestPublisher(
            new HttpClient(new FakeHandler(Reply("""{"message": "TF401027: token=secret"}""", HttpStatusCode.Forbidden))),
            new Uri("https://dev.azure.com/contoso/"), "bot-pr-token", log);

        await Assert.ThrowsAsync<FixPublishException>(() => ado.PublishAsync("Payments Core", Verified, false, default));

        Assert.Equal((LogLevel.Warning, "Azure DevOps POST pushes failed: ado_pull_request_not_permitted"), (log.Lines[0].Level, log.Lines[0].Message));
        Assert.DoesNotContain("secret", log.All, StringComparison.Ordinal); // ADO's own message is never copied
    }

    [Fact]
    public async Task A_retry_and_a_tree_mismatch_say_what_was_found()
    {
        var branch = "refs/heads/" + Verified.PullRequest!.SourceBranch;
        var log = new CapturingLogger<AdoPullRequestPublisher>();
        var ado = new AdoPullRequestPublisher(
            new HttpClient(new FakeHandler(
                Reply("""{"value": []}"""),
                Reply($$"""{"value": [{"name": "{{branch}}", "objectId": "{{PushedSha}}"}]}"""),
                Reply($$"""{"treeId": "{{new string('9', 40)}}"}"""))),
            new Uri("https://dev.azure.com/contoso/"), "bot-pr-token", log);

        await Assert.ThrowsAsync<FixPublishException>(() => ado.PublishAsync("Payments Core", Verified, mayAlreadyExist: true, default));

        Assert.Contains(log.Lines, line => line.Level == LogLevel.Information && line.Message.StartsWith("Retry found branch", StringComparison.Ordinal));
        Assert.Contains(log.Lines, line => line.Level == LogLevel.Error && line.Message.Contains("does not have the tested tree", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_delivery_worker_logs_what_it_did_with_each_result()
    {
        var log = new CapturingLogger<FixPullRequestPublisher>();
        var store = new FakeFixStore(Pending(VerifiedJson), Pending(BlockedJson), Pending(null));
        var publisher = new FixPullRequestPublisher(store, new FakePullRequests(FixPullRequestMode.Ado), PickupTestData.Policy, log);

        await publisher.ProcessPendingAsync();

        Assert.Equal(
            [
                (LogLevel.Debug, $"Delivering the fix result of run {RunId} (payments-core #4711), attempt 1"),
                (LogLevel.Information, $"Draft pull request 812 for run {RunId} (payments-core #4711) opened via Ado with 6 checks"),
                (LogLevel.Debug, $"Delivering the fix result of run {RunId} (payments-core #4711), attempt 1"),
                (LogLevel.Information, $"No pull request for run {RunId}: the fix result is BLOCKED"),
                (LogLevel.Debug, $"Delivering the fix result of run {RunId} (payments-core #4711), attempt 1"),
                (LogLevel.Error, $"Pull request for run {RunId} could not be opened: fix_result_unreadable"),
            ],
            log.Lines.Select(line => (line.Level, line.Message)));
    }

    [Fact]
    public async Task A_head_change_and_an_ignored_update_are_logged()
    {
        var log = new CapturingLogger<FixPullRequestHeadWatcher>();
        var watcher = new FixPullRequestHeadWatcher(
            new FakeFixStore { Opened = OpenedPullRequest() }, new FakePullRequests(FixPullRequestMode.Ado), null, log);

        await watcher.OnUpdatedAsync(new PullRequestUpdate("g", "kiosk-demo", "999", NewHeadSha), default);
        await watcher.OnUpdatedAsync(new PullRequestUpdate("g", "kiosk-demo", "812", NewHeadSha), default);

        Assert.Equal(
            [
                (LogLevel.Debug, "Pull request 999 update ignored: not_a_visioniq_pull_request"),
                (LogLevel.Information, $"Pull request 812 of run {RunId} has a new head: checks_invalidated"),
            ],
            log.Lines.Select(line => (line.Level, line.Message)));
    }

    private static FixPullRequestPublisher Publisher(IFixPullRequestStore store, IFixPullRequestPublisher pullRequests) =>
        new(store, pullRequests, PickupTestData.Policy, NullLogger<FixPullRequestPublisher>.Instance);

    private static AdoPullRequestPublisher Ado(FakeHandler handler) =>
        new(new HttpClient(handler), new Uri("https://dev.azure.com/contoso/"), "bot-pr-token");

    /// <summary>The verified result with some fields replaced (a JSON object merged over it, two levels deep).</summary>
    private static FixDocument Changed(string change)
    {
        var document = JsonNode.Parse(VerifiedJson)!;
        foreach (var (section, values) in JsonNode.Parse(change)!.AsObject())
        {
            foreach (var (name, value) in values!.AsObject())
            {
                document[section]![name] = value?.DeepClone();
            }
        }

        return FixDocument.Parse(document.ToJsonString())!;
    }

    private static OpenedFixPullRequest OpenedPullRequest() =>
        new(RunId, "payments-core", "kiosk-demo", "812", PushedSha, PushedSha, VerifiedJson);

    private static FixPullRequestOptions Options(params (string Name, string Value)[] settings) =>
        FixPullRequestOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(pair => pair.Name, pair => (string?)pair.Value))
            .Build());
}
