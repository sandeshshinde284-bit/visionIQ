namespace VisionIQ.Api.Configuration;

/// <summary>
/// Database connection settings read from the flat keys shared with the root .env file
/// (DB_HOST, DB_PORT, DB_NAME, DB_USER, DB_PASSWORD, DB_SSL_MODE).
/// </summary>
public sealed record DatabaseOptions(
    string Host,
    int Port,
    string Name,
    string User,
    string? Password,
    string SslMode)
{
    public static DatabaseOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new DatabaseOptions(
            Host: ValueOrDefault(configuration["DB_HOST"], "localhost"),
            Port: ParsePort(configuration["DB_PORT"]),
            Name: ValueOrDefault(configuration["DB_NAME"], "visioniq"),
            User: ValueOrDefault(configuration["DB_USER"], "visioniq"),
            Password: string.IsNullOrWhiteSpace(configuration["DB_PASSWORD"]) ? null : configuration["DB_PASSWORD"],
            SslMode: ValueOrDefault(configuration["DB_SSL_MODE"], "disable"));
    }

    /// <summary>Describes the target without ever including the password.</summary>
    public override string ToString() => $"{User}@{Host}:{Port}/{Name} (ssl={SslMode})";

    private static string ValueOrDefault(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

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
