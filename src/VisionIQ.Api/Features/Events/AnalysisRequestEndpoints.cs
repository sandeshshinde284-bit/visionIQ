using System.Diagnostics;
using Npgsql;

namespace VisionIQ.Api.Features.Events;

/// <summary>Body of the development-only trigger; the real trigger is the ADO pickup webhook (S04-02).</summary>
public sealed record AnalysisRequestBody(
    string ProjectId,
    string TicketId,
    int TicketRevision,
    string RepositoryId,
    string BaseCommitSha,
    AnalysisMode AnalysisMode,
    string SecurityScopeId);

public sealed record AnalysisRequestAccepted(Guid RunId, Guid EventId, string TraceId);

/// <summary>
/// Development-only entry point for the FND-05 smoke test: records a run and its analysis.requested event in one
/// transaction (outbox), so the end-to-end loop can be exercised before the webhook receiver exists.
/// </summary>
public static class AnalysisRequestEndpoints
{
    public static IEndpointRouteBuilder MapDevelopmentAnalysisRequests(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/dev/analysis-requests", RequestAsync).WithName("DevelopmentAnalysisRequest");
        return endpoints;
    }

    private static async Task<IResult> RequestAsync(
        AnalysisRequestBody body, NpgsqlDataSource dataSource, EventingOptions options, ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid();
        // Continue the caller's W3C trace when there is one, so a single trace spans HTTP and both services.
        var traceId = Activity.Current?.TraceId.ToHexString() ?? ActivityTraceId.CreateRandom().ToHexString();
        var data = new AnalysisRequestedV1(
            body.ProjectId, body.TicketId, body.TicketRevision, body.RepositoryId, body.BaseCommitSha,
            body.AnalysisMode, body.SecurityScopeId);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        EventEnvelope envelope;
        try
        {
            // A dev run has no pickup policy, so it is never a duplicate.
            envelope = (await AnalysisRuns.CreateAsync(
                connection, transaction, new NewAnalysisRun(runId, options.TenantId, data, traceId), cancellationToken))!;
        }
        catch (EventContractException error)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = [error.Message] });
        }

        await transaction.CommitAsync(cancellationToken);

        var logger = loggers.CreateLogger(typeof(AnalysisRequestEndpoints));
        using (logger.BeginScope(envelope.LogScope()))
        {
            EventLog.AnalysisRequested(logger);
        }

        return Results.Accepted(value: new AnalysisRequestAccepted(runId, envelope.EventId, traceId));
    }
}
