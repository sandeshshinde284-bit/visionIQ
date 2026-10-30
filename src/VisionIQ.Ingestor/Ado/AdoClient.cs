using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace VisionIQ.Ingestor.Ado;

/// <summary>A failure reading Azure DevOps. The message says what to check; it never contains the token.</summary>
public sealed class AdoSourceException : Exception
{
    public AdoSourceException()
    {
    }

    public AdoSourceException(string message)
        : base(message)
    {
    }

    public AdoSourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AdoSourceException(HttpStatusCode? statusCode, string message, Exception? innerException = null)
        : base(message, innerException) => StatusCode = statusCode;

    public HttpStatusCode? StatusCode { get; }

    /// <summary>401, 403, or 203 (Azure DevOps answers an invalid token with a sign-in page).</summary>
    public bool IsAccessDenied => StatusCode is HttpStatusCode.Unauthorized
        or HttpStatusCode.Forbidden
        or HttpStatusCode.NonAuthoritativeInformation;

    public bool IsNotFound => StatusCode == HttpStatusCode.NotFound;
}

/// <summary>One parsed response. Dispose it once its elements have been read.</summary>
public sealed class AdoResponse : IDisposable
{
    public AdoResponse(JsonDocument body, string? continuationToken)
    {
        Body = body;
        ContinuationToken = continuationToken;
    }

    public JsonDocument Body { get; }

    public JsonElement Root => Body.RootElement;

    /// <summary>The x-ms-continuationtoken header, when the API has more pages.</summary>
    public string? ContinuationToken { get; }

    public void Dispose() => Body.Dispose();
}

/// <summary>
/// Read-only Azure DevOps REST client: personal access token authentication, api-version on every call,
/// retries with Retry-After on throttling and transient errors, and clear errors for everything else.
/// </summary>
public sealed class AdoClient
{
    private readonly HttpClient http;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly string authorization;

    public AdoClient(HttpClient http, AdoOptions options, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        this.http = http;
        Options = options;
        this.delay = delay ?? Task.Delay;
        authorization = Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + options.PersonalAccessToken));
    }

    public AdoOptions Options { get; }

    /// <summary>Builds {organization}/{project}/{path}?{query}&amp;api-version=... with every part escaped.</summary>
    public Uri BuildUri(string? project, string path, IEnumerable<KeyValuePair<string, string>>? query = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var root = Options.OrganizationUrl.AbsoluteUri.TrimEnd('/');
        var prefix = project is null ? root : root + "/" + Uri.EscapeDataString(project);
        var parameters = new List<string>();
        if (query is not null)
        {
            foreach (var (key, value) in query)
            {
                parameters.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value));
            }
        }

        parameters.Add("api-version=" + Uri.EscapeDataString(Options.ApiVersion));
        return new Uri(prefix + "/" + path.TrimStart('/') + "?" + string.Join('&', parameters));
    }

    public Task<AdoResponse> GetAsync(Uri uri, CancellationToken cancellationToken = default) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken);

    public Task<AdoResponse> PostJsonAsync(Uri uri, object body, CancellationToken cancellationToken = default) =>
        SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(body) },
            cancellationToken);

    private async Task<AdoResponse> SendAsync(Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = createRequest();
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authorization);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < Options.MaxRetries)
            {
                await delay(Backoff(attempt, null), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException error)
            {
                throw new AdoSourceException(null, "Network error while calling Azure DevOps: " + error.Message, error);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NonAuthoritativeInformation)
                {
                    throw new AdoSourceException(response.StatusCode, Describe(response.StatusCode));
                }

                if (response.IsSuccessStatusCode)
                {
                    return await ReadAsync(response, cancellationToken).ConfigureAwait(false);
                }

                if (IsTransient(response.StatusCode) && attempt < Options.MaxRetries)
                {
                    await delay(Backoff(attempt, response.Headers.RetryAfter), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new AdoSourceException(response.StatusCode, Describe(response.StatusCode));
            }
        }
    }

    private static async Task<AdoResponse> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var token = response.Headers.TryGetValues("x-ms-continuationtoken", out var values) ? values.FirstOrDefault() : null;
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                return new AdoResponse(document, string.IsNullOrWhiteSpace(token) ? null : token);
            }
            catch (JsonException error)
            {
                throw new AdoSourceException(response.StatusCode, "Azure DevOps returned a response that is not valid JSON.", error);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.TooManyRequests
        or HttpStatusCode.InternalServerError
        or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;

    private static TimeSpan Backoff(int attempt, RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
        {
            return Clamp(delta);
        }

        if (retryAfter?.Date is { } date)
        {
            return Clamp(date - DateTimeOffset.UtcNow);
        }

        return Clamp(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
    }

    private static TimeSpan Clamp(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return value > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : value;
    }

    private static string Describe(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NonAuthoritativeInformation =>
            "Azure DevOps returned a sign-in page (203): ADO_READ_PAT is invalid or expired.",
        HttpStatusCode.Unauthorized => "Access denied (401): check that ADO_READ_PAT is valid and not expired.",
        HttpStatusCode.Forbidden => "Access denied (403): the token is valid but lacks read permission for this source.",
        HttpStatusCode.NotFound => "Not found (404): check the organization, project, repository or pipeline name.",
        HttpStatusCode.TooManyRequests => "Azure DevOps kept throttling the requests (429); try again later.",
        _ => $"Azure DevOps returned HTTP {(int)status} ({status}).",
    };
}
