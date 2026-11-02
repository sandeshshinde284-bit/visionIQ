using System.Net;
using System.Text.Json;
using VisionIQ.Ingestor.Ado;

namespace VisionIQ.Ingestor.Tests;

/// <summary>
/// Master design 6.6 stage 2: discussion comments are part of a ticket's history. Read when ADO reports any,
/// oldest first, deleted ones skipped, authors by ID only; unavailable is never shown as "no comments".
/// </summary>
public sealed class WorkItemCommentTests
{
    private static string Batch(string commentCount) => """
        {"value":[{"id":1234,"rev":5,"fields":{"System.WorkItemType":"Bug","System.State":"Active",
          "System.Title":"Barcode print fails","System.ChangedDate":"2026-01-01T00:00:00Z"COUNT},"relations":[]}]}
        """.Replace("COUNT", commentCount, StringComparison.Ordinal);

    private const string Page1 = """
        {"totalCount":3,"count":2,"continuationToken":"next-page","comments":[
          {"id":7,"text":"<p>Seen again after the retry patch.</p>","createdBy":{"id":"u-2","displayName":"Ana Lopez","uniqueName":"ana@contoso.com"},
           "createdDate":"2026-01-02T10:00:00Z","isDeleted":false},
          {"id":3,"text":"spam","createdBy":{"id":"u-9"},"createdDate":"2026-01-01T09:00:00Z","isDeleted":true}]}
        """;

    private const string Page2 = """
        {"totalCount":3,"count":1,"comments":[
          {"id":5,"text":"<p>Repro: pay, then retry printing.</p>","createdBy":{"id":"u-1"},"createdDate":"2026-01-01T11:00:00Z"}]}
        """;

    private static async Task<(WorkItemRecord Item, FakeAdoHandler Handler)> Read(string commentCount, Func<HttpRequestMessage, HttpResponseMessage>? comments = null)
    {
        var handler = new FakeAdoHandler()
            .On(HttpMethod.Post, "/_apis/wit/workitemsbatch", _ => FakeAdoHandler.Ok(Batch(commentCount)));
        if (comments is not null)
        {
            handler.On(HttpMethod.Get, "/_apis/wit/workItems/1234/comments", comments);
        }

        var outcome = Assert.Single(await new AdoWorkItemSource(DemoAdo.Client(handler)).GetWorkItemsAsync(DemoAdo.Project, [1234]));
        return (outcome.Record!, handler);
    }

    [Fact]
    public async Task Comments_are_paged_oldest_first_without_deleted_ones_or_personal_details()
    {
        var (item, handler) = await Read(",\"System.CommentCount\":3", request =>
            FakeAdoHandler.Ok(request.RequestUri!.Query.Contains("continuationToken=next-page", StringComparison.Ordinal) ? Page2 : Page1));

        Assert.Equal(new[] { 5, 7 }, item.Comments!.Select(c => c.Id)); // oldest first, deleted 3 skipped
        Assert.Equal(("u-1", "<p>Repro: pay, then retry printing.</p>"), (item.Comments![0].AuthorId, item.Comments[0].Text));
        Assert.Null(item.CommentsGap);
        Assert.False(item.CommentsTruncated);
        Assert.All(handler.Requests.Where(r => r.Contains("/comments", StringComparison.Ordinal)),
            r => Assert.Contains("api-version=7.1-preview.4", r, StringComparison.Ordinal));
        var stored = JsonSerializer.Serialize(item);
        Assert.DoesNotContain("ana@contoso.com", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("Ana Lopez", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ticket_without_comments_costs_no_extra_call()
    {
        var (item, handler) = await Read(",\"System.CommentCount\":0", _ => throw new InvalidOperationException("must not be called"));

        Assert.Empty(item.Comments!);
        Assert.DoesNotContain(handler.Requests, r => r.Contains("/comments", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unreadable_comments_are_unavailable_not_none_and_the_ticket_is_still_delivered()
    {
        var (item, _) = await Read(",\"System.CommentCount\":2", _ => FakeAdoHandler.Status(HttpStatusCode.Forbidden));

        Assert.Equal(1234, item.Id);
        Assert.Null(item.Comments);
        Assert.Equal("comments: HTTP 403", item.CommentsGap);
    }

    [Fact]
    public async Task A_missing_comment_count_is_reported_not_guessed()
    {
        var (item, handler) = await Read("", _ => throw new InvalidOperationException("must not be called"));

        Assert.Null(item.Comments);
        Assert.Equal("comments: count not reported", item.CommentsGap);
        Assert.DoesNotContain(handler.Requests, r => r.Contains("/comments", StringComparison.Ordinal));
    }
}
