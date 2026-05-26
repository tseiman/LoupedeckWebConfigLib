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
                EnableStdoutLogging = false,
#if DEBUG
                MinimumLogLevel = LoupedeckWebConfigLogLevel.Verbose,
#else
                MinimumLogLevel = LoupedeckWebConfigLogLevel.Warning,
#endif
                LogLifecycleMessages = true,
                LogMessage = LogWebConfigMessage,
                LogException = LogWebConfigException
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

        // Maps library log messages to the plugin log without adding a Loupedeck SDK dependency to the library.
        // Parameters:
        // - level: Severity from the web configuration library.
        // - text: Message to write.
        // Returns: Nothing.
        private static void LogWebConfigMessage(LoupedeckWebConfigLogLevel level, String text)
        {
            switch (level)
            {
                case LoupedeckWebConfigLogLevel.Verbose:
                    PluginLog.Verbose(text);
                    break;
                case LoupedeckWebConfigLogLevel.Info:
                    PluginLog.Info(text);
                    break;
                case LoupedeckWebConfigLogLevel.Warning:
                    PluginLog.Warning(text);
                    break;
                case LoupedeckWebConfigLogLevel.Error:
                    PluginLog.Error(text);
                    break;
            }
        }

        // Maps library exception logs to the plugin log without adding a Loupedeck SDK dependency to the library.
        // Parameters:
        // - level: Severity from the web configuration library.
        // - exception: Exception that caused the log entry.
        // - text: Message to write.
        // Returns: Nothing.
        private static void LogWebConfigException(LoupedeckWebConfigLogLevel level, Exception exception, String text)
        {
            switch (level)
            {
                case LoupedeckWebConfigLogLevel.Verbose:
                    PluginLog.Verbose(exception, text);
                    break;
                case LoupedeckWebConfigLogLevel.Info:
                    PluginLog.Info(exception, text);
                    break;
                case LoupedeckWebConfigLogLevel.Warning:
                    PluginLog.Warning(exception, text);
                    break;
                case LoupedeckWebConfigLogLevel.Error:
                    PluginLog.Error(exception, text);
                    break;
            }
        }

        // Tears down the web config server and runs the plugin cleanup path.
        public override void Unload()
        {
            LoupedeckWebConfig.DeactivateConfig();

            ...
        }
    }
}
