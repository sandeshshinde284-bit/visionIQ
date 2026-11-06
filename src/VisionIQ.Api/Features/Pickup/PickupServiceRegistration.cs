using Npgsql;
using VisionIQ.Api.Features.Events;
using VisionIQ.Ingestor.Ado;

namespace VisionIQ.Api.Features.Pickup;

/// <summary>
/// BRF-01 composition root. With PICKUP_POLICY_FILE unset nothing is registered and <c>/webhooks/ado</c> is not
/// mapped. With it set, the webhook, store and worker are on; the sweep and branch-head resolution also need
/// ADO_ORG_URL and ADO_READ_PAT (PAT scopes: Work Items read, Code read).
/// </summary>
public static class PickupServiceRegistration
{
    /// <summary>Registers pickup when configured; returns its options, or null when pickup is off.</summary>
    public static PickupOptions? AddTicketPickup(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = PickupOptions.FromConfiguration(configuration);
        if (options is null)
        {
            return null;
        }

        services.AddSingleton(options);
        services.AddSingleton<ITicketPickupStore>(provider =>
            new PostgresTicketPickupStore(provider.GetRequiredService<NpgsqlDataSource>()));

        var ado = AdoConnection(configuration, options.Policy);
        if (ado is not null)
        {
            services.AddSingleton(ado);
            services.AddSingleton<IBaseCommitResolver>(ado);
        }

        services.AddSingleton(provider => new PickupProcessor(
            provider.GetRequiredService<ITicketPickupStore>(),
            provider.GetService<IBaseCommitResolver>(),
            options.Policy,
            provider.GetRequiredService<EventingOptions>(),
            provider.GetRequiredService<ILogger<PickupProcessor>>()));
        services.AddHostedService(provider => new PickupWorker(
            provider.GetRequiredService<PickupProcessor>(),
            options,
            provider.GetRequiredService<ILogger<PickupWorker>>(),
            sweepEnabled: ado is not null && options.SweepInterval > TimeSpan.Zero));

        if (ado is not null && options.SweepInterval > TimeSpan.Zero)
        {
            services.AddSingleton(provider => new PickupSweep(
                ado,
                provider.GetRequiredService<ITicketPickupStore>(),
                options.Policy,
                provider.GetRequiredService<ILogger<PickupSweep>>()));
            services.AddHostedService<PickupSweepService>();
        }

        return options;
    }

    private static AdoPickupSource? AdoConnection(IConfiguration configuration, PickupPolicy policy)
    {
        if (!Uri.TryCreate(configuration["ADO_ORG_URL"], UriKind.Absolute, out var organization) ||
            organization.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(configuration["ADO_READ_PAT"]))
        {
            return null;
        }

        var options = new AdoOptions
        {
            OrganizationUrl = organization,
            Projects = policy.Projects.Select(p => p.Project).ToList(),
            PersonalAccessToken = configuration["ADO_READ_PAT"]!,
        };
        return new AdoPickupSource(new AdoClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, options));
    }
}
