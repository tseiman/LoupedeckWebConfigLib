// Example: Loupedeck SDK command that opens the shared LoupedeckWebConfigLib configuration UI.
namespace Loupedeck.LoupedeckAtemControlerPlugin
{
    using System;

    using LoupedeckWebConfigLib;

    public sealed class OpenWebConfigCommand : PluginDynamicCommand
    {
        // Registers the command in the Loupedeck UI.
        // Parameters: None.
        // Returns: A new command instance displayed in the Configuration group.
        public OpenWebConfigCommand()
            : base(displayName: "Open Configuration", description: "Opens the plugin web configuration", groupName: "Configuration")
        {
        }

        // Opens or reuses the local-only configuration web server and opens the default browser.
        // Parameters:
        // - actionParameter: Unused by this command because there is only one shared plugin configuration UI.
        // Returns: Nothing.
        protected override void RunCommand(String actionParameter)
        {
            LoupedeckWebConfig.ActivateConfig();
        }
    }
}
