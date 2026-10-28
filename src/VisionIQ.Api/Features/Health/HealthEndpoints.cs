using Microsoft.Extensions.Options;
using VisionIQ.Api.Configuration;

namespace VisionIQ.Api.Features.Health;

/// <summary>Response body of the liveness endpoint.</summary>
public sealed record HealthResponse(string Status, string Service);

public static class HealthEndpoints
{
    /// <summary>Maps GET /health. It never calls an external provider.</summary>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(
            "/health",
            (IOptions<ServiceOptions> options) => TypedResults.Ok(new HealthResponse("healthy", options.Value.Name)))
            .WithName("Health");

        return endpoints;
    }
}
