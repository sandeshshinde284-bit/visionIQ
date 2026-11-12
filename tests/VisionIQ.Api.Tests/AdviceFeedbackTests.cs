using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using VisionIQ.Api.Features.Feedback;
using VisionIQ.Api.Features.Pickup;
using static VisionIQ.Api.Tests.BriefTestData;
using static VisionIQ.Api.Tests.PickupTestData;

namespace VisionIQ.Api.Tests;

/// <summary>
/// BRF-06 (S13-05, S10-09): an engineer rates advice by replying on the ticket; the rating is stored against the
/// advice id with who and when, and a comment never starts a run.
/// </summary>
public sealed class AdviceFeedbackTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly string policyFile = WritePolicy();

    // ---- Reading ratings out of a comment ---------------------------------------------------------

    [Theory]
    [InlineData("ADV-001 accepted", "ADV-001", AdviceDisposition.Accepted, null)]
    [InlineData("adv-002 Rejected: we use a queue here, not a lock", "ADV-002", AdviceDisposition.Rejected, "we use a queue here, not a lock")]
    [InlineData("ADV-003 modified - kept the retry but added a dedupe key", "ADV-003", AdviceDisposition.Modified, "kept the retry but added a dedupe key")]
    [InlineData("ADV-004 n/a", "ADV-004", AdviceDisposition.NotApplicable, null)]
    [InlineData("ADV-005: not applicable, different printer model", "ADV-005", AdviceDisposition.NotApplicable, "different printer model")]
    [InlineData("- ADV-006 = accept", "ADV-006", AdviceDisposition.Accepted, null)]
    [InlineData("<div>ADV-007&nbsp;rejected &amp; reverted</div>", "ADV-007", AdviceDisposition.Rejected, "& reverted")]
    public void One_line_rates_one_advice_item(string comment, string id, AdviceDisposition disposition, string? note)
    {
        Assert.Equal(new AdviceRating(id, disposition, note), Assert.Single(AdviceFeedbackParser.Parse(comment)));
    }

    [Fact]
    public void A_comment_can_rate_several_items_and_the_last_line_for_an_id_wins()
    {
        var ratings = AdviceFeedbackParser.Parse(
            "<div>Thanks!</div><div>ADV-002 rejected: wrong layer</div><div>ADV-001 accepted</div>" +
            "<ul><li>ADV-003 n/a</li></ul><p>ADV-002 modified: moved it to the gateway</p>");

        Assert.Equal(
            [
                new AdviceRating("ADV-001", AdviceDisposition.Accepted, null),
                new AdviceRating("ADV-002", AdviceDisposition.Modified, "moved it to the gateway"),
                new AdviceRating("ADV-003", AdviceDisposition.NotApplicable, null),
            ],
            ratings);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Looks good to me, merging tomorrow.")]
    [InlineData("I think ADV-001 is accepted by the team")] // the id must start the line
    [InlineData("ADV-001 seems fine")] // not one of the four dispositions
    [InlineData("ADV-1 accepted")] // not an advice id
    [InlineData("ADV-001 acceptedly")]
    public void A_comment_that_is_not_a_rating_rates_nothing(string? comment)
    {
        Assert.Empty(AdviceFeedbackParser.Parse(comment));
    }

    [Fact]
    public void VisionIQs_own_brief_never_rates_itself()
    {
        // The brief quotes the rating syntax as an example and carries the marker.
        var brief = Features.Briefs.BriefCommentRenderer.Render(
            Target(), Features.Briefs.BriefDocument.Parse(Brief().ToJsonString()));

        Assert.Contains("ADV-001 accepted", brief, StringComparison.Ordinal);
        Assert.Empty(AdviceFeedbackParser.Parse(brief));
    }

    [Fact]
    public void Notes_and_the_number_of_ratings_are_bounded()
    {
        var longNote = AdviceFeedbackParser.Parse("ADV-001 rejected: " + new string('x', 2000));
        var many = AdviceFeedbackParser.Parse(string.Join('\n', Enumerable.Range(1, 60).Select(n => $"ADV-{n:000} accepted")));

        Assert.Equal(AdviceFeedbackParser.MaxNoteLength, Assert.Single(longNote).Note!.Length);
        Assert.Equal(AdviceFeedbackParser.MaxRatingsPerComment, many.Count);
    }

    [Fact]
    public void A_comment_event_is_read_without_keeping_more_than_feedback_needs()
    {
        var body = Encoding.UTF8.GetBytes(Commented("ADV-001 accepted").ToJsonString());

        var delivery = AdviceFeedbackParser.ParseDelivery(body, out var problem);

        Assert.Null(problem);
        Assert.Equal(
            ("c0mment-0000-4000-8000-000000000001", Organization, "Payments Core", 4711, "ADV-001 accepted", "a1b2c3", "dev@contoso.example"),
            (delivery!.EventId, delivery.OrganizationUrl, delivery.Project, delivery.WorkItemId, delivery.Text, delivery.AuthorId, delivery.AuthorName));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 9, 29, 58, 120, TimeSpan.Zero), delivery.CommentedAt);
    }

    [Theory]
    [InlineData("not json", "body_not_json")]
    [InlineData("""{"id": "e", "eventType": "workitem.updated", "resource": {}}""", "not_a_comment_event")]
    [InlineData("""{"id": "e", "eventType": "workitem.commented", "resource": {"id": 1}}""", "resource_details_missing")]
    [InlineData("""{"id": "e", "eventType": "workitem.commented", "resource": {"id": 1, "fields": {"System.TeamProject": "P"}}}""", "resource_details_missing")]
    public void An_unusable_comment_event_has_a_reason(string body, string reason)
    {
        Assert.Null(AdviceFeedbackParser.ParseDelivery(Encoding.UTF8.GetBytes(body), out var problem));
        Assert.Equal(reason, problem);
    }

    // ---- The webhook: rating happens inside ADO --------------------------------------------------

    [Fact]
    public async Task A_rating_comment_is_stored_against_the_advice_id_with_who_and_when()
    {
        var feedback = new FakeFeedbackStore();
        var pickups = new FakePickupStore();
        using var client = CreateClient(pickups, feedback);

        var response = await PostAsync(client, Commented("<div>ADV-001 accepted</div><div>ADV-002 rejected: wrong layer</div>"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<FeedbackResponse>();
        Assert.Equal(("recorded", (string?)null), (body!.Status, body.Reason));
        Assert.Equal(["ADV-001", "ADV-002"], body.Recorded);
        var stored = Assert.Single(feedback.Submissions);
        Assert.Equal(
            ("visioniq-dev", "payments-core", "4711", "c0mment-0000-4000-8000-000000000001", "a1b2c3"),
            (stored.TenantId, stored.ProjectId, stored.TicketId, stored.DeliveryId, stored.ReviewerId));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 9, 29, 58, 120, TimeSpan.Zero), stored.RatedAt);
        Assert.Equal(
            [new AdviceRating("ADV-001", AdviceDisposition.Accepted, null), new AdviceRating("ADV-002", AdviceDisposition.Rejected, "wrong layer")],
            stored.Ratings);
        Assert.Empty(pickups.Pickups); // a comment never starts a run
    }

    [Fact]
    public async Task VisionIQs_own_comment_and_ordinary_comments_change_nothing()
    {
        var feedback = new FakeFeedbackStore();
        var pickups = new FakePickupStore();
        using var client = CreateClient(pickups, feedback);
        var brief = Features.Briefs.BriefCommentRenderer.Render(
            Target(), Features.Briefs.BriefDocument.Parse(Brief().ToJsonString()));

        var cases = new (JsonObject Body, string Reason)[]
        {
            (Commented("ADV-001 accepted", actor: Bot), "bot_comment"), // even if the bot writes a rating line
            (Commented(brief, actor: "someone@contoso.example"), "no_rating_in_comment"), // the brief itself, pasted
            (Commented("Thanks, will look tomorrow."), "no_rating_in_comment"),
            (Commented("ADV-001 accepted", project: "Unknown"), "project_not_configured"),
            (Commented("ADV-001 accepted", organization: "https://dev.azure.com/evil/"), "organization_not_allowed"),
        };
        foreach (var (body, reason) in cases)
        {
            var response = await PostAsync(client, body);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal(new FeedbackResponse("ignored", reason), await ReadAsync(response));
        }

        Assert.Empty(feedback.Submissions);
        Assert.Empty(pickups.Pickups); // VisionIQ's own comments never trigger a new run
        Assert.Empty(pickups.Runs);
    }

    [Fact]
    public async Task With_an_allowlist_ratings_on_other_work_items_are_ignored()
    {
        var feedback = new FakeFeedbackStore();
        using var client = CreateClient(new FakePickupStore(), feedback, allowed: "9001, 9002");
        using var pilot = CreateClient(new FakePickupStore(), feedback, allowed: "4711");

        var other = await ReadAsync(await PostAsync(client, Commented("ADV-001 accepted")));
        var chosen = await ReadAsync(await PostAsync(pilot, Commented("ADV-001 accepted")));

        Assert.Equal(new FeedbackResponse("ignored", "work_item_not_allowlisted"), other);
        Assert.Equal("recorded", chosen!.Status);
        Assert.Single(feedback.Submissions);
    }

    [Fact]
    public async Task The_stores_answer_is_passed_back_to_ADO()
    {
        var feedback = new FakeFeedbackStore { Outcome = new FeedbackOutcome("ignored", "no_published_brief", [], []) };
        using var client = CreateClient(new FakePickupStore(), feedback);

        var none = await ReadAsync(await PostAsync(client, Commented("ADV-001 accepted")));
        feedback.Outcome = new FeedbackOutcome("ignored", "unknown_advice_ids", [], ["ADV-009"]);
        var unknown = await ReadAsync(await PostAsync(client, Commented("ADV-009 accepted")));
        feedback.Outcome = new FeedbackOutcome("duplicate", null, [], []);
        var again = await ReadAsync(await PostAsync(client, Commented("ADV-001 accepted")));

        Assert.Equal(("ignored", "no_published_brief"), (none!.Status, none.Reason));
        Assert.Equal(("unknown_advice_ids", "ADV-009"), (unknown!.Reason, Assert.Single(unknown.Unknown!)));
        Assert.Equal("duplicate", again!.Status);
    }

    [Fact]
    public async Task An_older_payload_without_an_identity_id_falls_back_to_the_unique_name()
    {
        var feedback = new FakeFeedbackStore();
        using var client = CreateClient(new FakePickupStore(), feedback);

        await PostAsync(client, Commented("ADV-001 accepted", actorId: null));

        Assert.Equal("dev@contoso.example", Assert.Single(feedback.Submissions).ReviewerId);
    }

    [Fact]
    public async Task A_storage_outage_asks_ADO_to_retry_and_a_wrong_secret_is_refused()
    {
        var feedback = new FakeFeedbackStore { Failure = new TimeoutException() };
        using var client = CreateClient(new FakePickupStore(), feedback);

        var outage = await PostAsync(client, Commented("ADV-001 accepted"));
        using var unsigned = new HttpRequestMessage(HttpMethod.Post, "/webhooks/ado")
        {
            Content = new StringContent(Commented("ADV-001 accepted").ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var refused = await client.SendAsync(unsigned);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, outage.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Single(feedback.Submissions); // only the authorized delivery reached the store
    }

    [Theory]
    [InlineData(AdviceDisposition.Accepted, "ACCEPTED")]
    [InlineData(AdviceDisposition.Modified, "MODIFIED")]
    [InlineData(AdviceDisposition.Rejected, "REJECTED")]
    [InlineData(AdviceDisposition.NotApplicable, "NOT_APPLICABLE")]
    public void Dispositions_are_stored_in_the_design_vocabulary(AdviceDisposition disposition, string stored)
    {
        Assert.Equal(stored, PostgresAdviceFeedbackStore.Name(disposition));
    }

    public void Dispose() => File.Delete(policyFile);

    private static string WritePolicy()
    {
        var path = Path.Combine(Path.GetTempPath(), "visioniq-feedback-policy-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, PolicyJson);
        return path;
    }

    private static async Task<FeedbackResponse?> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<FeedbackResponse>();

    private HttpClient CreateClient(ITicketPickupStore pickups, IAdviceFeedbackStore feedback, string? allowed = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("DB_API_USER", "visioniq_api");
            builder.UseSetting("DB_API_PASSWORD", "test-only-api-password");
            builder.UseSetting("PICKUP_POLICY_FILE", policyFile);
            builder.UseSetting("PICKUP_WEBHOOK_SECRET", Secret);
            builder.UseSetting("PICKUP_ALLOWED_WORK_ITEM_IDS", allowed);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(pickups);
                services.RemoveAll<IAdviceFeedbackStore>();
                services.AddSingleton(feedback);
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

    private sealed class FakeFeedbackStore : IAdviceFeedbackStore
    {
        public List<FeedbackSubmission> Submissions { get; } = [];

        public FeedbackOutcome? Outcome { get; set; }

        public Exception? Failure { get; init; }

        public Task<FeedbackOutcome> RecordAsync(FeedbackSubmission submission, CancellationToken cancellationToken)
        {
            Submissions.Add(submission);
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(
                Outcome ?? new FeedbackOutcome("recorded", null, [.. submission.Ratings.Select(r => r.AdviceId)], []));
        }
    }
}
