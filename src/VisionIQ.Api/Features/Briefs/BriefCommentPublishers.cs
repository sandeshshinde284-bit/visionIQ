using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace VisionIQ.Api.Features.Briefs;

/// <summary>Delivers one rendered brief comment to the ticket and returns the comment's id.</summary>
public interface IBriefCommentPublisher
{
    BriefPublishMode Mode { get; }

    /// <param name="project">The ADO project the work item belongs to.</param>
    /// <param name="workItemId">The work item to comment on.</param>
    /// <param name="html">The comment body.</param>
    /// <param name="marker">The run's marker, already inside <paramref name="html"/>.</param>
    /// <param name="mayAlreadyExist">True on a retry: an earlier attempt may have posted before failing.</param>
    Task<string> PublishAsync(
        string project, int workItemId, string html, string marker, bool mayAlreadyExist, CancellationToken cancellationToken);
}

/// <summary>The comment could not be posted. <see cref="Permanent"/> failures are not retried.</summary>
public sealed class BriefPublishException : Exception
{
    public BriefPublishException()
        : this("publish_failed", false)
    {
    }

    public BriefPublishException(string message)
        : this(message, false)
    {
    }

    public BriefPublishException(string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = "publish_failed";
    }

    public BriefPublishException(string reason, bool permanent)
        : base(reason)
    {
        Reason = reason;
        Permanent = permanent;
    }

    /// <summary>A fixed reason code, safe to log and store.</summary>
    public string Reason { get; }

    public bool Permanent { get; }
}

/// <summary>
/// <c>BRIEF_PUBLISH_MODE=record</c>: nothing leaves the service. The worker stores the rendered comment in
/// <c>workflow.brief_publications</c>, which is how the flow is tested without writing to a real ticket.
/// </summary>
public sealed class RecordingCommentPublisher : IBriefCommentPublisher
{
    public BriefPublishMode Mode => BriefPublishMode.Record;

    public Task<string> PublishAsync(
        string project, int workItemId, string html, string marker, bool mayAlreadyExist, CancellationToken cancellationToken) =>
        Task.FromResult("recorded");
}

/// <summary>
/// S04-03: posts the brief as a work-item comment through the Azure DevOps REST API, with the bot's own token.
/// This class is the only code in VisionIQ that writes to a source system, and all it can do is add a comment:
/// it never edits a title, field, state, assignee or an existing comment. On a retry it first looks for the
/// run's marker among the item's comments, so a failure after a successful post never produces a second comment.
/// </summary>
public sealed class AdoCommentPublisher : IBriefCommentPublisher
{
    private const string ApiVersion = "7.1-preview.4";
    private const int CommentsToCheck = 200;
    private readonly HttpClient http;
    private readonly string root;
    private readonly AuthenticationHeaderValue authorization;

    public AdoCommentPublisher(HttpClient http, Uri organizationUrl, string commentToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(organizationUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(commentToken);
        this.http = http;
        root = organizationUrl.AbsoluteUri.TrimEnd('/');
        authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + commentToken)));
    }

    public BriefPublishMode Mode => BriefPublishMode.Ado;

    public async Task<string> PublishAsync(
        string project, int workItemId, string html, string marker, bool mayAlreadyExist, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(html);
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
        var comments = root + "/" + Uri.EscapeDataString(project) + "/_apis/wit/workItems/" +
            workItemId.ToString(CultureInfo.InvariantCulture) + "/comments";

        if (mayAlreadyExist)
        {
            var listUri = new Uri(
                comments + "?$top=" + CommentsToCheck.ToString(CultureInfo.InvariantCulture) + "&order=desc&api-version=" + ApiVersion);
            using var existing = await SendAsync(HttpMethod.Get, listUri, null, cancellationToken);
            if (existing.RootElement.TryGetProperty("comments", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var comment in list.EnumerateArray())
                {
                    if (comment.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String &&
                        text.GetString()!.Contains(marker, StringComparison.Ordinal) && CommentId(comment) is { } found)
                    {
                        return found;
                    }
                }
            }
        }

        using var created = await SendAsync(
            HttpMethod.Post, new Uri(comments + "?api-version=" + ApiVersion), new { text = html }, cancellationToken);
        return CommentId(created.RootElement) ?? throw new BriefPublishException("comment_id_missing", permanent: false);
    }

    private static string? CommentId(JsonElement comment) =>
        comment.ValueKind == JsonValueKind.Object && comment.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
            ? id.GetRawText()
            : null;

    private async Task<JsonDocument> SendAsync(HttpMethod method, Uri uri, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = authorization;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException ||
            (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new BriefPublishException("ado_unreachable", permanent: false);
        }

        using (response)
        {
            // 203 is how ADO answers an expired or wrong token (a sign-in page instead of JSON).
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NonAuthoritativeInformation)
            {
                throw new BriefPublishException("ado_comment_not_permitted", permanent: true);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new BriefPublishException("ado_work_item_not_found", permanent: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new BriefPublishException("ado_http_" + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), permanent: false);
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            }
            catch (JsonException)
            {
                throw new BriefPublishException("ado_response_not_json", permanent: false);
            }
            catch (Exception error) when (error is IOException or HttpRequestException)
            {
                // The connection dropped while the answer was being read.
                throw new BriefPublishException("ado_unreachable", permanent: false);
            }
        }
    }
}
