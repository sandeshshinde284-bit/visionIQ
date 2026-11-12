using Npgsql;
using VisionIQ.Api.Features.Pickup;

namespace VisionIQ.Api.Features.Briefs;

/// <summary>
/// BRF-05 composition root. The options are always registered (the event inbox reads the mode); the store,
/// publisher and worker only when <c>BRIEF_PUBLISH_MODE</c> is <c>record</c> or <c>ado</c>.
/// </summary>
public static class BriefServiceRegistration
{
    /// <param name="services">The application's services.</param>
    /// <param name="configuration">The application's configuration.</param>
    /// <param name="policy">The pickup policy, used to turn a run's project id into the ADO project name.</param>
    public static BriefPublishOptions AddBriefPublishing(
        this IServiceCollection services, IConfiguration configuration, PickupPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = BriefPublishOptions.FromConfiguration(configuration);
        services.AddSingleton(options);
        if (options.Mode == BriefPublishMode.Off)
        {
            return options;
        }

        services.AddSingleton<IBriefPublicationStore>(provider =>
            new PostgresBriefPublicationStore(provider.GetRequiredService<NpgsqlDataSource>()));
        if (options.Mode == BriefPublishMode.Ado)
        {
            services.AddSingleton<IBriefCommentPublisher>(_ => new AdoCommentPublisher(
                new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, options.OrganizationUrl!, options.CommentToken!));
        }
        else
        {
            services.AddSingleton<IBriefCommentPublisher, RecordingCommentPublisher>();
        }

        services.AddSingleton(provider => new BriefPublisher(
            provider.GetRequiredService<IBriefPublicationStore>(),
            provider.GetRequiredService<IBriefCommentPublisher>(),
            policy,
            provider.GetRequiredService<ILogger<BriefPublisher>>()));
        services.AddHostedService<BriefPublishWorker>();
        return options;
    }
}
