using Npgsql;
using VisionIQ.Api.Features.Pickup;

namespace VisionIQ.Api.Features.Fixes;

/// <summary>
/// DEF-05 / DEF-06 composition root. The options are always registered (the event inbox reads them); the store,
/// publisher, head watcher and worker only when <c>FIX_PR_MODE</c> is <c>record</c> or <c>ado</c>.
/// </summary>
public static class FixServiceRegistration
{
    /// <param name="services">The application's services.</param>
    /// <param name="configuration">The application's configuration.</param>
    /// <param name="policy">The pickup policy, used to turn a run's project id into the ADO project name.</param>
    public static FixPullRequestOptions AddFixPullRequests(
        this IServiceCollection services, IConfiguration configuration, PickupPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = FixPullRequestOptions.FromConfiguration(configuration);
        services.AddSingleton(options);
        if (options.Mode == FixPullRequestMode.Off)
        {
            return options;
        }

        services.AddSingleton<IFixPullRequestStore>(provider =>
            new PostgresFixPullRequestStore(provider.GetRequiredService<NpgsqlDataSource>()));
        if (options.Mode == FixPullRequestMode.Ado)
        {
            services.AddSingleton<IFixPullRequestPublisher>(provider => new AdoPullRequestPublisher(
                new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, options.OrganizationUrl!, options.PullRequestToken!,
                provider.GetRequiredService<ILogger<AdoPullRequestPublisher>>()));
        }
        else
        {
            services.AddSingleton<IFixPullRequestPublisher, RecordingPullRequestPublisher>();
        }

        services.AddSingleton(provider => new FixPullRequestPublisher(
            provider.GetRequiredService<IFixPullRequestStore>(),
            provider.GetRequiredService<IFixPullRequestPublisher>(),
            policy,
            provider.GetRequiredService<ILogger<FixPullRequestPublisher>>()));
        services.AddSingleton(provider => new FixPullRequestHeadWatcher(
            provider.GetRequiredService<IFixPullRequestStore>(),
            provider.GetRequiredService<IFixPullRequestPublisher>(),
            policy,
            provider.GetRequiredService<ILogger<FixPullRequestHeadWatcher>>()));
        services.AddHostedService<FixPullRequestWorker>();
        return options;
    }
}
