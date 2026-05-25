// Example: action-level registration with runtime GUID, stable config key, and embedded HTML.
namespace Loupedeck.LoupedeckAtemControlerPlugin
{
    using System;
    using System.Text.Json.Nodes;

    using LoupedeckWebConfigLib;

    public class MacroPlayCommand : PluginMultistateDynamicCommand, ILoupedeckConfigAction
    {
        private const String ConfigKey = "macro-play-command";

        private readonly Guid _webConfigActionGuid = Guid.NewGuid();
        private JsonNode? _webConfig = JsonNode.Parse("""
{
  "buttonLabel": "Run Macro",
  "repeatCount": 1
}
""");

        public MacroPlayCommand()
            : base(groupName: "Misc", displayName: "Run Macro", description: "Runs a macro which was predefined in the ATEM GUI")
        {
            ...

            LoupedeckAtemControlerPlugin.PluginReady += this.OnPluginActionReady;
        }

        // Creates the registration metadata that the web UI uses for this action.
        // Returns the action GUID, display name, parameter definitions, HTML snippet, and stable configuration key.
        public LoupedeckActionRegistration Registration => new(
            ActionGuid: this._webConfigActionGuid,
            Name: "Run Macro",
            Parameters:
            [
                new ConfigParameterDefinition(
                    "macroIndex",
                    ConfigParameterType.Select,
                    "Macro",
                    "",
                    _macroStore?.SupportedMacros.Select(macro => new ConfigParameterOption(macro.Index.ToString(), macro.DisplayName)).ToArray() ?? Array.Empty<ConfigParameterOption>()),
                new ConfigParameterDefinition("buttonLabel", ConfigParameterType.String, "Button label", "Run Macro", Required: true),
                new ConfigParameterDefinition("repeatCount", ConfigParameterType.Integer, "Repeat count", "1")
            ],
            HtmlSnippet: EmbeddedTextResource.Load<MacroPlayCommand>("Resources.MacroPlayCommand.html"),
            ConfigurationKey: ConfigKey);

        // Returns the current configuration snapshot for this action.
        // Parameters: none.
        // Returns: A JSON node containing the current action configuration, or null when no config is set.
        public JsonNode? GetConfiguration() => this._webConfig?.DeepClone();

        // Receives updated configuration from the web UI or from persisted storage.
        // Parameters: configuration - the new configuration JSON for this action.
        // Returns: nothing.
        public void OnConfigurationUpdated(JsonNode? configuration)
        {
            this._webConfig = configuration?.DeepClone();

            ...
        }

        // Registers the action with the shared web config service once the plugin infrastructure is ready.
        // Parameters: none.
        // Returns: nothing.
        private void OnPluginActionReady()
        {
            ...

            LoupedeckWebConfig.RegisterAction(this);
        }

        // Rebuilds the action registration when macro data changes so the select list options stay current.
        // Parameters: none.
        // Returns: nothing.
        private void OnMacroOptionsChanged()
        {
            ...

            LoupedeckWebConfig.UpdateActionRegistration(this);
        }
    }
}
