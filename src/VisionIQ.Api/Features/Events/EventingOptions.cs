using System.Globalization;

namespace VisionIQ.Api.Features.Events;

/// <summary>
/// FND-05 eventing settings; the same environment names as the Intelligence service. PUBSUB_EMULATOR_HOST is
/// read by the Google client itself and points publishing at the local emulator.
/// </summary>
public sealed record EventingOptions(
    string TenantId,
    string? PubSubProjectId,
    bool DispatchEnabled,
    TimeSpan DispatchInterval)
{
    public static EventingOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var tenant = configuration["TENANT_ID"];
        var project = configuration["PUBSUB_PROJECT_ID"];
        var enabled = bool.TryParse(configuration["EVENTS_DISPATCH_ENABLED"], out var flag) && flag;
        var seconds = double.TryParse(
            configuration["EVENTS_DISPATCH_INTERVAL_SECONDS"], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 2.0;
        if (seconds is <= 0 or > 60)
        {
            throw new InvalidOperationException("EVENTS_DISPATCH_INTERVAL_SECONDS must be greater than 0 and at most 60.");
        }

        if (enabled && string.IsNullOrWhiteSpace(project))
        {
            throw new InvalidOperationException("PUBSUB_PROJECT_ID is required when EVENTS_DISPATCH_ENABLED is true.");
        }

        return new EventingOptions(
            string.IsNullOrWhiteSpace(tenant) ? "visioniq-dev" : tenant.Trim(),
            string.IsNullOrWhiteSpace(project) ? null : project.Trim(),
            enabled,
            TimeSpan.FromSeconds(seconds));
    }
}
