namespace VisionIQ.Api.Logging;

/// <summary>Source-generated log messages for the application lifecycle.</summary>
internal static partial class LifecycleLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "VisionIQ.Api started")]
    public static partial void Started(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "VisionIQ.Api is stopping")]
    public static partial void Stopping(ILogger logger);
}
