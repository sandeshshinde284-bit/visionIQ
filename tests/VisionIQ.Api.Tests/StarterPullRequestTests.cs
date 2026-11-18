using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using VisionIQ.Api.Features.Briefs;
using VisionIQ.Api.Features.Feedback;
using VisionIQ.Api.Features.Fixes;
using VisionIQ.Api.Features.Pickup;
using VisionIQ.Api.Features.Starters;
using static VisionIQ.Api.Tests.BriefTestData;
using static VisionIQ.Api.Tests.FixTestData;
using static VisionIQ.Api.Tests.PickupTestData;

namespace VisionIQ.Api.Tests;

/// <summary>
/// ENH-02: the optional starter pull request of an enhancement. A starter is asked for by a comment on the ticket
/// or by policy (PROP-04), and delivered as a draft pull request linked to the work item through the same path as
/// a fix (PROP-06). Not getting one is never an error.
/// </summary>
public sealed class StarterPullRequestTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Repository = "https://dev.azure.com/contoso/Payments%20Core/_apis/git/repositories/kiosk-demo";
    private static readonly string StarterJson = File.ReadAllText(
        Path.Combine(EventContractTests.ContractsDirectory, "..", "fixes", "fixtures", "fix-result.starter.v1.json"));

    private static readonly FixDocument Starter = FixDocument.Parse(StarterJson)!;
    private readonly string policyFile = WritePolicy();

    // ---- Asking: one line on the ticket ---------------------------------------------------------------

    [Theory]
    [InlineData("visioniq starter-pr")]
    [InlineData("VisionIQ Starter PR")]
    [InlineData("/visioniq starter pr please")]
    [InlineData("@visioniq: starter pull request")]
    [InlineData("- visioniq starter_pr")]
    [InlineData("<div>Thanks for the guidance.</div><div>visioniq starter-pr</div>")]
    [InlineData("<p>ADV-001 accepted</p><p>visioniq&nbsp;starter-pr</p>")]
    public void A_line_that_starts_with_the_command_asks_for_a_starter(string comment)
    {
        Assert.True(StarterCommand.IsRequested(comment));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ADV-001 accepted")]
    [InlineData("Should we try the visioniq starter-pr thing?")] // mentioned, not asked: it must start the line
    [InlineData("visioniq starter")]
    [InlineData("visioniq starter-project")]
    [InlineData("visioniqstarter-pr")]
    public void Anything_else_asks_for_nothing(string? comment)
    {
        Assert.False(StarterCommand.IsRequested(comment));
    }

    [Fact]
    public void VisionIQs_own_guidance_shows_the_command_and_never_asks_for_itself()
    {
        var offered = RenderGuidance(offerStarter: true);
        var notOffered = RenderGuidance(offerStarter: false);

        Assert.Contains("<code>visioniq starter-pr</code>", offered, StringComparison.Ordinal);
        Assert.Contains("It is optional", offered, StringComparison.Ordinal);
        Assert.DoesNotContain("starter", notOffered, StringComparison.OrdinalIgnoreCase);
        Assert.False(StarterCommand.IsRequested(offered));
    }

    [Fact]
    public void The_command_is_offered_only_on_published_enhancement_guidance()
    {
        var partial = RenderGuidance(offerStarter: true, state: "PARTIAL", "weak_evidence");
        var defect = BriefCommentRenderer.Render(Target(), BriefDocument.Parse(Brief().ToJsonString()), offerStarter: true);

        Assert.DoesNotContain("starter", partial, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("starter", defect, StringComparison.OrdinalIgnoreCase);
    }

    // ---- When it is asked for (PROP-04) ---------------------------------------------------------------

    [Theory]
    [InlineData(null, StarterPullRequestMode.Off)]
    [InlineData("", StarterPullRequestMode.Off)]
    [InlineData("off", StarterPullRequestMode.Off)]
    [InlineData("on_request", StarterPullRequestMode.OnRequest)]
    [InlineData(" AUTO ", StarterPullRequestMode.Auto)]
    public void The_mode_is_off_unless_it_is_set(string? value, StarterPullRequestMode expected)
    {
        var options = StarterPullRequestOptions.FromConfiguration(Configuration(value));

        Assert.Equal(expected, options.Mode);
        Assert.Equal(expected != StarterPullRequestMode.Off, options.Enabled);
    }

    [Fact]
    public void An_unknown_mode_stops_the_service_instead_of_guessing()
    {
        var error = Assert.Throws<InvalidOperationException>(() => StarterPullRequestOptions.FromConfiguration(Configuration("always")));

        Assert.Equal("STARTER_PR_MODE must be off, on_request or auto.", error.Message);
    }

    [Fact]
    public void A_starter_run_has_its_own_place_next_to_the_guidance_and_the_fix_run()
    {
        Assert.Equal("pilot-2026-11-10.1.starter", StarterRunStarter.StarterPolicyRevision("pilot-2026-11-10.1"));
        Assert.NotEqual(FixRunStarter.FixPolicyRevision("p.1"), StarterRunStarter.StarterPolicyRevision("p.1"));
        var longest = StarterRunStarter.StarterPolicyRevision(new string('r', 64));
        Assert.Equal(64, longest.Length);
        Assert.EndsWith(".starter", longest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_the_mode_off_a_request_is_acknowledged_and_ignored()
    {
        var starters = new FakeStarterStore();
        using var client = CreateClient(starters, mode: null);

        var response = await PostAsync(client, Commented("visioniq starter-pr"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); // never a failure
        Assert.Equal(new StarterResponse("ignored", "starter_pr_not_enabled"), await ReadAsync(response));
        Assert.Empty(starters.Requests);
    }

    [Theory]
    [InlineData("on_request")]
    [InlineData("auto")]
    public async Task An_engineers_request_starts_one_starter_run_for_the_ticket(string mode)
    {
        var run = Guid.NewGuid();
        var starters = new FakeStarterStore { Outcome = new StarterResponse("queued", RunId: run) };
        var pickups = new FakePickupStore();
        using var client = CreateClient(starters, mode, pickups);

        var response = await PostAsync(client, Commented("<div>visioniq starter-pr</div>"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(new StarterResponse("queued", RunId: run), await ReadAsync(response));
        // The run's project id comes from the policy, the ticket from the comment.
        var request = Assert.Single(starters.Requests);
        Assert.Equal(("payments-core", "4711"), (request.ProjectId, request.TicketId));
        Assert.Equal(32, request.TraceId.Length);
        Assert.Empty(pickups.Pickups); // a comment is never a ticket pickup
    }

    [Fact]
    public async Task The_stores_answer_is_passed_back_and_none_of_them_is_an_error()
    {
        var starters = new FakeStarterStore { Outcome = new StarterResponse("duplicate", StarterRunStarter.AlreadyRequested) };
        using var client = CreateClient(starters, "on_request");

        var again = await PostAsync(client, Commented("visioniq starter-pr"));
        starters.Outcome = new StarterResponse("ignored", PostgresStarterRequestStore.NoGuidance);
        var none = await PostAsync(client, Commented("visioniq starter-pr"));
        starters.Outcome = new StarterResponse("ignored", PostgresStarterRequestStore.GuidanceNotPublished);
        var partial = await PostAsync(client, Commented("visioniq starter-pr"));

        Assert.All([again, none, partial], response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        Assert.Equal(new StarterResponse("duplicate", "starter_already_requested"), await ReadAsync(again));
        Assert.Equal(new StarterResponse("ignored", "no_enhancement_guidance"), await ReadAsync(none));
        Assert.Equal(new StarterResponse("ignored", "guidance_not_published"), await ReadAsync(partial));
    }

    [Fact]
    public async Task Requests_from_elsewhere_from_a_bot_or_outside_the_pilot_list_are_ignored()
    {
        var starters = new FakeStarterStore();
        using var client = CreateClient(starters, "on_request");
        using var pilot = CreateClient(starters, "on_request", allowed: "9001");
        var cases = new (HttpClient Client, JsonObject Body, string Reason)[]
        {
            (client, Commented("visioniq starter-pr", organization: "https://dev.azure.com/someone-else/"), "organization_not_allowed"),
            (client, Commented("visioniq starter-pr", project: "Unknown Project"), "project_not_configured"),
            (client, Commented("visioniq starter-pr", actor: Bot), "bot_comment"),
            (pilot, Commented("visioniq starter-pr"), "work_item_not_allowlisted"),
        };

        foreach (var (sender, body, reason) in cases)
        {
            Assert.Equal(new StarterResponse("ignored", reason), await ReadAsync(await PostAsync(sender, body)));
        }

        Assert.Empty(starters.Requests);
    }

    [Fact]
    public async Task Ratings_in_the_same_comment_are_recorded_too()
    {
        var starters = new FakeStarterStore();
        var feedback = new FakeFeedbackStore();
        using var client = CreateClient(starters, "on_request", feedback: feedback);

        var response = await PostAsync(client, Commented("<div>ADV-001 accepted</div><div>visioniq starter-pr</div>"));

        Assert.Equal("queued", (await ReadAsync(response))!.Status);
        Assert.Equal("ADV-001", Assert.Single(Assert.Single(feedback.Submissions).Ratings).AdviceId);
        Assert.Single(starters.Requests);
    }

    [Fact]
    public async Task A_plain_rating_still_never_asks_for_a_starter()
    {
        var starters = new FakeStarterStore();
        using var client = CreateClient(starters, "auto");

        await PostAsync(client, Commented("ADV-001 accepted"));

        Assert.Empty(starters.Requests);
    }

    [Fact]
    public async Task A_storage_outage_asks_ADO_to_retry()
    {
        var starters = new FakeStarterStore { Failure = new TimeoutException() };
        using var client = CreateClient(starters, "on_request");

        var response = await PostAsync(client, Commented("visioniq starter-pr"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // ---- Delivery: a draft, linked to the work item, never green (PROP-06) ------------------------------

    [Fact]
    public void A_starter_result_is_read_as_a_starter_with_the_scaffold_as_its_only_files()
    {
        Assert.True(Starter.IsStarter);
        Assert.False(Verified.IsStarter);
        Assert.Null(Starter.Reproduction?.Test); // no reproduction test to add to the commit
        var files = Starter.CommitFiles();
        Assert.NotNull(files);
        Assert.Equal(
            [("src/kiosk/refund_window.py", "added"), ("tests/test_refund_window.py", "added")],
            files.Select(file => (file.Path, file.Change)));
        Assert.True(Starter.PullRequest!.IsDraft);
        Assert.Equal("4711", Starter.PullRequest.WorkItemId);
        Assert.StartsWith("visioniq/starter/", Starter.PullRequest.SourceBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void A_result_of_an_unknown_kind_is_not_delivered_as_anything()
    {
        var document = JsonNode.Parse(StarterJson)!;
        document["kind"] = "refactoring";

        Assert.Null(FixDocument.Parse(document.ToJsonString()));
    }

    [Fact]
    public async Task A_starter_gets_one_draft_pull_request_and_no_check_it_did_not_earn()
    {
        var store = new FakeFixStore(Pending(StarterJson));
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado);
        var publisher = new FixPullRequestPublisher(store, pullRequests, Policy, NullLogger<FixPullRequestPublisher>.Instance);

        Assert.Equal(1, await publisher.ProcessPendingAsync());
        Assert.Equal(0, await publisher.ProcessPendingAsync());

        var opened = Assert.Single(pullRequests.Published);
        Assert.True(opened.Fix.IsStarter);
        Assert.True(opened.Fix.PullRequest!.IsDraft);
        var checks = Assert.Single(pullRequests.Checks).Checks;
        Assert.Equal(6, checks.Count);
        // Nothing was built or run: only the patch policy can be green.
        Assert.Equal(["visioniq/patch-policy"], checks.Where(check => check.State == "succeeded").Select(check => check.Name));
        Assert.Equal(
            ["visioniq/reproduction", "visioniq/fix-test"],
            checks.Where(check => check.State == "not_applicable").Select(check => check.Name));
        Assert.Equal(3, checks.Count(check => check.State == "pending"));
        Assert.Single(store.Published);
        Assert.Empty(store.Failed);
    }

    [Fact]
    public async Task The_scaffold_is_pushed_under_visioniq_starter_and_opened_as_a_linked_draft()
    {
        var tree = Starter.Patch!.TreeSha!;
        var handler = new FakeHandler(
            Reply($$"""{"commits": [{"commitId": "{{PushedSha}}"}]}"""),
            Reply($$"""{"treeId": "{{tree}}"}"""),
            Reply("""{"pullRequestId": 913}"""),
            Reply($$"""{"lastMergeSourceCommit": {"commitId": "{{PushedSha}}"} }"""),
            Reply($$"""{"treeId": "{{tree}}"}"""));
        var ado = new AdoPullRequestPublisher(new HttpClient(handler), new Uri("https://dev.azure.com/contoso/"), "bot-pr-token");

        var opened = await ado.PublishAsync("Payments Core", Starter, mayAlreadyExist: false, default);

        Assert.Equal(new PublishedPullRequest("913", PushedSha), opened);
        var pushed = JsonNode.Parse(handler.Requests[0].Body!)!;
        Assert.Equal("refs/heads/" + Starter.PullRequest!.SourceBranch, pushed["refUpdates"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(Starter.BaseSha, pushed["refUpdates"]![0]!["oldObjectId"]!.GetValue<string>());
        Assert.Equal(
            [("add", "/src/kiosk/refund_window.py"), ("add", "/tests/test_refund_window.py")],
            pushed["commits"]![0]!["changes"]!.AsArray()
                .Select(change => (change!["changeType"]!.GetValue<string>(), change["item"]!["path"]!.GetValue<string>())));
        var create = handler.Requests[2];
        Assert.Equal((HttpMethod.Post, Repository + "/pullrequests?api-version=7.1"), (create.Method, create.Uri));
        var body = JsonNode.Parse(create.Body!)!;
        Assert.True(body["isDraft"]!.GetValue<bool>());
        Assert.Equal("4711", body["workItemRefs"]![0]!["id"]!.GetValue<string>());
        Assert.Contains("**Status: NOT VERIFIED.**", body["description"]!.GetValue<string>(), StringComparison.Ordinal);
        // HG-01: the bot does not approve, does not set auto-complete and does not complete.
        Assert.Null(body["autoCompleteSetBy"]);
        Assert.Null(body["completionOptions"]);
        Assert.Null(body["reviewers"]);
    }

    [Theory]
    // A starter never goes to a fix branch, and a fix never to a starter branch.
    [InlineData("starter", "visioniq/fix/4711-3b25fca8")]
    [InlineData("starter", "main")]
    [InlineData("fix", "visioniq/starter/4711-3b25fca8")]
    public async Task Each_kind_of_result_may_only_use_its_own_branches(string kind, string branch)
    {
        var document = JsonNode.Parse(kind == "starter" ? StarterJson : VerifiedJson)!;
        document["pull_request"]!["source_branch"] = branch;
        var ado = new AdoPullRequestPublisher(new HttpClient(new FakeHandler()), new Uri("https://dev.azure.com/contoso/"), "bot-pr-token");

        var error = await Assert.ThrowsAsync<FixPublishException>(() =>
            ado.PublishAsync("Payments Core", FixDocument.Parse(document.ToJsonString())!, mayAlreadyExist: false, default));

        Assert.Equal(("branch_not_allowed", true), (error.Reason, error.Permanent));
    }

    [Fact]
    public async Task A_starter_that_was_not_created_is_skipped_not_failed()
    {
        var document = JsonNode.Parse(StarterJson)!;
        document["state"] = "BLOCKED";
        document["verdict"] = "BLOCKED";
        document["pull_request"] = null;
        var store = new FakeFixStore(Pending(document.ToJsonString()));
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado);

        await new FixPullRequestPublisher(store, pullRequests, Policy, NullLogger<FixPullRequestPublisher>.Instance).ProcessPendingAsync();

        Assert.Equal((FixTestData.RunId, "no_draft_pull_request"), Assert.Single(store.Skipped));
        Assert.Empty(store.Failed); // guidance alone completes the enhancement path
        Assert.Empty(pullRequests.Published);
    }

    // ---- Found in review -------------------------------------------------------------------------------

    [Theory]
    [InlineData("> visioniq starter-pr")] // quoting someone else's request
    [InlineData("visioniq starter-pr is not needed yet")]
    [InlineData("visioniq starter-pr?")]
    public void A_quoted_or_continued_line_is_not_a_request(string comment)
    {
        Assert.False(StarterCommand.IsRequested(comment));
    }

    [Theory]
    // What the API recorded the ticket as decides what its result may be.
    [InlineData("starter", "defect")] // a "starter" would skip the reproduction test of a defect's fix
    [InlineData("fix", "enhancement")]
    public async Task A_result_whose_kind_is_not_the_kind_of_its_run_is_never_delivered(string kind, string ticketKind)
    {
        var pending = Pending(kind == "starter" ? StarterJson : VerifiedJson) with { TicketKind = ticketKind };
        var store = new FakeFixStore(pending);
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado);

        await new FixPullRequestPublisher(store, pullRequests, Policy, NullLogger<FixPullRequestPublisher>.Instance).ProcessPendingAsync();

        Assert.Equal((FixTestData.RunId, "fix_result_does_not_match_run"), Assert.Single(store.Failed));
        Assert.Empty(pullRequests.Published);
    }

    [Fact]
    public async Task A_result_of_the_runs_own_kind_is_delivered()
    {
        var store = new FakeFixStore(Pending(StarterJson) with { TicketKind = "enhancement" });
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado);

        await new FixPullRequestPublisher(store, pullRequests, Policy, NullLogger<FixPullRequestPublisher>.Instance).ProcessPendingAsync();

        Assert.Single(pullRequests.Published);
        Assert.Empty(store.Failed);
    }

    [Fact]
    public async Task A_starter_that_claims_a_passed_check_it_cannot_have_is_still_not_shown_green()
    {
        var document = JsonNode.Parse(StarterJson)!;
        foreach (var gate in document["gates"]!.AsArray())
        {
            gate!["status"] = "PASS"; // a stored document that says everything passed
        }

        var store = new FakeFixStore(Pending(document.ToJsonString()));
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado);

        await new FixPullRequestPublisher(store, pullRequests, Policy, NullLogger<FixPullRequestPublisher>.Instance).ProcessPendingAsync();

        var checks = Assert.Single(pullRequests.Checks).Checks;
        Assert.Equal(["visioniq/patch-policy"], checks.Where(check => check.State == "succeeded").Select(check => check.Name));
        Assert.Equal(5, checks.Count(check => check.State == "pending"));
    }

    [Fact]
    public async Task After_the_engineers_first_commit_nothing_is_green_and_what_does_not_apply_still_does_not()
    {
        var store = new FakeFixStore
        {
            Opened = new OpenedFixPullRequest(FixTestData.RunId, "payments-core", "kiosk-demo", "913", PushedSha, PushedSha, StarterJson),
        };
        var pullRequests = new FakePullRequests(FixPullRequestMode.Ado);
        var watcher = new FixPullRequestHeadWatcher(store, pullRequests, Policy, NullLogger<FixPullRequestHeadWatcher>.Instance);

        var outcome = await watcher.OnUpdatedAsync(new PullRequestUpdate("repo-guid", "kiosk-demo", "913", NewHeadSha), default);

        Assert.Equal(FixPullRequestHeadWatcher.Invalidated, outcome);
        var checks = Assert.Single(pullRequests.Checks).Checks.ToDictionary(check => check.Name, check => check);
        Assert.DoesNotContain(checks.Values, check => check.State == "succeeded");
        // The scaffold's policy check was about the scaffold; the engineer's commit is not covered by it.
        Assert.Equal("pending", checks["visioniq/patch-policy"].State);
        Assert.StartsWith("NOT_RUN: Stale", checks["visioniq/patch-policy"].Description, StringComparison.Ordinal);
        // An enhancement still has no defect to reproduce: not "stale", not pending.
        Assert.Equal("not_applicable", checks["visioniq/reproduction"].State);
        Assert.Equal("not_applicable", checks["visioniq/fix-test"].State);
        Assert.DoesNotContain("Stale", checks["visioniq/reproduction"].Description, StringComparison.Ordinal);
    }

    [Fact]
    public void The_command_is_only_offered_when_asking_is_how_a_starter_is_created()
    {
        Assert.True(Offers("on_request", "record"));
        Assert.False(Offers("auto", "record")); // already on its way: a reply could only be a duplicate
        Assert.False(Offers("on_request", "off")); // it could be prepared but never opened
        Assert.False(Offers(null, "record"));
    }

    public void Dispose() => File.Delete(policyFile);

    /// <summary>Whether the service, configured like this, tells the engineer how to ask.</summary>
    private bool Offers(string? starterMode, string fixMode)
    {
        using var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("DB_API_USER", "visioniq_api");
            builder.UseSetting("DB_API_PASSWORD", "test-only-api-password");
            builder.UseSetting("PICKUP_POLICY_FILE", policyFile);
            builder.UseSetting("PICKUP_WEBHOOK_SECRET", Secret);
            builder.UseSetting("BRIEF_PUBLISH_MODE", "record");
            builder.UseSetting("STARTER_PR_MODE", starterMode);
            builder.UseSetting("FIX_PR_MODE", fixMode);
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        });
        var publisher = configured.Services.GetRequiredService<BriefPublisher>();
        var offer = typeof(BriefPublisher)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(bool));
        return (bool)offer.GetValue(publisher)!;
    }

    private static IConfiguration Configuration(string? mode) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["STARTER_PR_MODE"] = mode }).Build();

    private static string RenderGuidance(bool offerStarter, string state = "PUBLISHED", params string[] reasons)
    {
        var brief = Brief();
        brief["kind"] = "enhancement";
        brief["advice"]![0]!["section"] = "related_past_changes";
        brief["advice"]![1]!["section"] = "do_and_dont";
        return BriefCommentRenderer.Render(Target(state, reasons), BriefDocument.Parse(brief.ToJsonString()), offerStarter);
    }

    private static string WritePolicy()
    {
        var path = Path.Combine(Path.GetTempPath(), "visioniq-starter-policy-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, PolicyJson);
        return path;
    }

    private static async Task<StarterResponse?> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<StarterResponse>();

    private HttpClient CreateClient(
        IStarterRequestStore starters, string? mode, ITicketPickupStore? pickups = null, IAdviceFeedbackStore? feedback = null,
        string? allowed = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("DB_API_USER", "visioniq_api");
            builder.UseSetting("DB_API_PASSWORD", "test-only-api-password");
            builder.UseSetting("PICKUP_POLICY_FILE", policyFile);
            builder.UseSetting("PICKUP_WEBHOOK_SECRET", Secret);
            builder.UseSetting("PICKUP_ALLOWED_WORK_ITEM_IDS", allowed);
            builder.UseSetting("STARTER_PR_MODE", mode);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(pickups ?? new FakePickupStore());
                services.RemoveAll<IAdviceFeedbackStore>();
                services.AddSingleton(feedback ?? new FakeFeedbackStore());
                services.RemoveAll<IStarterRequestStore>();
                services.AddSingleton(starters);
                services.RemoveAll<IHostedService>();
            });
        }).CreateClient();

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, JsonNode body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/ado")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("visioniq:" + Secret)));
        return await client.SendAsync(request);
    }

    private sealed record Requested(string TenantId, string ProjectId, string TicketId, string TraceId);

    private sealed class FakeStarterStore : IStarterRequestStore
    {
        public List<Requested> Requests { get; } = [];

        public StarterResponse Outcome { get; set; } = new("queued", RunId: Guid.NewGuid());

        public Exception? Failure { get; init; }

        public Task<StarterResponse> RequestAsync(
            string tenantId, string projectId, string ticketId, string traceId, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Requests.Add(new Requested(tenantId, projectId, ticketId, traceId));
            return Task.FromResult(Outcome);
        }
    }

    private sealed class FakeFeedbackStore : IAdviceFeedbackStore
    {
        public List<FeedbackSubmission> Submissions { get; } = [];

        public Task<FeedbackOutcome> RecordAsync(FeedbackSubmission submission, CancellationToken cancellationToken)
        {
            Submissions.Add(submission);
            return Task.FromResult(new FeedbackOutcome(
                "recorded", null, [.. submission.Ratings.Select(rating => rating.AdviceId)], []));
        }
    }
}
