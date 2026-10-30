namespace VisionIQ.Api.Configuration;

/// <summary>
/// PostgreSQL runtime connection settings. Only API-specific credentials are accepted;
/// generic DB_USER/DB_PASSWORD may contain migration administrator credentials.
/// </summary>
public sealed record DatabaseOptions(
    string Host,
    int Port,
    string Name,
    string User,
    string Password,
    string SslMode)
{
    public static DatabaseOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new DatabaseOptions(
            Host: ValueOrDefault(configuration["DB_HOST"], "localhost"),
            Port: ParsePort(configuration["DB_PORT"]),
            Name: ValueOrDefault(configuration["DB_NAME"], "visioniq"),
            User: Required(configuration["DB_API_USER"], "DB_API_USER"),
            Password: Required(configuration["DB_API_PASSWORD"], "DB_API_PASSWORD"),
            SslMode: ValueOrDefault(configuration["DB_SSL_MODE"], "disable"));
    }

    /// <summary>Describes the target without ever including the password.</summary>
    public override string ToString() => $"{User}@{Host}:{Port}/{Name} (ssl={SslMode})";

    private static string ValueOrDefault(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Required(string? value, string key) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{key} is required for the API runtime identity.")
            : value.Trim();

    private static int ParsePort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 5432;
        }

        if (int.TryParse(value, out var port) && port is >= 1 and <= 65535)
        {
            return port;
        }

        throw new InvalidOperationException("DB_PORT must be a whole number between 1 and 65535.");
    }
}
