// Example: plugin-level setup for LoupedeckWebConfigLib in a Loupedeck plugin main class.
namespace Loupedeck.LoupedeckAtemControlerPlugin
{
    using System;

    using LoupedeckWebConfigLib;

    public partial class LoupedeckAtemControlerPlugin : Plugin
    {
        private const String WebConfigSettingName = "WebConfigJson";

        // Sets up the plugin, configures persistent web-config storage, and registers the shared plugin metadata.
        public override void Load()
        {
            ...

            LoupedeckWebConfig.Configure(new LoupedeckWebConfigOptions
            {
                ConfigStore = new DelegateLoupedeckConfigStore(
                    load: () => this.TryGetPluginSetting(WebConfigSettingName, out var json) ? json : null,
                    save: json => this.SetPluginSetting(WebConfigSettingName, json, backupOnline: false)),
                OpenBrowser = true,
#if DEBUG
                EnableStdoutLogging = true
#else
                EnableStdoutLogging = false
#endif
            });

            LoupedeckWebConfig.RegisterPlugin(new LoupedeckPluginRegistration(
                PluginId: "loupedeck-atem-controller",
                Title: "ATEM Controller",
                Heading: "ATEM Controller Configuration"));

            PluginReady?.Invoke();

            ...
        }

        // Tears down the web config server and runs the plugin cleanup path.
        public override void Unload()
        {
            LoupedeckWebConfig.DeactivateConfig();

            ...
        }
    }
}
