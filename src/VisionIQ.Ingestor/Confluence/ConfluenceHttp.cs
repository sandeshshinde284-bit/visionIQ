using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VisionIQ.Ingestor.Confluence;

/// <summary>A failure reading Confluence. The message says what to check; it never contains the token.</summary>
public sealed class ConfluenceSourceException : Exception
{
    public ConfluenceSourceException()
    {
    }

    public ConfluenceSourceException(string message)
        : base(message)
    {
    }

    public ConfluenceSourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ConfluenceSourceException(HttpStatusCode? statusCode, string message, Exception? innerException = null)
        : base(message, innerException) => StatusCode = statusCode;

    public HttpStatusCode? StatusCode { get; }

    public bool IsAccessDenied => StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    public bool IsNotFound => StatusCode == HttpStatusCode.NotFound;
}

/// <summary>Read-only Confluence REST client: authentication per deployment, retries with Retry-After, clear errors.</summary>
public sealed class ConfluenceHttp
{
    private readonly HttpClient http;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly AuthenticationHeaderValue authorization;
    private readonly Microsoft.Extensions.Logging.ILogger log;

    public ConfluenceHttp(HttpClient http, ConfluenceOptions options, Func<TimeSpan, CancellationToken, Task>? delay = null, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        this.http = http;
        Options = options;
        this.delay = delay ?? Task.Delay;
        authorization = options.Deployment == ConfluenceDeployment.Cloud
            ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(options.UserEmail + ":" + options.ApiToken)))
            : new AuthenticationHeaderValue("Bearer", options.ApiToken);
    }

    public ConfluenceOptions Options { get; }

    /// <summary>{api root}/{path}?{query}, every part escaped.</summary>
    public Uri Api(string path, IEnumerable<KeyValuePair<string, string>>? query = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var text = Options.ApiRoot + "/" + path.TrimStart('/');
        var parameters = query?.Select(item => Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(item.Value)).ToList() ?? [];
        return new Uri(parameters.Count == 0 ? text : text + "?" + string.Join('&', parameters));
    }

    /// <summary>
    /// Resolves a "next" link: site-relative paths are joined to the site root. Every request carries the
    /// Confluence credential, so the resolved link must stay on the configured site (same scheme, host and
    /// port); anything else, including a scheme-relative "//other-host/..." link, is refused (review R2-06).
    /// </summary>
    public Uri Link(string link)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(link);
        var resolved = Uri.TryCreate(link, UriKind.Absolute, out var absolute) && (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp)
            ? absolute
            : new Uri(Options.SiteRoot, link);
        var site = Options.SiteRoot;
        if (!string.Equals(resolved.Scheme, site.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(resolved.Host, site.Host, StringComparison.OrdinalIgnoreCase) ||
            resolved.Port != site.Port)
        {
            throw new InvalidOperationException(
                "Confluence returned a paging link outside the configured site; refusing to send credentials to it.");
        }

        return resolved;
    }

    public Task<JsonDocument> GetAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken);
    }

    /// <summary>POSTs a JSON body with the same authentication, retries and timeout handling as GET.</summary>
    public Task<JsonDocument> PostJsonAsync(Uri uri, object body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(body);
        return SendAsync(() => new HttpRequestMessage(HttpMethod.Post, uri) { Content = System.Net.Http.Json.JsonContent.Create(body) }, cancellationToken);
    }

    private async Task<JsonDocument> SendAsync(Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = createRequest();
            request.Headers.Authorization = authorization;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var method = request.Method.Method;
            var path = request.RequestUri?.AbsolutePath ?? "?"; // never the query string (cursors)
            var started = System.Diagnostics.Stopwatch.GetTimestamp();

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < Options.MaxRetries)
            {
                // HttpClient.Timeout surfaces as TaskCanceledException, not HttpRequestException (B-2/B-3).
                // Only the caller's own token means "stop"; a timeout is a transient source failure.
                var wait = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));
                Logging.IngestionLog.Retry(log, "Confluence", method, path, attempt + 1, "timeout", wait.TotalMilliseconds);
                await delay(wait, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                Logging.IngestionLog.CallFailed(log, "Confluence", method, path, attempt + 1, "timeout");
                throw new ConfluenceSourceException(null, "Confluence did not respond in time (request timed out after retries).", error);
            }
            catch (HttpRequestException) when (attempt < Options.MaxRetries)
            {
                var wait = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));
                Logging.IngestionLog.Retry(log, "Confluence", method, path, attempt + 1, "network error", wait.TotalMilliseconds);
                await delay(wait, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException error)
            {
                Logging.IngestionLog.CallFailed(log, "Confluence", method, path, attempt + 1, "network error");
                throw new ConfluenceSourceException(null, "Network error while calling Confluence: " + error.Message, error);
            }

            using (response)
            {
                if (log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                {
                    var status = (int)response.StatusCode;
                    var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    var attemptNumber = attempt + 1;
                    Logging.IngestionLog.Call(log, "Confluence", method, path, status, elapsed, attemptNumber);
                }
                if (response.IsSuccessStatusCode)
                {
                    var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using (stream.ConfigureAwait(false))
                    {
                        try
                        {
                            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                        }
                        catch (JsonException error)
                        {
                            throw new ConfluenceSourceException(response.StatusCode, "Confluence returned a response that is not valid JSON (often a sign-in page: check the token).", error);
                        }
                    }
                }

                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
                    && attempt < Options.MaxRetries)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    wait = wait > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait;
                    Logging.IngestionLog.Retry(log, "Confluence", method, path, attempt + 1, "HTTP " + (int)response.StatusCode, wait.TotalMilliseconds);
                    await delay(wait, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Everything else (401/403/404, or a 429/5xx that has exhausted its retries) is terminal:
                // an auth or not-found error will not fix itself by retrying, so fail now with a message
                // that points at the likely cause instead of looping.
                Logging.IngestionLog.CallFailed(log, "Confluence", method, path, attempt + 1, "HTTP " + (int)response.StatusCode);
                throw new ConfluenceSourceException(response.StatusCode, Describe(response.StatusCode));
            }
        }
    }

    private static string Describe(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "Access denied (401): check CONFLUENCE_API_TOKEN (and CONFLUENCE_USER_EMAIL on Cloud).",
        HttpStatusCode.Forbidden => "Access denied (403): the account cannot read this Confluence content.",
        HttpStatusCode.NotFound => "Not found (404): check CONFLUENCE_BASE_URL and the space key.",
        HttpStatusCode.TooManyRequests => "Confluence kept throttling the requests (429); try again later.",
        _ => $"Confluence returned HTTP {(int)status} ({status}).",
    };
}
