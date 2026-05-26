// Configures browser launch behavior, logging, and local binding for the web service.
namespace LoupedeckWebConfigLib;

public sealed class LoupedeckWebConfigOptions
{
    public bool OpenBrowser { get; init; } = true;

    public bool LogLifecycleMessages { get; init; } = true;

    public Action<LoupedeckWebConfigLogEntry>? Log { get; init; }

    public string Host { get; init; } = "127.0.0.1";

    public ILoupedeckConfigStore? ConfigStore { get; init; }

    public bool AutoDeactivateWhenBrowserClosed { get; init; } = true;

    public TimeSpan BrowserDisconnectGracePeriod { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan SseHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(2);
}
