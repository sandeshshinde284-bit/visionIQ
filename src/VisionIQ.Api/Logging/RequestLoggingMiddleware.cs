using System.Diagnostics;
using System.Text.RegularExpressions;

namespace VisionIQ.Api.Logging;

/// <summary>
/// One log line per request with a correlation ID. Accepts a well-formed incoming X-Request-Id (so callers such as
/// Impact Agent can correlate), otherwise generates one; returns it on the response. Never logs headers, query
/// strings or bodies (they can carry tokens or evidence).
/// </summary>
public sealed partial class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public const string Header = "X-Request-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var incoming = context.Request.Headers[Header].ToString();
        var requestId = SafeId().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");
        context.TraceIdentifier = requestId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[Header] = requestId;
            return Task.CompletedTask;
        });

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["requestId"] = requestId });
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? context.Request.Path.Value ?? "/";
            Log.Completed(logger, context.Request.Method, route, context.Response.StatusCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex SafeId();

    private static partial class Log
    {
        [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "HTTP {Method} {Route} responded {StatusCode} in {ElapsedMs:0.0} ms")]
        public static partial void Completed(ILogger logger, string method, string route, int statusCode, double elapsedMs);
    }
}
