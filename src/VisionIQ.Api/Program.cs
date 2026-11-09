using Npgsql;
using VisionIQ.Api.Configuration;
using VisionIQ.Api.Features.Events;
using VisionIQ.Api.Features.Health;
using VisionIQ.Api.Features.Evidence;
using VisionIQ.Api.Features.Graph;
using VisionIQ.Api.Features.Pickup;
using VisionIQ.Api.Logging;

var builder = WebApplication.CreateBuilder(args);

// Configuration precedence, lowest to highest:
// appsettings.json < appsettings.{Environment}.json < repository .env (Development only)
// < environment variables < command line.
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddDotEnvFile(builder.Environment.ContentRootPath);

    // Re-add the higher-precedence sources so real environment variables still beat the .env file.
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args);
}

builder.WebHost.UseUrls(ListenAddress.Resolve(builder.Configuration));

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "O";
});
// LOG_LEVEL: Information by default; Debug adds per-item detail such as each access-denial reason.
builder.Logging.SetMinimumLevel(Enum.TryParse<LogLevel>(builder.Configuration["LOG_LEVEL"], true, out var level) ? level : LogLevel.Information);
var pseudonymizer = new IdentityPseudonymizer(builder.Configuration["LOG_PSEUDONYM_KEY"]);
builder.Services.AddSingleton(pseudonymizer);

builder.Services
    .AddOptions<ServiceOptions>()
    .Bind(builder.Configuration.GetSection(ServiceOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(services =>
    DatabaseOptions.FromConfiguration(services.GetRequiredService<IConfiguration>()));
// Ingestion is an internal application service, not an unauthenticated HTTP endpoint.
builder.Services.AddSingleton<IRawEvidenceArchive>(services =>
    new CloudStorageEvidenceArchive(
        services.GetRequiredService<IConfiguration>()["GCS_EVIDENCE_BUCKET"]));
builder.Services.AddSingleton<IVersionedEvidenceStore>(services =>
    new PostgresEvidenceStore(services.GetRequiredService<DatabaseOptions>()));
builder.Services.AddSingleton<EvidenceIngestionService>();
// LRN-07 (B-16): authorized relationship-graph traversal; fail-closed provider access.
builder.Services.AddAdoEvidenceAccess(builder.Configuration);
builder.Services.AddRelationshipGraph();
// LRN-07 graph query API: exposed only when Entra ID and ADO access are fully configured.
var graphApi = VisionIQ.Api.Features.GraphApi.GraphApiEndpoints.AddGraphApi(builder.Services, builder.Configuration);

builder.Services.AddSingleton(services =>
    NpgsqlDataSource.Create(services.GetRequiredService<DatabaseOptions>().CreateConnectionStringBuilder().ConnectionString));

// FND-05 events: the inbox and push endpoint are always on; publishing the outbox is opt-in.
var eventing = EventingOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(eventing);
builder.Services.AddSingleton<IInboundEventProcessor, PostgresInboundEventProcessor>();
builder.Services.AddSingleton<IEventPublisher, PubSubEventPublisher>();
if (eventing.DispatchEnabled)
{
    builder.Services.AddHostedService<OutboxDispatcher>();
}

// BRF-01 ticket pickup: off unless PICKUP_POLICY_FILE is set (then PICKUP_WEBHOOK_SECRET is required).
var pickup = builder.Services.AddTicketPickup(builder.Configuration);

builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(15));

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter<AnalysisMode>(
            System.Text.Json.JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
});

var app = builder.Build();
app.UseMiddleware<RequestLoggingMiddleware>();

// Validate runtime database credentials during startup, after test-host configuration is applied.
_ = app.Services.GetRequiredService<DatabaseOptions>();

app.MapHealthEndpoints();
app.MapDatabaseReadiness();
if (graphApi)
{
    app.UseAuthentication();
    app.UseAuthorization();
    VisionIQ.Api.Features.GraphApi.GraphApiEndpoints.MapGraphApi(app);
}
app.MapPubSubPush();
if (pickup is not null)
{
    app.MapAdoPickupWebhook();
}

if (app.Environment.IsDevelopment())
{
    app.MapDevelopmentAnalysisRequests();
}

var logger = app.Services.GetRequiredService<ILogger<Program>>();
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStarted.Register(() => LifecycleLog.Started(logger));
if (!pseudonymizer.IsKeyed)
{
    LifecycleLog.UnkeyedPseudonyms(logger);
}
lifetime.ApplicationStopping.Register(() => LifecycleLog.Stopping(logger));

app.Run();

/// <summary>Makes the entry point visible to integration tests.</summary>
public partial class Program
{
}
