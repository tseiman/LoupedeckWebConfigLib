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
                LogLifecycleMessages = true,
                Log = LoupedeckWebConfigLog.FromDelegates(
                    verbose: PluginLog.Verbose,
                    info: PluginLog.Info,
                    warning: PluginLog.Warning,
                    error: PluginLog.Error,
                    verboseException: PluginLog.Verbose,
                    infoException: PluginLog.Info,
                    warningException: PluginLog.Warning,
                    errorException: PluginLog.Error)
            });

            LoupedeckWebConfig.RegisterPlugin(new LoupedeckPluginRegistration(
                PluginId: "loupedeck-atem-controller",
                Title: "ATEM Controller",
                Heading: "ATEM Controller Configuration",
                Parameters:
                [
                    new ConfigParameterDefinition("deviceIp", ConfigParameterType.String, "Device IP"),
                    new ConfigParameterDefinition("devicePath", ConfigParameterType.String, "Device path"),
                    new ConfigParameterDefinition("mediaDirectory", ConfigParameterType.String, "Media directory")
                ],
                HtmlSnippet: EmbeddedTextResource.Load<LoupedeckAtemControlerPlugin>("Resources.PluginSettings.html"),
                ConfigurationKey: "atem-controller-settings"), this.OnPluginConfigurationUpdated);

            PluginReady?.Invoke();

            ...
        }

        // Receives saved plugin-wide settings such as connection and directory values.
        // Parameters:
        // - configuration: The JSON object saved from the plugin settings section, or null when no settings exist yet.
        // Returns: Nothing.
        private void OnPluginConfigurationUpdated(System.Text.Json.Nodes.JsonNode? configuration)
        {
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
