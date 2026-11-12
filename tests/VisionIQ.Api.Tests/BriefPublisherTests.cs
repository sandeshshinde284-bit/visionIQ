using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VisionIQ.Api.Features.Briefs;
using static VisionIQ.Api.Tests.BriefTestData;

namespace VisionIQ.Api.Tests;

/// <summary>
/// S04-03 / S10-08: one comment per run, error-safe retries, and no write to Azure DevOps unless explicitly
/// configured. Everything here uses fakes; no test ever contacts a real ADO organization.
/// </summary>
public sealed class BriefPublisherTests
{
    private static readonly DateTimeOffset PickedUp = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly int[] Attempts = [1, 2, 3, 7, 20];

    // ---- The publisher: claim, render, post, mark -------------------------------------------------

    [Fact]
    public async Task A_stored_brief_is_posted_once_on_its_ticket_with_the_run_marker()
    {
        var store = new FakeStore(Pending());
        var comments = new FakeComments(BriefPublishMode.Ado);
        var publisher = new BriefPublisher(store, comments, PickupTestData.Policy, NullLogger<BriefPublisher>.Instance);

        Assert.Equal(1, await publisher.ProcessPendingAsync());
        Assert.Equal(0, await publisher.ProcessPendingAsync()); // nothing left: no second comment

        var post = Assert.Single(comments.Posts);
        // The run stores the policy's project id; ADO is addressed by the project name.
        Assert.Equal(("Payments Core", 4711, false), (post.Project, post.WorkItemId, post.MayAlreadyExist));
        Assert.Equal("visioniq-brief run=7c9e6679-7425-40de-944b-e07fc1f90ae7", post.Marker);
        Assert.Contains(post.Marker, post.Html, StringComparison.Ordinal);
        Assert.Contains("<b>ADV-001</b>", post.Html, StringComparison.Ordinal);
        var published = Assert.Single(store.Published);
        Assert.Equal((RunId, BriefPublishMode.Ado, "comment-1"), (published.RunId, published.Mode, published.CommentId));
        Assert.Matches("^[0-9a-f]{64}$", published.Sha256);
        Assert.Null(published.Body); // in ado mode the text lives on the ticket, not in our database
    }

    [Fact]
    public async Task Record_mode_keeps_the_comment_locally_and_calls_nothing_external()
    {
        var store = new FakeStore(Pending());
        var publisher = new BriefPublisher(store, new RecordingCommentPublisher(), null, NullLogger<BriefPublisher>.Instance);

        await publisher.ProcessPendingAsync();

        var published = Assert.Single(store.Published);
        Assert.Equal((BriefPublishMode.Record, "recorded"), (published.Mode, published.CommentId));
        Assert.Contains("VisionIQ brief", published.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_retry_tells_the_publisher_an_earlier_attempt_may_have_posted()
    {
        var store = new FakeStore(Pending(attempt: 3));
        var comments = new FakeComments(BriefPublishMode.Ado);

        await new BriefPublisher(store, comments, null, NullLogger<BriefPublisher>.Instance).ProcessPendingAsync();

        Assert.True(Assert.Single(comments.Posts).MayAlreadyExist);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_later_with_backoff()
    {
        var store = new FakeStore(Pending(attempt: 2));
        var comments = new FakeComments(BriefPublishMode.Ado) { Failure = new BriefPublishException("ado_http_503", permanent: false) };

        await new BriefPublisher(store, comments, null, NullLogger<BriefPublisher>.Instance).ProcessPendingAsync();

        Assert.Empty(store.Published);
        Assert.Empty(store.Failed);
        Assert.Equal((RunId, TimeSpan.FromSeconds(30)), Assert.Single(store.Deferred));
        Assert.Equal([15, 30, 60, 900, 900], Attempts.Select(BriefPublisher.RetryDelaySeconds));
    }

    [Theory]
    [InlineData(true, 1, "ado_comment_not_permitted", "ado_comment_not_permitted")]
    [InlineData(false, BriefPublisher.MaxAttempts, "ado_http_500", "publish_attempts_exhausted")]
    public async Task A_permanent_failure_or_too_many_attempts_ends_as_failed_with_a_reason(
        bool permanent, int attempt, string error, string stored)
    {
        var store = new FakeStore(Pending(attempt: attempt));
        var comments = new FakeComments(BriefPublishMode.Ado) { Failure = new BriefPublishException(error, permanent) };

        await new BriefPublisher(store, comments, null, NullLogger<BriefPublisher>.Instance).ProcessPendingAsync();

        Assert.Equal((RunId, stored), Assert.Single(store.Failed));
        Assert.Empty(store.Deferred);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(BriefPublisher.MaxAttempts, true)]
    public async Task An_unexpected_error_is_retried_and_capped_and_does_not_stop_the_batch(int attempt, bool gaveUp)
    {
        var second = new PendingPublication(Target() with { RunId = Guid.NewGuid() }, attempt, PickedUp, Brief().ToJsonString());
        var store = new FakeStore(Pending(attempt: attempt), second);
        var comments = new FakeComments(BriefPublishMode.Ado) { Failure = new IOException("connection reset, token=secret") };

        Assert.Equal(2, await new BriefPublisher(store, comments, null, NullLogger<BriefPublisher>.Instance).ProcessPendingAsync());

        Assert.Empty(store.Published);
        Assert.Equal(gaveUp ? 0 : 2, store.Deferred.Count); // both items were handled, not just the first
        Assert.Equal(gaveUp ? 2 : 0, store.Failed.Count);
        Assert.All(store.Failed, failed => Assert.Equal("publish_attempts_exhausted", failed.Reason));
    }

    [Fact]
    public async Task A_brief_that_cannot_be_read_is_never_half_posted()
    {
        var unreadable = new FakeStore(Pending(briefJson: """{"schema_version": 99}"""));
        var notAWorkItem = new FakeStore(Pending(ticketId: "PAY-12"));
        var comments = new FakeComments(BriefPublishMode.Ado);

        await new BriefPublisher(unreadable, comments, null, NullLogger<BriefPublisher>.Instance).ProcessPendingAsync();
        await new BriefPublisher(notAWorkItem, comments, null, NullLogger<BriefPublisher>.Instance).ProcessPendingAsync();

        Assert.Empty(comments.Posts);
        Assert.Equal("brief_unreadable", Assert.Single(unreadable.Failed).Reason);
        Assert.Equal("ticket_id_not_a_work_item", Assert.Single(notAWorkItem.Failed).Reason);
    }

    [Fact]
    public async Task A_blocked_run_without_a_brief_still_gets_one_comment()
    {
        var store = new FakeStore(new PendingPublication(Target("BLOCKED", "ticket_not_readable"), 1, PickedUp, null));
        var comments = new FakeComments(BriefPublishMode.Ado);

        await new BriefPublisher(store, comments, null, NullLogger<BriefPublisher>.Instance).ProcessPendingAsync();

        Assert.Contains("&middot; Blocked", Assert.Single(comments.Posts).Html, StringComparison.Ordinal);
        Assert.Single(store.Published);
    }

    // ---- The only code that writes to ADO: it can add a comment, nothing else --------------------

    [Fact]
    public async Task The_ado_publisher_posts_one_comment_with_the_bot_token()
    {
        var handler = new FakeHandler(Reply("""{"id": 51, "text": "x"}"""));
        var publisher = Ado(handler);

        var id = await publisher.PublishAsync("Payments Core", 4711, "<p>brief</p>", "visioniq-brief run=r1", false, default);

        Assert.Equal("51", id);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "https://dev.azure.com/contoso/Payments%20Core/_apis/wit/workItems/4711/comments?api-version=7.1-preview.4",
            request.Uri);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":bot-comment-token")), request.Authorization);
        Assert.Equal("<p>brief</p>", JsonNode.Parse(request.Body!)!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task On_a_retry_an_existing_comment_of_the_run_is_reused_instead_of_posting_again()
    {
        var handler = new FakeHandler(Reply(
            """{"comments": [{"id": 60, "text": "someone else"}, {"id": 58, "text": "<p>visioniq-brief run=r1 result=x</p>"}]}"""));

        var id = await Ado(handler).PublishAsync("Payments", 4711, "<p>brief</p>", "visioniq-brief run=r1", true, default);

        Assert.Equal("58", id);
        var request = Assert.Single(handler.Requests); // a read; no second POST
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Contains("/comments?$top=200&order=desc&api-version=", request.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_a_retry_with_no_existing_comment_it_posts()
    {
        var handler = new FakeHandler(Reply("""{"comments": [{"id": 60, "text": "run=r1 but not ours"}]}"""), Reply("""{"id": 61}"""));

        var id = await Ado(handler).PublishAsync("Payments", 4711, "<p>brief</p>", "visioniq-brief run=r1", true, default);

        Assert.Equal("61", id);
        Assert.Equal([HttpMethod.Get, HttpMethod.Post], handler.Requests.Select(r => r.Method));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "ado_comment_not_permitted", true)]
    [InlineData(HttpStatusCode.Forbidden, "ado_comment_not_permitted", true)]
    [InlineData(HttpStatusCode.NonAuthoritativeInformation, "ado_comment_not_permitted", true)]
    [InlineData(HttpStatusCode.NotFound, "ado_work_item_not_found", true)]
    [InlineData(HttpStatusCode.TooManyRequests, "ado_http_429", false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ado_http_503", false)]
    public async Task Ado_errors_become_fixed_reasons_and_only_permission_problems_are_permanent(
        HttpStatusCode status, string reason, bool permanent)
    {
        var error = await Assert.ThrowsAsync<BriefPublishException>(() =>
            Ado(new FakeHandler(Reply("{}", status))).PublishAsync("Payments", 1, "<p>b</p>", "m", false, default));

        Assert.Equal((reason, permanent), (error.Reason, error.Permanent));
    }

    [Fact]
    public async Task A_network_failure_is_transient_and_carries_no_message_text()
    {
        var error = await Assert.ThrowsAsync<BriefPublishException>(() =>
            Ado(new FakeHandler(new HttpRequestException("token=secret leaked"))).PublishAsync("Payments", 1, "<p>b</p>", "m", false, default));

        Assert.Equal(("ado_unreachable", false), (error.Reason, error.Permanent));
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
    }

    // ---- Configuration: writing is opt-in and uses its own token ---------------------------------

    [Theory]
    [InlineData(null, BriefPublishMode.Off)]
    [InlineData("", BriefPublishMode.Off)]
    [InlineData("OFF", BriefPublishMode.Off)]
    [InlineData("record", BriefPublishMode.Record)]
    public void Publishing_is_off_by_default_and_record_mode_needs_no_credentials(string? value, BriefPublishMode mode)
    {
        var options = BriefPublishOptions.FromConfiguration(Config(("BRIEF_PUBLISH_MODE", value)));

        Assert.Equal(mode, options.Mode);
        Assert.Null(options.CommentToken);
    }

    [Fact]
    public void Ado_mode_needs_its_own_write_token()
    {
        var ok = BriefPublishOptions.FromConfiguration(Config(
            ("BRIEF_PUBLISH_MODE", "ado"), ("ADO_ORG_URL", "https://dev.azure.com/contoso/"),
            ("ADO_COMMENT_PAT", "bot-comment-token"), ("ADO_READ_PAT", "read-only-token")));

        Assert.Equal((BriefPublishMode.Ado, "bot-comment-token"), (ok.Mode, ok.CommentToken));
        Assert.Equal("https://dev.azure.com/contoso/", ok.OrganizationUrl!.AbsoluteUri);

        foreach (var broken in new (string, string?)[][]
        {
            [("BRIEF_PUBLISH_MODE", "ado"), ("ADO_ORG_URL", "https://dev.azure.com/contoso/")],
            [("BRIEF_PUBLISH_MODE", "ado"), ("ADO_COMMENT_PAT", "bot-comment-token")],
            [("BRIEF_PUBLISH_MODE", "ado"), ("ADO_ORG_URL", "http://dev.azure.com/contoso/"), ("ADO_COMMENT_PAT", "t")],
            // The read token must stay read-only: it is refused as the comment token.
            [("BRIEF_PUBLISH_MODE", "ado"), ("ADO_ORG_URL", "https://dev.azure.com/contoso/"), ("ADO_COMMENT_PAT", "same"), ("ADO_READ_PAT", "same")],
            [("BRIEF_PUBLISH_MODE", "everywhere")],
            [("BRIEF_PUBLISH_MODE", "record"), ("BRIEF_PUBLISH_INTERVAL_SECONDS", "0")],
        })
        {
            Assert.Throws<InvalidOperationException>(() => BriefPublishOptions.FromConfiguration(Config(broken)));
        }
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    private static PendingPublication Pending(int attempt = 1, string? briefJson = null, string ticketId = "4711") =>
        new(Target() with { TicketId = ticketId }, attempt, PickedUp, briefJson ?? Brief().ToJsonString());

    private static AdoCommentPublisher Ado(FakeHandler handler) =>
        new(new HttpClient(handler), new Uri("https://dev.azure.com/contoso/"), "bot-comment-token");

    private static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record Post(string Project, int WorkItemId, string Html, string Marker, bool MayAlreadyExist);

    private sealed class FakeComments(BriefPublishMode mode) : IBriefCommentPublisher
    {
        public List<Post> Posts { get; } = [];

        public Exception? Failure { get; init; }

        public BriefPublishMode Mode => mode;

        public Task<string> PublishAsync(
            string project, int workItemId, string html, string marker, bool mayAlreadyExist, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Posts.Add(new Post(project, workItemId, html, marker, mayAlreadyExist));
            return Task.FromResult("comment-" + Posts.Count);
        }
    }

    private sealed class FakeStore(params PendingPublication[] pending) : IBriefPublicationStore
    {
        private readonly Queue<PendingPublication> queue = new(pending);

        public List<(Guid RunId, BriefPublishMode Mode, string CommentId, string Sha256, string? Body)> Published { get; } = [];

        public List<(Guid RunId, TimeSpan Delay)> Deferred { get; } = [];

        public List<(Guid RunId, string Reason)> Failed { get; } = [];

        public Task<IReadOnlyList<PendingPublication>> ClaimAsync(int batchSize, TimeSpan lease, CancellationToken cancellationToken)
        {
            var claimed = new List<PendingPublication>();
            while (claimed.Count < batchSize && queue.TryDequeue(out var next))
            {
                claimed.Add(next);
            }

            return Task.FromResult<IReadOnlyList<PendingPublication>>(claimed);
        }

        public Task<TimeSpan?> MarkPublishedAsync(
            Guid runId, BriefPublishMode mode, string commentId, string commentSha256, string? commentBody,
            CancellationToken cancellationToken)
        {
            Published.Add((runId, mode, commentId, commentSha256, commentBody));
            return Task.FromResult<TimeSpan?>(TimeSpan.FromSeconds(42));
        }

        public Task DeferAsync(Guid runId, TimeSpan delay, CancellationToken cancellationToken)
        {
            Deferred.Add((runId, delay));
            return Task.CompletedTask;
        }

        public Task FailAsync(Guid runId, string reason, CancellationToken cancellationToken)
        {
            Failed.Add((runId, reason));
            return Task.CompletedTask;
        }
    }

    private sealed record Sent(HttpMethod Method, string Uri, string? Authorization, string? Body);

    private sealed class FakeHandler(params object[] replies) : HttpMessageHandler
    {
        private readonly Queue<object> replies = new(replies);

        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Sent(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return replies.Dequeue() switch
            {
                Exception error => throw error,
                var reply => (HttpResponseMessage)reply,
            };
        }
    }
}
