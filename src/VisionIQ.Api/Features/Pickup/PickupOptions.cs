using System.Globalization;

namespace VisionIQ.Api.Features.Pickup;

/// <summary>
/// BRF-01 settings. Pickup is off unless PICKUP_POLICY_FILE is set; when it is, PICKUP_WEBHOOK_SECRET is required
/// so the webhook is never open. The sweep also needs ADO_ORG_URL and ADO_READ_PAT (see
/// <see cref="PickupServiceRegistration"/>).
/// </summary>
public sealed record PickupOptions(
    PickupPolicy Policy,
    string WebhookSecret,
    TimeSpan WorkerInterval,
    TimeSpan SweepInterval,
    TimeSpan SweepLookback)
{
    public const int MinimumSecretLength = 24;

    /// <summary>Null when pickup is not configured.</summary>
    /// <exception cref="InvalidOperationException">Pickup is configured but a setting or the policy file is invalid.</exception>
    public static PickupOptions? FromConfiguration(IConfiguration configuration, Func<string, string>? readFile = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var path = configuration["PICKUP_POLICY_FILE"];
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string json;
        try
        {
            json = (readFile ?? File.ReadAllText)(path.Trim());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("PICKUP_POLICY_FILE cannot be read.", error);
        }

        var secret = configuration["PICKUP_WEBHOOK_SECRET"];
        if (string.IsNullOrWhiteSpace(secret) || secret.Trim().Length < MinimumSecretLength)
        {
            throw new InvalidOperationException(
                $"PICKUP_WEBHOOK_SECRET is required (at least {MinimumSecretLength} characters) when PICKUP_POLICY_FILE is set.");
        }

        var sweepInterval = TimeSpan.FromMinutes(Number(configuration, "PICKUP_SWEEP_INTERVAL_MINUTES", 15, 0, 1440));
        var lookback = TimeSpan.FromHours(Number(configuration, "PICKUP_SWEEP_LOOKBACK_HOURS", 24, 1, 720));
        if (lookback < 2 * sweepInterval)
        {
            // Each sweep must overlap the previous one, or pickups between sweeps are never looked at.
            throw new InvalidOperationException("PICKUP_SWEEP_LOOKBACK_HOURS must cover at least two sweep intervals.");
        }

        return new PickupOptions(
            PickupPolicy.Parse(json),
            secret.Trim(),
            TimeSpan.FromSeconds(Number(configuration, "PICKUP_WORKER_INTERVAL_SECONDS", 2, 0.1, 60)),
            sweepInterval,
            lookback);
    }

    private static double Number(IConfiguration configuration, string name, double fallback, double min, double max)
    {
        var text = configuration[name];
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : throw new InvalidOperationException($"{name} must be a number from {min} to {max}.");
    }
}
