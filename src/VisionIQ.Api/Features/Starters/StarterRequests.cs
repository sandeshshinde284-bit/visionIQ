using System.Text.RegularExpressions;
using Npgsql;
using VisionIQ.Api.Features.Briefs;
using VisionIQ.Api.Features.Events;
using VisionIQ.Api.Features.Feedback;
using VisionIQ.Api.Features.Pickup;

namespace VisionIQ.Api.Features.Starters;

/// <summary>
/// ENH-02: how an engineer asks for a starter pull request. Like a rating (BRF-06) it is a reply on the ticket,
/// so nothing outside Azure DevOps is needed: one line that starts with <c>visioniq starter-pr</c>.
/// </summary>
public static partial class StarterCommand
{
    /// <summary>The text shown to the engineer; the parser accepts a few spellings of it.</summary>
    public const string Text = "visioniq starter-pr";

    /// <summary>Whether a comment (HTML or plain text) asks for a starter pull request.</summary>
    public static bool IsRequested(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment) ||
            comment.Contains(BriefCommentRenderer.MarkerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // VisionIQ's own guidance comment quotes the command as an example; it must never ask for itself.
            return false;
        }

        return AdviceFeedbackParser.PlainLines(comment).Any(line => CommandLine().IsMatch(line));
    }

    // "visioniq starter-pr", "/visioniq starter pr please", "@VisionIQ: starter pull request". The command is
    // the whole line: a sentence that mentions it, a quoted line ("> visioniq starter-pr") and a line that
    // goes on ("visioniq starter-pr is not needed") ask for nothing.
    [GeneratedRegex(
        @"^[\s\-\*•]*[/@!]?visioniq\b[\s:,\-–—]*starter[\s_\-]?(pr|pull[\s_\-]?request)\b[\s,]*(please)?[\s.!]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex CommandLine();
}

/// <summary>What the webhook did with a starter request.</summary>
/// <param name="Status">queued, duplicate or ignored.</param>
public sealed record StarterResponse(string Status, string? Reason = null, Guid? RunId = null);

/// <summary>Finds the guidance a request refers to and starts the starter run, once.</summary>
public interface IStarterRequestStore
{
    Task<StarterResponse> RequestAsync(
        string tenantId, string projectId, string ticketId, string traceId, CancellationToken cancellationToken);
}

/// <summary>
/// A request applies to the ticket's newest enhancement guidance run (the newest ticket revision that was picked
/// up). It is honoured only when that guidance was PUBLISHED: a starter is for a change that is well understood,
/// and partial or blocked guidance says it is not. While that guidance is still being written the request is
/// ignored, never answered from an older revision's guidance. Asking again for the same guidance is a duplicate,
/// never a second run.
/// </summary>
public sealed class PostgresStarterRequestStore(NpgsqlDataSource dataSource) : IStarterRequestStore
{
    public const string NoGuidance = "no_enhancement_guidance";
    public const string GuidanceNotPublished = "guidance_not_published";
    public const string GuidanceNotReady = "guidance_not_ready";

    public async Task<StarterResponse> RequestAsync(
        string tenantId, string projectId, string ticketId, string traceId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid runId;
        string? state;
        await using (var guidance = new NpgsqlCommand(
            "SELECT r.run_id, res.result_state, r.ticket_kind " +
            "FROM workflow.analysis_runs r " +
            "LEFT JOIN workflow.analysis_results res ON res.run_id = r.run_id " +
            "WHERE r.tenant_id = $1 AND r.project_id = $2 AND r.ticket_id = $3 " +
            "  AND r.analysis_mode = 'brief' AND r.policy_revision IS NOT NULL " +
            "ORDER BY r.ticket_revision DESC, r.requested_at DESC LIMIT 1",
            connection,
            transaction))
        {
            guidance.Parameters.Add(new NpgsqlParameter { Value = tenantId });
            guidance.Parameters.Add(new NpgsqlParameter { Value = projectId });
            guidance.Parameters.Add(new NpgsqlParameter { Value = ticketId });
            await using var reader = await guidance.ExecuteReaderAsync(cancellationToken);
            // The newest pickup of the ticket decides: a ticket that is a defect now has no enhancement guidance,
            // whatever an older revision of it was.
            if (!await reader.ReadAsync(cancellationToken) ||
                await reader.IsDBNullAsync(2, cancellationToken) || reader.GetString(2) != AnalysisRequestedV1.Enhancement)
            {
                return new StarterResponse("ignored", NoGuidance);
            }

            runId = reader.GetGuid(0);
            state = await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1);
        }

        if (state != "PUBLISHED")
        {
            return new StarterResponse("ignored", state is null ? GuidanceNotReady : GuidanceNotPublished);
        }

        var started = await StarterRunStarter.StartAsync(connection, transaction, runId, traceId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return started.RunId is { } run
            ? new StarterResponse("queued", RunId: run)
            : new StarterResponse(started.Reason == StarterRunStarter.AlreadyRequested ? "duplicate" : "ignored", started.Reason);
    }
}

/// <summary>
/// ENH-02: handles a "work item commented" delivery that asks for a starter pull request. The same rules as a
/// rating comment decide whether the comment counts at all: configured organization and project, on the pilot
/// list, not written by a VisionIQ bot identity. A request that cannot be honoured is acknowledged and ignored
/// with a reason: not getting a starter is never an error.
/// </summary>
public static class StarterRequestEndpoint
{
    public static async Task<IResult> HandleAsync(
        CommentDelivery comment, PickupPolicy policy, IServiceProvider services, ILogger logger, string traceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comment);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(services);
        IResult Ignored(string reason)
        {
            StarterLog.Request(logger, comment.Project, comment.WorkItemId, "ignored", reason);
            return Results.Accepted(value: new StarterResponse("ignored", reason));
        }

        if (!policy.IsOrganizationAllowed(comment.OrganizationUrl))
        {
            return Ignored("organization_not_allowed");
        }

        if (!policy.IsWorkItemAllowed(comment.WorkItemId))
        {
            return Ignored("work_item_not_allowlisted");
        }

        if (policy.For(comment.Project) is not { } project)
        {
            return Ignored("project_not_configured");
        }

        if (policy.IsBot(comment.AuthorId, comment.AuthorName))
        {
            return Ignored("bot_comment");
        }

        var options = services.GetService<StarterPullRequestOptions>();
        var store = services.GetService<IStarterRequestStore>();
        var eventing = services.GetService<EventingOptions>();
        if (options is not { Enabled: true } || store is null || eventing is null)
        {
            return Ignored("starter_pr_not_enabled");
        }

        StarterResponse outcome;
        try
        {
            outcome = await store.RequestAsync(
                eventing.TenantId,
                project.ProjectId,
                comment.WorkItemId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                traceId,
                cancellationToken);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // ADO retries the delivery; asking twice never starts a second run.
            StarterLog.RequestFailed(logger, project.ProjectId, comment.WorkItemId, error.GetType().Name);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        StarterLog.Request(logger, project.ProjectId, comment.WorkItemId, outcome.Status, outcome.Reason ?? "requested_by_engineer");
        return Results.Accepted(value: outcome);
    }
}

internal static partial class StarterLog
{
    [LoggerMessage(EventId = 662, Level = LogLevel.Information, Message = "Starter pull requests on (mode {Mode}); delivered by FIX_PR_MODE {Delivery}")]
    public static partial void Enabled(ILogger logger, StarterPullRequestMode mode, Fixes.FixPullRequestMode delivery);

    [LoggerMessage(EventId = 666, Level = LogLevel.Warning, Message = "Starter pull requests are on (mode {Mode}) but FIX_PR_MODE is off: starters are prepared and never opened")]
    public static partial void NotDelivered(ILogger logger, StarterPullRequestMode mode);

    [LoggerMessage(EventId = 667, Level = LogLevel.Error, Message = "Starter run after the guidance of run {GuidanceRunId} could not be requested ({ErrorType}); the guidance is kept")]
    public static partial void PolicyStartFailed(ILogger logger, Guid guidanceRunId, string errorType);

    [LoggerMessage(EventId = 663, Level = LogLevel.Information, Message = "Starter request on {ProjectId} #{WorkItemId}: {Status} ({Reason})")]
    public static partial void Request(ILogger logger, string projectId, int workItemId, string status, string reason);

    [LoggerMessage(EventId = 664, Level = LogLevel.Error, Message = "Starter request on {ProjectId} #{WorkItemId} could not be stored ({ErrorType}); ADO will retry")]
    public static partial void RequestFailed(ILogger logger, string projectId, int workItemId, string errorType);

    [LoggerMessage(EventId = 665, Level = LogLevel.Information, Message = "Starter run {StarterRunId} requested by policy after the guidance of run {GuidanceRunId}")]
    public static partial void RequestedByPolicy(ILogger logger, Guid starterRunId, Guid guidanceRunId);
}

/// <summary>ENH-02 composition root. The options are always registered (the event inbox and the webhook read them).</summary>
public static class StarterServiceRegistration
{
    public static StarterPullRequestOptions AddStarterPullRequests(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = StarterPullRequestOptions.FromConfiguration(configuration);
        services.AddSingleton(options);
        if (options.Enabled)
        {
            services.AddSingleton<IStarterRequestStore>(provider =>
                new PostgresStarterRequestStore(provider.GetRequiredService<NpgsqlDataSource>()));
        }

        return options;
    }

    /// <summary>Says at start-up how starters are asked for and whether they can be delivered.</summary>
    public static void LogEnabled(ILogger logger, StarterPullRequestOptions options, Fixes.FixPullRequestOptions delivery)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(delivery);
        if (delivery.Mode == Fixes.FixPullRequestMode.Off)
        {
            StarterLog.NotDelivered(logger, options.Mode);
            return;
        }

        StarterLog.Enabled(logger, options.Mode, delivery.Mode);
    }
}
