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
    Description: "Console smoke test for LoupedeckWebConfigLib."));

var sampleAction = new SampleAction();
var secondAction = new SecondSampleAction();
LoupedeckWebConfig.RegisterAction(sampleAction);
LoupedeckWebConfig.RegisterAction(secondAction);

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

using var sseResponse = await httpClient.GetAsync(new Uri(uri, "events"), HttpCompletionOption.ResponseHeadersRead);
sseResponse.EnsureSuccessStatusCode();
using var sseReader = new StreamReader(await sseResponse.Content.ReadAsStreamAsync());
Console.WriteLine($"SSE event: {await ReadNextSseEventAsync(sseReader)}");

secondAction.EnableExpertMode();
LoupedeckWebConfig.UpdateActionRegistration(secondAction);
Console.WriteLine($"SSE event: {await ReadNextSseEventAsync(sseReader)}");

var savePayload = new Dictionary<string, object?>
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
    }
};

var saveResponse = await httpClient.PostAsJsonAsync(new Uri(uri, "config"), savePayload);
saveResponse.EnsureSuccessStatusCode();

Console.WriteLine("GetConfig() after batch save:");
Console.WriteLine(LoupedeckWebConfig.GetConfig());

if (interactive)
{
    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    Console.WriteLine("Press Ctrl-C to stop the local config server.");
    try
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
    }
    catch (OperationCanceledException)
    {
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
