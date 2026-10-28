using VisionIQ.Api.Configuration;
using VisionIQ.Api.Features.Health;
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

builder.Services
    .AddOptions<ServiceOptions>()
    .Bind(builder.Configuration.GetSection(ServiceOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(DatabaseOptions.FromConfiguration(builder.Configuration));
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(15));

var app = builder.Build();

app.MapHealthEndpoints();

var logger = app.Services.GetRequiredService<ILogger<Program>>();
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStarted.Register(() => LifecycleLog.Started(logger));
lifetime.ApplicationStopping.Register(() => LifecycleLog.Stopping(logger));

app.Run();

/// <summary>Makes the entry point visible to integration tests.</summary>
public partial class Program
{
}
