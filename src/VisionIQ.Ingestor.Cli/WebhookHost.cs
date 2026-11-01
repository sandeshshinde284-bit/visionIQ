using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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

        var http = new HttpClient { Timeout = settings.RequestTimeout };
        var client = new AdoClient(http, settings.Ado);
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
        var handler = new AdoWorkItemChangeHandler(settings.Ado,
            new AdoChangedWorkItemReader(new AdoWorkItemSource(client, settings.Environments)), evidence);
        var endpoint = new AdoWebhookEndpoint(credentials, new SourceChangeProcessor(inbox, handler),
            settings.Ado.OrganizationUrl, settings.Ado.Projects, settings.Ado.Tenant);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 1024 * 1024);
        var port = int.TryParse(configuration.GetValueOrDefault("PORT"), out var configured) ? configured : 8080;
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        await using var app = builder.Build();
        app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "VisionIQ.Ingestor.Webhooks" }));
        app.MapPost(AdoWebhookEndpoint.Route, endpoint.HandleAsync);
        Console.WriteLine($"Listening for ADO work-item Service Hooks on port {port} at {AdoWebhookEndpoint.Route}");
        await app.RunAsync();
        return 0;
    }
}
