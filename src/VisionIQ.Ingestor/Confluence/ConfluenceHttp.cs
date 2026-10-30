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

    public ConfluenceHttp(HttpClient http, ConfluenceOptions options, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
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

    /// <summary>Resolves a "next" link: absolute links are kept, site-relative paths are joined to the site root.</summary>
    public Uri Link(string link)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(link);
        return Uri.TryCreate(link, UriKind.Absolute, out var absolute) && (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp)
            ? absolute
            : new Uri(Options.SiteRoot, link);
    }

    public async Task<JsonDocument> GetAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = authorization;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < Options.MaxRetries)
            {
                await delay(TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt))), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException error)
            {
                throw new ConfluenceSourceException(null, "Network error while calling Confluence: " + error.Message, error);
            }

            using (response)
            {
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
                    await delay(wait > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait, cancellationToken).ConfigureAwait(false);
                    continue;
                }

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
