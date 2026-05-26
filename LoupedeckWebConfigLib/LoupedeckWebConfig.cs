// Provides a shared facade so independent plugin/action classes can register with one service.
namespace LoupedeckWebConfigLib;

public static class LoupedeckWebConfig
{
    private static readonly object Sync = new();
    private static LoupedeckWebConfigService _shared = new();

    public static LoupedeckWebConfigService Shared
    {
        get
        {
            lock (Sync)
            {
                return _shared;
            }
        }
    }

    public static void Configure(LoupedeckWebConfigOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (Sync)
        {
            if (_shared.IsActive)
            {
                throw new InvalidOperationException("The shared configuration service cannot be reconfigured while it is active.");
            }

            _shared.Dispose();
            _shared = new LoupedeckWebConfigService(options);
        }
    }

    public static void RegisterPlugin(LoupedeckPluginRegistration plugin)
    {
        Shared.RegisterPlugin(plugin);
    }

    public static void RegisterPlugin(LoupedeckPluginRegistration plugin, Action<System.Text.Json.Nodes.JsonNode?> configurationUpdated)
    {
        Shared.RegisterPlugin(plugin, configurationUpdated);
    }

    public static void RegisterAction(ILoupedeckConfigAction action)
    {
        Shared.RegisterAction(action);
    }

    public static void UpdateActionRegistration(ILoupedeckConfigAction action)
    {
        Shared.UpdateActionRegistration(action);
    }

    public static void UnregisterAction(Guid actionGuid)
    {
        Shared.UnregisterAction(actionGuid);
    }

    public static void UpdateActionConfiguration(ILoupedeckConfigAction action)
    {
        Shared.UpdateActionConfiguration(action);
    }

    public static void UpdatePluginConfiguration(System.Text.Json.Nodes.JsonNode? configuration)
    {
        Shared.UpdatePluginConfiguration(configuration);
    }

    public static Uri ActivateConfig()
    {
        return Shared.ActivateConfig();
    }

    public static void DeactivateConfig()
    {
        Shared.DeactivateConfig();
    }

    public static string GetConfig()
    {
        return Shared.GetConfig();
    }

    public static System.Text.Json.Nodes.JsonNode? GetActionConfiguration(Guid actionGuid)
    {
        return Shared.GetActionConfiguration(actionGuid);
    }

    public static System.Text.Json.Nodes.JsonNode? GetPluginConfiguration()
    {
        return Shared.GetPluginConfiguration();
    }
}
