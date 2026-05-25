# LoupedeckWebConfigLib
This is an *AI (Codex)* generated library for Loupedeck plugins.

`LoupedeckWebConfigLib` is a .NET 8 class library for local Loupedeck configuration screens. After `ActivateConfig()` is called, it starts a local web server on a free port greater than `1024`, binds only to `127.0.0.1`, and can optionally open the default browser with the temporary local URL.

The web server keeps running until `DeactivateConfig()` or `Dispose()` is called. The base HTML skeleton lives in `LoupedeckWebConfigLib/wwwroot/index.html`, and the styling lives in `LoupedeckWebConfigLib/wwwroot/style.css`. Both files are copied to the build output and can be edited manually.

The web UI provides `Reset`, `Save`, and `Save & Close`. `Reset` restores the values that were loaded when the page was opened. `Save` uploads the current values to the local server and invokes the matching action callbacks. `Save & Close` saves first and then asks the local server to stop.

## API Basics

```csharp
using System.Text.Json.Nodes;
using LoupedeckWebConfigLib;

LoupedeckWebConfig.RegisterPlugin(new LoupedeckPluginRegistration(
    PluginId: "my-plugin",
    Title: "My Plugin",
    Heading: "My Plugin Configuration"));

LoupedeckWebConfig.RegisterAction(new MyAction(runtimeActionGuid));

var url = LoupedeckWebConfig.ActivateConfig();
var json = LoupedeckWebConfig.GetConfig();
LoupedeckWebConfig.DeactivateConfig();

public sealed class MyAction : ILoupedeckConfigAction
{
    private readonly Guid _actionGuid;
    private JsonNode? _configuration;

    public MyAction(Guid actionGuid)
    {
        _actionGuid = actionGuid;
    }

    public LoupedeckActionRegistration Registration => new(
        ActionGuid: _actionGuid,
        Name: "My Action",
        Parameters:
        [
            new ConfigParameterDefinition("label", ConfigParameterType.String, "Label")
        ],
        HtmlSnippet: EmbeddedTextResource.Load<MyAction>("Resources.MyAction.html"),
        ConfigurationKey: "my-action");

    public JsonNode? GetConfiguration() => _configuration?.DeepClone();

    public void OnConfigurationUpdated(JsonNode? configuration)
    {
        _configuration = configuration?.DeepClone();
    }
}
```

The embedded `Resources/MyAction.html` file can use `{{actionGuid}}`; the web server replaces it with the runtime action GUID when rendering the page:

```html
<input id="my-label" type="text">
<script>
  (() => {
    const actionGuid = "{{actionGuid}}";
    const config = window.getLoupedeckActionConfig(actionGuid) || {};
    document.getElementById("my-label").value = config.label ?? "";

    window.LoupedeckConfigProviders[actionGuid] = () => ({
      label: document.getElementById("my-label").value
    });
  })();
</script>
```

Add HTML snippets as embedded resources in the plugin `.csproj`:

```xml
<ItemGroup>
  <EmbeddedResource Include="Resources\*.html" />
</ItemGroup>
```

Each action class registers itself through `LoupedeckWebConfig.RegisterAction(...)` and must implement `ILoupedeckConfigAction`. Internally, all actions are stored in the same shared service instance and are rendered together on the HTML page. If the same action GUID is registered again, the new registration replaces the old one.

HTML snippets can include JavaScript by assigning a function to `window.LoupedeckConfigProviders[actionGuid]`. This function returns the action configuration as a JSON-compatible object. Existing values are available in the page through `window.getLoupedeckActionConfig(actionGuid)`.

For simple forms, custom JavaScript can be avoided. Mark inputs with `data-config-key` and call `window.registerLoupedeckAutoConfig("{{actionGuid}}")`:

```html
<input data-config-key="label" data-default="Start" checkRegEx="[A-Za-z0-9 _-]{1,32}">
<input type="number" data-config-key="count" data-config-type="integer" data-default="1" checkRegEx="[0-9]+">
<script>
  window.registerLoupedeckAutoConfig("{{actionGuid}}");
</script>
```

Fields with `checkRegEx` are validated on every input/change. Invalid fields get an `invalid` CSS state, and `Save` plus `Save & Close` are disabled until all fields are valid.

`OnConfigurationUpdated(...)` is called whenever the web UI saves new configuration for that action. This lets the action update its internal state immediately without polling the API.

## Configuration Ownership

The action should own its configuration state. The library stores cloned JSON snapshots for the web UI and local API; it does not expose mutable references into an action.

Use `ConfigurationKey` for persistence. The runtime `ActionGuid` is used for the current web/API session, but `ConfigurationKey` should be stable across plugin restarts, for example `"my-action"` or `"my-action:{actionParameter}"`. If `ConfigurationKey` is omitted, the runtime GUID is used as fallback.

When an action changes its own configuration programmatically, update the action's internal state first and then call:

```csharp
LoupedeckWebConfig.UpdateActionConfiguration(this);
```

The library then refreshes its snapshot from `GetConfiguration()` and notifies open web UI pages through Server-Sent Events. The current stored snapshot for one action can also be read from library code with:

```csharp
var config = LoupedeckWebConfig.GetActionConfiguration(actionGuid);
```

## Persistent Storage

Logi/Loupedeck plugin settings are persistent string values. Configure a store in the plugin main class and trigger loading before or while registering actions:

```csharp
const string ConfigSettingName = "WebConfigJson";

LoupedeckWebConfig.Configure(new LoupedeckWebConfigOptions
{
    ConfigStore = new DelegateLoupedeckConfigStore(
        load: () => this.TryGetPluginSetting(ConfigSettingName, out var json) ? json : null,
        save: json => this.SetPluginSetting(ConfigSettingName, json, backupOnline: false))
});
```

The library loads the persisted JSON when the service is configured. When actions register, matching stored values are applied through `OnConfigurationUpdated(...)` using each action's stable `ConfigurationKey`.

For larger files or non-SDK hosts, use the plugin data directory and `FileLoupedeckConfigStore`:

```csharp
var pluginDataDirectory = this.GetPluginDataDirectory();
IoHelpers.EnsureDirectoryExists(pluginDataDirectory);

LoupedeckWebConfig.Configure(new LoupedeckWebConfigOptions
{
    ConfigStore = new FileLoupedeckConfigStore(Path.Combine(pluginDataDirectory, "web-config.json"))
});
```

## Updating Dynamic Parameters

An action can change its available parameters or HTML snippet by changing the data returned through its `Registration` property and calling:

```csharp
LoupedeckWebConfig.UpdateActionRegistration(myAction);
```

Open configuration pages listen on `GET /events` using Server-Sent Events. When a registration or action-owned configuration changes, the page reloads automatically so changed values or refreshed select-box options become visible.

## Local Endpoints

- `GET /` renders the configuration page.
- `GET /style.css` returns the CSS.
- `GET /config` returns all plugin, action, and saved configuration data as JSON.
- `GET /events` opens a Server-Sent Events stream for live web UI reloads.
- `POST /config` stores a JSON object keyed by action GUID. Each value is passed to the matching action through `OnConfigurationUpdated(...)`.
- `GET /actions/{actionGuid}/config` returns only the stored configuration JSON for one registered action.
- `POST /actions/{actionGuid}/config` stores JSON for one registered action.
- `POST /close` stops the local web server. The web UI uses this after `Save & Close`.

Remote access is rejected through loopback binding plus an additional loopback check for every request.

## Build

Debug:

```bash
dotnet build LoupedeckWebConfigLib.sln -c Debug
```

Debug builds include the console smoke-test project.

Release:

```bash
dotnet build LoupedeckWebConfigLib.sln -c Release
```

Release builds only build `LoupedeckWebConfigLib`; the console smoke-test project is excluded from the solution's Release build configuration.

In `Debug`, `EnableStdoutLogging` is enabled by default. In `Release`, it is disabled by default, but it can be enabled through `LoupedeckWebConfigOptions`.

## Examples

See the `example` folder for integration sketches:

- `PluginLoadExample.cs` shows plugin startup, persistent storage via Loupedeck plugin settings, plugin registration, and shutdown cleanup.
- `ActionRegistrationExample.cs` shows a dynamic command implementing `ILoupedeckConfigAction`, using a runtime action GUID plus stable `ConfigurationKey`.
- `Resources/MacroPlayCommand.html` shows an embedded HTML snippet using `data-config-key`, `checkRegEx`, and `window.registerLoupedeckAutoConfig("{{actionGuid}}")`.

## Console Smoke Test

Run without opening a browser:

```bash
dotnet run --project LoupedeckWebConfigLib.TestConsole -c Debug -- --no-browser
```

Run with browser opening and manual shutdown:

```bash
dotnet run --project LoupedeckWebConfigLib.TestConsole -c Debug -- --interactive
```
