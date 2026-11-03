using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace VisionIQ.Api.Features.GraphApi;

/// <summary>
/// LRN-07 graph query API for the Impact Agent backend. Requires a Microsoft Entra ID delegated
/// (On-Behalf-Of) access token issued for VisionIQ by the configured tenant, carrying the required scope, from
/// an approved calling application, for a real user. Everything else is rejected.
/// </summary>
public static class GraphApiEndpoints
{
    public const string Policy = "VisionIQ.GraphReader";
    public const string Route = "/api/v1/graph/work-items/{project}/{workItemId}";

    /// <summary>Registers authentication and the query service; false (nothing exposed) when not configured.</summary>
    public static bool AddGraphApi(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = GraphApiOptions.FromConfiguration(configuration);
        if (options is null)
        {
            return false;
        }

        var authorizer = new GraphCallerAuthorizer(options);
        services.AddSingleton(options);
        services.AddSingleton(authorizer);
        services.AddSingleton<IGraphQueryService, GraphQueryService>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
        {
            var tenant = options.TenantId.ToString("D");
            jwt.Authority = $"https://login.microsoftonline.com/{tenant}/v2.0";
            jwt.MapInboundClaims = false; // keep raw claim names (tid, scp, azp, oid, preferred_username)
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers = [$"https://login.microsoftonline.com/{tenant}/v2.0", $"https://sts.windows.net/{tenant}/"],
                ValidateAudience = true,
                ValidAudiences = options.Audiences,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromMinutes(2),
            };
        });
        services.AddAuthorization(authorization => authorization.AddPolicy(Policy, policy => policy
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
            {
                var decision = authorizer.Authorize(context.User);
                if (!decision.Allowed && context.Resource is HttpContext http)
                {
                    // Why a caller was refused: reason code and calling app ID only (never the token).
                    var caller = context.User.FindFirst("azp")?.Value ?? context.User.FindFirst("appid")?.Value ?? "unknown";
                    VisionIQ.Api.Logging.AccessLog.CallerDenied(
                        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("VisionIQ.Api.GraphApi"), decision.Reason, caller);
                }

                return decision.Allowed;
            })));
        return true;
    }

    public static void MapGraphApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet(Route, async (string project, string workItemId, int? hops, HttpContext http,
            GraphCallerAuthorizer authorizer, IGraphQueryService query, VisionIQ.Api.Logging.IdentityPseudonymizer pseudonyms,
            ILoggerFactory logs, CancellationToken cancellationToken) =>
        {
            var log = logs.CreateLogger("VisionIQ.Api.GraphApi");
            http.Response.Headers.CacheControl = "no-store"; // authorized engineering evidence
            if (authorizer.Authorize(http.User) is not { Allowed: true, Requester: { } requester })
            {
                return Results.Forbid();
            }

            if (hops is < 1 or > 3)
            {
                return Results.BadRequest(new { error = "hops must be 1-3" });
            }

            var user = pseudonyms.For(requester.PrincipalId);
            using var scope = log.BeginScope(new Dictionary<string, object> { ["user"] = user });
            var result = await query.QueryWorkItemAsync(project, workItemId, requester, hops ?? 2, cancellationToken);
            // Audit: who (pseudonymous) asked for what, and what came back (counts only, never content).
            VisionIQ.Api.Logging.AccessLog.GraphQuery(log, project, workItemId, hops ?? 2, user,
                result is null ? "not found or not allowed" : "answered", result?.Nodes.Count ?? 0, result?.Edges.Count ?? 0);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireAuthorization(Policy);
    }
}
