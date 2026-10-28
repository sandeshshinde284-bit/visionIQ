namespace VisionIQ.Api.Configuration;

/// <summary>Chooses the address the API listens on.</summary>
public static class ListenAddress
{
    public const string Default = "http://localhost:5100";

    /// <summary>
    /// PORT (set by Cloud Run) wins, then ASPNETCORE_URLS (set by Docker Compose or launchSettings),
    /// then the local default http://localhost:5100.
    /// </summary>
    public static string Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var port = configuration["PORT"];
        if (!string.IsNullOrWhiteSpace(port))
        {
            return $"http://0.0.0.0:{port.Trim()}";
        }

        var urls = configuration["ASPNETCORE_URLS"];
        return string.IsNullOrWhiteSpace(urls) ? Default : urls.Trim();
    }
}
