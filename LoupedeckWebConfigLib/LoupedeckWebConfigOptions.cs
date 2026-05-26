// Configures browser launch behavior, logging, and local binding for the web service.
namespace LoupedeckWebConfigLib;

public sealed class LoupedeckWebConfigOptions
{
    public bool OpenBrowser { get; init; } = true;

#if DEBUG
    public bool EnableStdoutLogging { get; init; } = true;
#else
    public bool EnableStdoutLogging { get; init; }
#endif

#if DEBUG
    public LoupedeckWebConfigLogLevel MinimumLogLevel { get; init; } = LoupedeckWebConfigLogLevel.Verbose;
#else
    public LoupedeckWebConfigLogLevel MinimumLogLevel { get; init; } = LoupedeckWebConfigLogLevel.Warning;
#endif

    public bool LogLifecycleMessages { get; init; } = true;

    public Action<LoupedeckWebConfigLogLevel, string>? LogMessage { get; init; }

    public Action<LoupedeckWebConfigLogLevel, Exception, string>? LogException { get; init; }

    public string Host { get; init; } = "127.0.0.1";

    public ILoupedeckConfigStore? ConfigStore { get; init; }

    public bool AutoDeactivateWhenBrowserClosed { get; init; } = true;

    public TimeSpan BrowserDisconnectGracePeriod { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan SseHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(2);
}
