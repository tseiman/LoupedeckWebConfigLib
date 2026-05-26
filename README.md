# LoupedeckWebConfigLib
This is an *AI (Codex)* generated library for Loupedeck plugins.

`LoupedeckWebConfigLib` is a .NET 8 class library for local Loupedeck configuration screens. After `ActivateConfig()` is called, it starts a local web server on a free port greater than `1024`, binds only to `127.0.0.1`, and can optionally open the default browser with the temporary local URL.

The web server keeps running until `DeactivateConfig()` or `Dispose()` is called. The base HTML skeleton lives in `LoupedeckWebConfigLib/wwwroot/index.html`, and the styling lives in `LoupedeckWebConfigLib/wwwroot/style.css`. Both files are copied to the build output and can be edited manually.

The web UI provides `Reset`, `Save`, and `Save & Close`. `Reset` restores the values that were loaded when the page was opened. `Save` uploads the current values to the local server and invokes the matching action callbacks. `Save & Close` saves first and then asks the local server to stop.

Calling `ActivateConfig()` while the local server is already running reuses the existing server and opens the existing URL again; it does not start a second server. Open browser windows keep an SSE connection alive. When the last browser window is closed, the server stops after a short grace period so a later action press starts a fresh configuration session. If an old tab is left open after the server stops, the page disables saving and asks the user to reopen configuration from Loupedeck.

## Table of Contents

- [API Basics](#api-basics)
- [Configuration Model](#configuration-model)
  - [Action Settings](#action-settings)
  - [Plugin-Wide Settings](#plugin-wide-settings)
  - [LWCL HTML Attributes](#lwcl-html-attributes)
  - [Configuration Ownership](#configuration-ownership)
- [Persistent Storage](#persistent-storage)
  - [Logging Delegate Mapping](#logging-delegate-mapping)
- [Runtime Updates](#runtime-updates)
  - [Updating Dynamic Parameters](#updating-dynamic-parameters)
- [HTTP API](#http-api)
  - [Local Endpoints](#local-endpoints)
- [Development](#development)
  - [Build](#build)
  - [Loupedeck SDK Command Example](#loupedeck-sdk-command-example)
  - [Examples](#examples)
  - [Console Smoke Test](#console-smoke-test)

## API Basics

~~~csharp
using System.Text.Json.Nodes;
using LoupedeckWebConfigLib;

LoupedeckWebConfig.RegisterPlugin(new LoupedeckPluginRegistration(
    PluginId: "my-plugin",
    Title: "My Plugin",
    Heading: "My Plugin Configuration",
    Parameters:
    [
        new ConfigParameterDefinition("deviceIp", ConfigParameterType.String, "Device IP"),
        new ConfigParameterDefinition("devicePath", ConfigParameterType.String, "Device path")
    ],
    HtmlSnippet: EmbeddedTextResource.Load<MyPlugin>("Resources.PluginSettings.html"),
    ConfigurationKey: "my-plugin-settings"), configuration =>
{
    // Store a clone in the plugin main class or update plugin services here.
});

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
~~~

The embedded `Resources/MyAction.html` file can use `{{actionGuid}}`; the web server replaces it with the runtime action GUID when rendering the page:

~~~html
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
~~~

Add HTML snippets as embedded resources in the plugin `.csproj`:

~~~xml
<ItemGroup>
  <EmbeddedResource Include="Resources\*.html" />
</ItemGroup>
~~~

## Configuration Model

### Action Settings

Each action class registers itself through `LoupedeckWebConfig.RegisterAction(...)` and must implement `ILoupedeckConfigAction`. Internally, all actions are stored in the same shared service instance and are rendered together on the HTML page. If the same action GUID is registered again, the new registration replaces the old one.

HTML snippets can include JavaScript by assigning a function to `window.LoupedeckConfigProviders[actionGuid]`. This function returns the action configuration as a JSON-compatible object. Existing values are available in the page through `window.getLoupedeckActionConfig(actionGuid)`.

For simple forms, custom JavaScript can be avoided. Mark inputs with `lwcl-config-key` and call `window.registerLoupedeckAutoConfig("{{actionGuid}}")`:

~~~html
<input lwcl-config-key="label" lwcl-default="Start" lwcl-check-regex="[A-Za-z0-9 _-]{1,32}">
<input type="number" lwcl-config-key="count" lwcl-config-type="integer" lwcl-default="1" lwcl-check-regex="[0-9]+">
<select lwcl-config-key="singleMode"></select>
<select lwcl-config-key="multiMode" multiple></select>
<div lwcl-config-key="richMode" lwcl-control="rich-select"></div>
<div lwcl-config-key="richMultiMode" lwcl-control="rich-select" multiple></div>
<script>
  window.registerLoupedeckAutoConfig("{{actionGuid}}");
</script>
~~~

Fields with `lwcl-check-regex` are validated on every input/change. Invalid fields get an `invalid` CSS state, and `Save` plus `Save & Close` are disabled until all fields are valid. `lwcl-default` is only used when no stored value exists. If a stored value is an empty string, it stays empty and must pass validation before saving.

Single select controls save a string value. Select controls with the standard HTML `multiple` attribute save an array of strings. Native `<select>` controls render only option labels. `lwcl-control="rich-select"` renders each option with label and optional description.

`OnConfigurationUpdated(...)` is called whenever the web UI saves new configuration for that action. This lets the action update its internal state immediately without polling the API.

### Plugin-Wide Settings

Plugin-wide settings use the same concepts as action settings: `ConfigParameterDefinition`, an optional embedded HTML snippet, `lwcl-config-key`, `lwcl-check-regex`, persistence, and JSON snapshots. Use plugin-wide settings for values that belong to the plugin as a whole, for example a device IP address, a shared device path, or a media directory. Use action settings for values that are different per Loupedeck action instance.

Register plugin-wide settings through `LoupedeckPluginRegistration`:

~~~csharp
LoupedeckWebConfig.RegisterPlugin(new LoupedeckPluginRegistration(
    PluginId: "atem-controller",
    Title: "ATEM Controller",
    Heading: "ATEM Controller Configuration",
    Parameters:
    [
        new ConfigParameterDefinition("deviceIp", ConfigParameterType.String, "Device IP"),
        new ConfigParameterDefinition("devicePath", ConfigParameterType.String, "Device path"),
        new ConfigParameterDefinition("mediaDirectory", ConfigParameterType.String, "Media directory")
    ],
    HtmlSnippet: EmbeddedTextResource.Load<MyPlugin>("Resources.PluginSettings.html"),
    ConfigurationKey: "atem-controller-settings"), configuration =>
{
    _pluginConfiguration = configuration?.DeepClone();
});
~~~

The embedded plugin HTML can be as small as this:

~~~html
<input inputmode="decimal" lwcl-config-key="deviceIp" lwcl-default="192.168.10.20" lwcl-check-regex="(?:(?:25[0-5]|2[0-4][0-9]|1?[0-9]{1,2})\.){3}(?:25[0-5]|2[0-4][0-9]|1?[0-9]{1,2})">
<input lwcl-config-key="devicePath" lwcl-default="/dev/video0" lwcl-check-regex=".{1,255}">
<input lwcl-config-key="mediaDirectory" lwcl-default="/tmp" lwcl-check-regex=".{1,255}">
<script>
  window.registerLoupedeckPluginAutoConfig();
</script>
~~~

Plugin configuration is saved together with action configuration when the user presses `Save` or `Save & Close`. It can be read from library code with:

~~~csharp
var pluginConfig = LoupedeckWebConfig.GetPluginConfiguration();
~~~

Plugin code can set or refresh plugin-wide configuration before opening the web UI:

~~~csharp
LoupedeckWebConfig.UpdatePluginConfiguration(JsonNode.Parse("""
{
  "deviceIp": "192.168.10.42",
  "devicePath": "/dev/loupedeck-test",
  "mediaDirectory": "/tmp/loupedeck-media"
}
"""));
~~~

### LWCL HTML Attributes

The library only reads attributes with the `lwcl-` prefix. Normal HTML attributes such as `id`, `type`, `min`, `inputmode`, and `class` remain regular HTML and are optional unless the browser needs them.

- `lwcl-config-key`: Required for automatic config binding. The value becomes the JSON property name.
- `lwcl-config-type`: Optional value conversion hint. Supported values are `integer`, `number`, and `boolean`. If omitted, the input's HTML `type` is used.
- `lwcl-default`: Optional default value. It is applied only when no stored value exists. A stored empty string stays empty.
- `lwcl-check-regex`: Optional full-field validation regex. Invalid values mark the field invalid and disable `Save` and `Save & Close`.
- `lwcl-control="rich-select"`: Optional select renderer for options with title and description.

For select options, pass `ConfigParameterOption(Value, Label, Description)` in the matching `ConfigParameterDefinition.Options`. `Description` is ignored by native `<select>` controls and shown by `rich-select`.

### Configuration Ownership

The action should own its configuration state. The library stores cloned JSON snapshots for the web UI and local API; it does not expose mutable references into an action.

Use `ConfigurationKey` for persistence. The runtime `ActionGuid` is used for the current web/API session, but `ConfigurationKey` should be stable across plugin restarts, for example `"my-action"` or `"my-action:{actionParameter}"`. If `ConfigurationKey` is omitted, the runtime GUID is used as fallback.

When an action changes its own configuration programmatically, update the action's internal state first and then call:

~~~csharp
LoupedeckWebConfig.UpdateActionConfiguration(this);
~~~

The library then refreshes its snapshot from `GetConfiguration()` and notifies open web UI pages through Server-Sent Events. The current stored snapshot for one action can also be read from library code with:

~~~csharp
var config = LoupedeckWebConfig.GetActionConfiguration(actionGuid);
~~~

## Persistent Storage

Logi/Loupedeck plugin settings are persistent string values. Configure a store in the plugin main class and trigger loading before or while registering actions:

~~~csharp
const string ConfigSettingName = "WebConfigJson";

LoupedeckWebConfig.Configure(new LoupedeckWebConfigOptions
{
    ConfigStore = new DelegateLoupedeckConfigStore(
        load: () => this.TryGetPluginSetting(ConfigSettingName, out var json) ? json : null,
        save: json => this.SetPluginSetting(ConfigSettingName, json, backupOnline: false)),
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
~~~

The library loads the persisted JSON when the service is configured. When plugin settings are registered, the persisted plugin configuration is passed to the plugin callback. When actions register, matching stored values are applied through `OnConfigurationUpdated(...)` using each action's stable `ConfigurationKey`.

### Logging Delegate Mapping

`LoupedeckWebConfigLib` has no dependency on the Loupedeck SDK. To route logs into a plugin helper like this:

~~~csharp
internal static class PluginLog
{
    [Conditional("DEBUG")]
    public static void Verbose(String text) => ...

    [Conditional("DEBUG")]
    public static void Verbose(Exception ex, String text) => ...

    public static void Info(String text) => ...
    public static void Info(Exception ex, String text) => ...
    public static void Warning(String text) => ...
    public static void Warning(Exception ex, String text) => ...
    public static void Error(String text) => ...
    public static void Error(Exception ex, String text) => ...
}
~~~

add two mapping methods in the plugin main class:

~~~csharp
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
~~~

In `Debug`, use `MinimumLogLevel = LoupedeckWebConfigLogLevel.Verbose` to get request-level detail. In `Release`, use `Warning`; `LogLifecycleMessages = true` still logs short start/stop messages.

For larger files or non-SDK hosts, use the plugin data directory and `FileLoupedeckConfigStore`:

~~~csharp
var pluginDataDirectory = this.GetPluginDataDirectory();
IoHelpers.EnsureDirectoryExists(pluginDataDirectory);

LoupedeckWebConfig.Configure(new LoupedeckWebConfigOptions
{
    ConfigStore = new FileLoupedeckConfigStore(Path.Combine(pluginDataDirectory, "web-config.json"))
});
~~~

## Runtime Updates

### Updating Dynamic Parameters

An action can change its available parameters or HTML snippet by changing the data returned through its `Registration` property and calling:

~~~csharp
LoupedeckWebConfig.UpdateActionRegistration(myAction);
~~~

Open configuration pages listen on `GET /events` using Server-Sent Events. When a registration or action-owned configuration changes, the page reloads automatically so changed values or refreshed select-box options become visible.

## HTTP API

### Local Endpoints

- `GET /` renders the configuration page.
- `GET /style.css` returns the CSS.
- `GET /config` returns all plugin, action, and saved configuration data as JSON.
- `GET /events` opens a Server-Sent Events stream for live web UI reloads.
- `POST /config` stores plugin and action configuration. The web UI sends `{ "plugin": {...}, "actions": { "{actionGuid}": {...} } }`; the old flat action-GUID object shape is still accepted.
- `GET /plugin/config` returns only the stored plugin-wide configuration JSON.
- `POST /plugin/config` stores only the plugin-wide configuration JSON.
- `GET /actions/{actionGuid}/config` returns only the stored configuration JSON for one registered action.
- `POST /actions/{actionGuid}/config` stores JSON for one registered action.
- `POST /close` stops the local web server. The web UI uses this after `Save & Close`.

Remote access is rejected through loopback binding plus an additional loopback check for every request.

## Development

### Build

Debug:

~~~bash
dotnet build LoupedeckWebConfigLib.sln -c Debug
~~~

Debug builds include the console smoke-test project.

Release:

~~~bash
dotnet build LoupedeckWebConfigLib.sln -c Release
~~~

Release builds only build `LoupedeckWebConfigLib`; the console smoke-test project is excluded from the solution's Release build configuration.

In `Debug`, stdout logging is enabled by default and `MinimumLogLevel` is `Verbose`. In `Release`, stdout logging is disabled by default and `MinimumLogLevel` is `Warning`; lifecycle messages such as server start/stop are still emitted when `LogLifecycleMessages` is true. Use `LogMessage` and `LogException` to map library logs to plugin logging without adding any Loupedeck SDK dependency to the library.

### Loupedeck SDK Command Example

The core library has no dependency on the Logi/Loupedeck SDK. The Loupedeck command that opens the web configuration UI is intentionally left as plugin code, because the SDK-specific part is only a normal `PluginDynamicCommand` whose `RunCommand(...)` calls `LoupedeckWebConfig.ActivateConfig()`.

Minimal command:

~~~csharp
namespace Loupedeck.MyPlugin
{
    using System;
    using LoupedeckWebConfigLib;

    public sealed class OpenWebConfigCommand : PluginDynamicCommand
    {
        public OpenWebConfigCommand()
            : base(displayName: "Open Configuration", description: "Opens the plugin web configuration", groupName: "Configuration")
        {
        }

        protected override void RunCommand(String actionParameter)
        {
            LoupedeckWebConfig.ActivateConfig();
        }
    }
}
~~~

See `example/OpenWebConfigCommand.cs` for the commented version.

### Examples

See the `example` folder for integration sketches:

- `PluginLoadExample.cs` shows plugin startup, persistent storage via Loupedeck plugin settings, plugin registration, and shutdown cleanup.
- `ActionRegistrationExample.cs` shows a dynamic command implementing `ILoupedeckConfigAction`, using a runtime action GUID plus stable `ConfigurationKey`.
- `OpenWebConfigCommand.cs` shows the minimal SDK command class that opens the configuration UI.
- `Resources/MacroPlayCommand.html` shows an embedded HTML snippet using `lwcl-config-key`, `lwcl-check-regex`, and `window.registerLoupedeckAutoConfig("{{actionGuid}}")`.

### Console Smoke Test

Run without opening a browser:

~~~bash
dotnet run --project LoupedeckWebConfigLib.TestConsole -c Debug -- --no-browser
~~~

Run with browser opening and manual shutdown:

~~~bash
dotnet run --project LoupedeckWebConfigLib.TestConsole -c Debug -- --interactive
~~~
