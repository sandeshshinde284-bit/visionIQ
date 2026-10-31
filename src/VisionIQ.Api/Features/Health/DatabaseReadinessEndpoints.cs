using Npgsql;
using VisionIQ.Api.Configuration;

namespace VisionIQ.Api.Features.Health;

/// <summary>A sanitized readiness outcome with no database names or credentials.</summary>
public sealed record DatabaseReadinessResponse(string Status);

/// <summary>
/// FND-04 private deployment readiness probe, separate from process liveness.
/// A successful response proves the configured API PostgreSQL identity can execute SELECT 1.
/// It intentionally does not claim access to business tables or permission-policy acceptance.
/// </summary>
public static class DatabaseReadinessEndpoints
{
    /// <summary>
    /// Registers an internal readiness route which returns only ready/not_ready;
    /// no connection strings, database errors, user names or raw SQL are exposed.
    /// The Cloud Run service must remain internal and Invoker-authenticated.
    /// </summary>
    public static IEndpointRouteBuilder MapDatabaseReadiness(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/ready", async (DatabaseOptions options, CancellationToken cancellationToken) =>
        {
            try
            {
                var connectionString = new NpgsqlConnectionStringBuilder
                {
                    Host = options.Host,
                    Port = options.Port,
                    Database = options.Name,
                    Username = options.User,
                    Password = options.Password,
                    Timeout = 3,
                    CommandTimeout = 3,
                };
                connectionString["SSL Mode"] = options.SslMode;

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await using var connection = new NpgsqlConnection(connectionString.ConnectionString);
                await connection.OpenAsync(timeout.Token);
                await using var command = new NpgsqlCommand("SELECT 1", connection);
                var value = await command.ExecuteScalarAsync(timeout.Token);
                if (value is int number && number == 1)
                {
                    return Results.Ok(new DatabaseReadinessResponse("ready"));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Failure is intentional and externally indistinguishable. Do not
                // log connection strings, DB passwords or provider exception details.
            }

            return Results.Json(new DatabaseReadinessResponse("not_ready"), statusCode: 503);
        }).WithName("DatabaseReadiness");

        return endpoints;
    }
}
