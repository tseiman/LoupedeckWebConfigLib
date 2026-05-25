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

    public string Host { get; init; } = "127.0.0.1";

    public ILoupedeckConfigStore? ConfigStore { get; init; }
}
