// Runs a console smoke test that registers sample actions and exercises the local web service.
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LoupedeckWebConfigLib;

var openBrowser = !args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase);
var interactive = args.Contains("--interactive", StringComparer.OrdinalIgnoreCase);

LoupedeckWebConfig.Configure(new LoupedeckWebConfigOptions
{
    OpenBrowser = openBrowser,
    EnableStdoutLogging = true,
    ConfigStore = new FileLoupedeckConfigStore(Path.Combine(Path.GetTempPath(), "LoupedeckWebConfigLib.TestConsole", "config.json"))
});

LoupedeckWebConfig.RegisterPlugin(new LoupedeckPluginRegistration(
    PluginId: "sample-plugin",
    Title: "Sample Loupedeck Plugin",
    Heading: "Sample Plugin Configuration",
    Description: "Console smoke test for LoupedeckWebConfigLib.",
    Parameters:
    [
        new ConfigParameterDefinition("deviceIp", ConfigParameterType.String, "Device IP", "192.168.10.20"),
        new ConfigParameterDefinition("devicePath", ConfigParameterType.String, "Device path", "/dev/video0"),
        new ConfigParameterDefinition("mediaDirectory", ConfigParameterType.String, "Media directory", "/tmp")
    ],
    HtmlSnippet: EmbeddedTextResource.Load<Program>("Resources.PluginSettings.html"),
    ConfigurationKey: "sample-plugin-settings"), configuration =>
{
    Console.WriteLine("Plugin saved configuration:");
    Console.WriteLine(configuration?.ToJsonString() ?? "null");
});
LoupedeckWebConfig.UpdatePluginConfiguration(JsonNode.Parse("""
{
  "deviceIp": "192.168.10.42",
  "devicePath": "/dev/loupedeck-test",
  "mediaDirectory": "/tmp/loupedeck-media"
}
"""));

var sampleAction = new SampleAction();
var secondAction = new SecondSampleAction();
var selectSampleAction = new SelectSampleAction();
LoupedeckWebConfig.RegisterAction(sampleAction);
LoupedeckWebConfig.RegisterAction(secondAction);
LoupedeckWebConfig.RegisterAction(selectSampleAction);

var uri = LoupedeckWebConfig.ActivateConfig();
Console.WriteLine($"Service URL: {uri}");

using var httpClient = new HttpClient();
var indexHtml = await httpClient.GetStringAsync(uri);
Console.WriteLine($"Index HTML length: {indexHtml.Length}");
var clientScript = await httpClient.GetStringAsync(new Uri(uri, "loupedeck-config.js"));
Console.WriteLine($"Client script length: {clientScript.Length}");

var sampleConfig = await httpClient.GetStringAsync(new Uri(uri, $"actions/{sampleAction.Registration.ActionGuid}/config"));
Console.WriteLine("Sample action current config:");
Console.WriteLine(sampleConfig);

if (!openBrowser)
{
    using var sseResponse = await httpClient.GetAsync(new Uri(uri, "events"), HttpCompletionOption.ResponseHeadersRead);
    sseResponse.EnsureSuccessStatusCode();
    using var sseReader = new StreamReader(await sseResponse.Content.ReadAsStreamAsync());
    Console.WriteLine($"SSE event: {await ReadNextSseEventAsync(sseReader)}");

    secondAction.EnableExpertMode();
    LoupedeckWebConfig.UpdateActionRegistration(secondAction);
    Console.WriteLine($"SSE event: {await ReadNextSseEventAsync(sseReader)}");
}
else
{
    secondAction.EnableExpertMode();
    LoupedeckWebConfig.UpdateActionRegistration(secondAction);
}

var savePayload = new Dictionary<string, object?>
{
    ["plugin"] = new
    {
        deviceIp = "192.168.10.42",
        devicePath = "/dev/loupedeck-test",
        mediaDirectory = "/tmp/loupedeck-media"
    },
    ["actions"] = new Dictionary<string, object?>
    {
        [sampleAction.Registration.ActionGuid.ToString()] = new
        {
            buttonText = "Configured from console",
            repeatCount = 3,
            enabled = true
        },
        [secondAction.Registration.ActionGuid.ToString()] = new
        {
            mode = "advanced"
        },
        [selectSampleAction.Registration.ActionGuid.ToString()] = new
        {
            simpleSingle = "camera-b",
            simpleMulti = new[] { "camera-a", "camera-c" },
            richSingle = "program",
            richMulti = new[] { "macro-start", "macro-stop" }
        }
    }
};

var saveResponse = await httpClient.PostAsJsonAsync(new Uri(uri, "config"), savePayload);
saveResponse.EnsureSuccessStatusCode();

Console.WriteLine("GetConfig() after batch save:");
Console.WriteLine(LoupedeckWebConfig.GetConfig());

if (interactive || openBrowser)
{
    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    Console.WriteLine(openBrowser
        ? "Press Ctrl-C or close the configuration browser window to stop the local config server."
        : "Press Ctrl-C to stop the local config server.");
    try
    {
        while (!shutdown.IsCancellationRequested && LoupedeckWebConfig.Shared.IsActive)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), shutdown.Token);
        }
    }
    catch (OperationCanceledException)
    {
    }

    if (!shutdown.IsCancellationRequested)
    {
        Console.WriteLine("Configuration browser window closed; stopping test app.");
    }
}

LoupedeckWebConfig.DeactivateConfig();
Console.WriteLine("Done.");

static async Task<string> ReadNextSseEventAsync(StreamReader reader)
{
    while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is { } line)
    {
        if (line.StartsWith("event: ", StringComparison.Ordinal))
        {
            return line["event: ".Length..];
        }
    }

    return "closed";
}

internal sealed class SampleAction : ILoupedeckConfigAction
{
    private readonly Guid _actionGuid = Guid.NewGuid();
    private JsonNode? _configuration = JsonNode.Parse("""
{
  "buttonText": "Existing Value",
  "repeatCount": 7,
  "enabled": true
}
""");

    public LoupedeckActionRegistration Registration => new(
        ActionGuid: _actionGuid,
        Name: "Sample Action",
        Parameters:
        [
            new ConfigParameterDefinition("buttonText", ConfigParameterType.String, "Button text", "Start", Required: true),
            new ConfigParameterDefinition("repeatCount", ConfigParameterType.Integer, "Repeat count", "1"),
            new ConfigParameterDefinition("enabled", ConfigParameterType.Boolean, "Enabled", "true")
        ],
        HtmlSnippet: EmbeddedTextResource.Load<SampleAction>("Resources.SampleAction.html"),
        ConfigurationKey: "sample-action");

    public JsonNode? GetConfiguration()
    {
        return _configuration?.DeepClone();
    }

    public void OnConfigurationUpdated(JsonNode? configuration)
    {
        _configuration = configuration?.DeepClone();
        Console.WriteLine("SampleAction saved configuration:");
        Console.WriteLine(_configuration?.ToJsonString() ?? "null");
    }
}

internal sealed class SecondSampleAction : ILoupedeckConfigAction
{
    private readonly Guid _actionGuid = Guid.NewGuid();
    private string[] _modes = ["default", "advanced"];
    private JsonNode? _configuration = JsonNode.Parse("""
{
  "mode": "advanced"
}
""");

    public LoupedeckActionRegistration Registration => new(
        ActionGuid: _actionGuid,
        Name: "Second Sample Action",
        Parameters:
        [
            new ConfigParameterDefinition("mode", ConfigParameterType.Select, "Mode", "default", _modes.Select(mode => new ConfigParameterOption(mode, mode)).ToArray())
            ],
        HtmlSnippet: BuildHtmlSnippet(),
        ConfigurationKey: "second-sample-action");

    public JsonNode? GetConfiguration()
    {
        return _configuration?.DeepClone();
    }

    public void OnConfigurationUpdated(JsonNode? configuration)
    {
        _configuration = configuration?.DeepClone();
        Console.WriteLine("SecondSampleAction saved configuration:");
        Console.WriteLine(_configuration?.ToJsonString() ?? "null");
    }

    public void EnableExpertMode()
    {
        _modes = ["default", "advanced", "expert"];
        Console.WriteLine("SecondSampleAction dynamic modes updated.");
    }

    private string BuildHtmlSnippet()
    {
        var options = string.Join(Environment.NewLine, _modes.Select(mode => $"""    <option value="{mode}">{mode}</option>"""));
        return EmbeddedTextResource.Load<SecondSampleAction>("Resources.SecondSampleAction.html")
            .Replace("{{options}}", options, StringComparison.Ordinal);
    }
}

internal sealed class SelectSampleAction : ILoupedeckConfigAction
{
    private readonly Guid _actionGuid = Guid.NewGuid();
    private JsonNode? _configuration = JsonNode.Parse("""
{
  "simpleSingle": "camera-b",
  "simpleMulti": ["camera-a", "camera-c"],
  "richSingle": "program",
  "richMulti": ["macro-start", "macro-stop"]
}
""");

    private static readonly ConfigParameterOption[] CameraOptions =
    [
        new("camera-a", "Camera A", "Wide shot from the front camera."),
        new("camera-b", "Camera B", "Close-up camera for the speaker."),
        new("camera-c", "Camera C", "Fallback input for secondary content.")
    ];

    private static readonly ConfigParameterOption[] MacroOptions =
    [
        new("preview", "Preview", "Switch the selected input to preview."),
        new("program", "Program", "Switch the selected input to program."),
        new("macro-start", "Start macro", "Run the configured startup macro."),
        new("macro-stop", "Stop macro", "Run the configured stop macro.")
    ];

    public LoupedeckActionRegistration Registration => new(
        ActionGuid: _actionGuid,
        Name: "Select Sample Action",
        Parameters:
        [
            new ConfigParameterDefinition("simpleSingle", ConfigParameterType.Select, "Simple single select", "camera-a", CameraOptions),
            new ConfigParameterDefinition("simpleMulti", ConfigParameterType.Select, "Simple multi select", null, CameraOptions),
            new ConfigParameterDefinition("richSingle", ConfigParameterType.Select, "Rich single select", "preview", MacroOptions),
            new ConfigParameterDefinition("richMulti", ConfigParameterType.Select, "Rich multi select", null, MacroOptions)
        ],
        HtmlSnippet: EmbeddedTextResource.Load<SelectSampleAction>("Resources.SelectSampleAction.html"),
        ConfigurationKey: "select-sample-action");

    public JsonNode? GetConfiguration()
    {
        return _configuration?.DeepClone();
    }

    public void OnConfigurationUpdated(JsonNode? configuration)
    {
        _configuration = configuration?.DeepClone();
        Console.WriteLine("SelectSampleAction saved configuration:");
        Console.WriteLine(_configuration?.ToJsonString() ?? "null");
    }
}
