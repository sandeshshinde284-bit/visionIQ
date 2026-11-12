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

    /// <summary>The most ids PICKUP_ALLOWED_WORK_ITEM_IDS may hold; the list is meant for a pilot, not a rollout.</summary>
    public const int MaximumAllowedWorkItems = 200;

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
            PickupPolicy.Parse(json) with { AllowedWorkItemIds = AllowedWorkItemIds(configuration["PICKUP_ALLOWED_WORK_ITEM_IDS"]) },
            secret.Trim(),
            TimeSpan.FromSeconds(Number(configuration, "PICKUP_WORKER_INTERVAL_SECONDS", 2, 0.1, 60)),
            sweepInterval,
            lookback);
    }

    /// <summary>
    /// Reads PICKUP_ALLOWED_WORK_ITEM_IDS ("4711, 4712"). Empty or unset means no restriction. Anything that is
    /// not a list of positive work-item numbers stops start-up: a typo must never widen pickup to every ticket.
    /// </summary>
    public static IReadOnlySet<int>? AllowedWorkItemIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var ids = new SortedSet<int>();
        foreach (var part in value.Split([',', ';'], StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                throw new InvalidOperationException(
                    "PICKUP_ALLOWED_WORK_ITEM_IDS must be a comma-separated list of work-item numbers, for example 4711,4712.");
            }

            ids.Add(id);
        }

        return ids.Count <= MaximumAllowedWorkItems
            ? ids
            : throw new InvalidOperationException(
                $"PICKUP_ALLOWED_WORK_ITEM_IDS holds more than {MaximumAllowedWorkItems} ids; remove the setting to pick up every eligible item.");
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
