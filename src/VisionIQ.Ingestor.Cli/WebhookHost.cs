using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using VisionIQ.Api.Configuration;
using VisionIQ.Api.Features.Backfill;
using VisionIQ.Api.Features.Evidence;
using VisionIQ.Api.Features.TestLinks;
using VisionIQ.Api.Features.TicketFixes;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Configuration;

namespace VisionIQ.Ingestor.Cli;

/// <summary>
/// LRN-05 "serve-webhooks": hosts the ADO Service Hook endpoint. Every event is processed through the idempotent
/// inbox and written with the SAME evidence chain, environment rules (N-8) and projections as the scheduled sync,
/// so a webhook update can never disagree with a sync of the same revision.
/// </summary>
public static class WebhookHost
{
    public static async Task<int> RunAsync(IngestorSettings settings, IReadOnlyDictionary<string, string?> configuration, DatabaseOptions database, string archiveBucket)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configuration);
        WebhookCredentials credentials;
        try
        {
            credentials = new WebhookCredentials(configuration.GetValueOrDefault("ADO_WEBHOOK_USERNAME") ?? string.Empty,
                configuration.GetValueOrDefault("ADO_WEBHOOK_SECRET") ?? string.Empty);
        }
        catch (InvalidOperationException error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }

        using var loggerFactory = IngestorLogging.Create(configuration);
        var http = new HttpClient { Timeout = settings.RequestTimeout };
        var client = new AdoClient(http, settings.Ado, logger: loggerFactory.CreateLogger<AdoClient>());
        await using var evidenceStore = new PostgresEvidenceStore(database);
        await using var linkStore = new PostgresTicketFixLinkStore(database);
        await using var testLinkStore = new PostgresTestLinkStore(database);
        await using var inbox = new PostgresSourceChangeInbox(database);
        var evidence = new TestLinkProjectionSink(
            new TicketFixProjectionSink(
                new CanonicalEvidenceSink(new EvidenceIngestionService(new CloudStorageEvidenceArchive(archiveBucket), evidenceStore),
                    settings.Ado.OrganizationUrl, settings.Ado.Tenant, settings.Confluence?.BaseUrl)
                {
                    Tombstones = evidenceStore, // N-2
                },
                linkStore, settings.Ado.OrganizationUrl),
            testLinkStore, settings.Ado.OrganizationUrl);
        var handler = new AdoChangeHandler(
            new AdoWorkItemChangeHandler(settings.Ado,
                new AdoChangedWorkItemReader(new AdoWorkItemSource(client, settings.Environments)), evidence),
            // LRN-12: PR created/updated/reviewed/abandoned/merged/commented events.
            new AdoPullRequestChangeHandler(settings.Ado, new AdoChangedPullRequestReader(new AdoRepositorySource(client)), evidence));
        var endpoint = new AdoWebhookEndpoint(credentials, new SourceChangeProcessor(inbox, handler),
            settings.Ado.OrganizationUrl, settings.Ado.Projects, settings.Ado.Tenant, loggerFactory.CreateLogger<AdoWebhookEndpoint>());

        var builder = WebApplication.CreateSlimBuilder();
        IngestorLogging.Configure(builder.Logging, configuration);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 1024 * 1024);
        var port = int.TryParse(configuration.GetValueOrDefault("PORT"), out var configured) ? configured : 8080;
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        await using var app = builder.Build();
        Map(app, endpoint);
        Console.WriteLine($"Listening for ADO work-item Service Hooks on port {port} at {AdoWebhookEndpoint.Route}");
        await app.RunAsync();
        return 0;
    }

    /// <summary>
    /// Maps the routes so the handler's IResult (401/500/503...) is executed. Mapping the method group or a
    /// HttpContext lambda binds the RequestDelegate overload, which discards the result and always answers 200
    /// (found by the ASP0016 analyzer in the second-pass review; proven by WebhookHttpTests over real HTTP).
    /// </summary>
    public static void Map(WebApplication app, AdoWebhookEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(endpoint);
        app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "VisionIQ.Ingestor.Webhooks" }));
        // A plain HttpContext -> Task lambda would bind to the RequestDelegate overload and discard the result
        // again; the explicit Func<..., Task<IResult>> cast to Delegate forces the minimal-API handler path.
        Func<HttpContext, Task<IResult>> handler = endpoint.HandleAsync;
        app.MapPost(AdoWebhookEndpoint.Route, (Delegate)handler);
        // LRN-12: a separate URL for PR Service Hook subscriptions (same handler, events are told apart by type).
        app.MapPost(AdoWebhookEndpoint.PullRequestRoute, (Delegate)handler);
    }
}
