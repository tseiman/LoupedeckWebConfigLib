// Hosts the local-only configuration web server and stores plugin/action configuration state.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace LoupedeckWebConfigLib;

public sealed class LoupedeckWebConfigService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Default,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly object _sync = new();
    private readonly LoupedeckWebConfigOptions _options;
    private readonly Dictionary<Guid, ILoupedeckConfigAction> _actions = new();
    private readonly Dictionary<Guid, JsonNode?> _actionConfigurations = new();
    private readonly Dictionary<string, JsonNode?> _persistedActionConfigurations = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SseClient> _sseClients = [];
    private HttpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _serverTask;

    public LoupedeckWebConfigService(LoupedeckWebConfigOptions? options = null)
    {
        _options = options ?? new LoupedeckWebConfigOptions();
        LoadPersistedConfiguration();
    }

    public LoupedeckPluginRegistration? Plugin { get; private set; }

    public Uri? ServiceUri { get; private set; }

    public bool IsActive => _listener is not null;

    public void RegisterPlugin(LoupedeckPluginRegistration plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        lock (_sync)
        {
            Plugin = plugin;
        }

        Log($"Registered plugin '{plugin.PluginId}' with title '{plugin.Title}'.");
    }

    public void RegisterAction(ILoupedeckConfigAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var registration = action.Registration;

        lock (_sync)
        {
            _actions[registration.ActionGuid] = action;
            var configurationKey = GetConfigurationKey(registration);
            if (_persistedActionConfigurations.TryGetValue(configurationKey, out var persistedConfig))
            {
                action.OnConfigurationUpdated(persistedConfig?.DeepClone());
            }

            _actionConfigurations[registration.ActionGuid] = action.GetConfiguration()?.DeepClone();
        }

        Log($"Registered action '{registration.Name}' ({registration.ActionGuid}).");
    }

    public void UpdateActionRegistration(ILoupedeckConfigAction action)
    {
        RegisterAction(action);
        _ = BroadcastServerEventAsync("registration-updated", new
        {
            actionGuid = action.Registration.ActionGuid
        });
    }

    public void UpdateActionConfiguration(ILoupedeckConfigAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var registration = action.Registration;

        lock (_sync)
        {
            if (!_actions.ContainsKey(registration.ActionGuid))
            {
                throw new InvalidOperationException($"Action '{registration.ActionGuid}' is not registered.");
            }

            _actionConfigurations[registration.ActionGuid] = action.GetConfiguration()?.DeepClone();
            _persistedActionConfigurations[GetConfigurationKey(registration)] = action.GetConfiguration()?.DeepClone();
        }

        PersistConfigurations();
        Log($"Refreshed configuration snapshot for action {registration.ActionGuid}.");
        _ = BroadcastServerEventAsync("configuration-updated", new
        {
            actionGuid = registration.ActionGuid
        });
    }

    public Uri ActivateConfig()
    {
        lock (_sync)
        {
            if (ServiceUri is not null)
            {
                return ServiceUri;
            }

            var port = FindFreeLocalPort();
            var uri = new Uri($"http://{_options.Host}:{port}/");
            _listener = new HttpListener();
            _listener.Prefixes.Add(uri.ToString());
            _listener.Start();

            _cancellation = new CancellationTokenSource();
            _serverTask = Task.Run(() => RunServerAsync(_listener, _cancellation.Token));
            ServiceUri = uri;
            Log($"Started local config web server at {uri}.");
        }

        if (_options.OpenBrowser && ServiceUri is not null)
        {
            OpenDefaultBrowser(ServiceUri);
        }

        return ServiceUri!;
    }

    public void DeactivateConfig()
    {
        HttpListener? listener;
        CancellationTokenSource? cancellation;
        Task? serverTask;

        lock (_sync)
        {
            listener = _listener;
            cancellation = _cancellation;
            serverTask = _serverTask;
            _listener = null;
            _cancellation = null;
            _serverTask = null;
            ServiceUri = null;
        }

        if (listener is null)
        {
            return;
        }

        cancellation?.Cancel();
        listener.Stop();
        listener.Close();

        try
        {
            serverTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static e => e is OperationCanceledException or HttpListenerException))
        {
        }

        cancellation?.Dispose();
        Log("Stopped local config web server.");
    }

    public LoupedeckConfigSnapshot GetConfigSnapshot()
    {
        lock (_sync)
        {
            return new LoupedeckConfigSnapshot(
                Plugin,
                _actions.Values.Select(static action => action.Registration).OrderBy(static action => action.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                new Dictionary<Guid, JsonNode?>(_actionConfigurations));
        }
    }

    public string GetConfig()
    {
        return JsonSerializer.Serialize(GetConfigSnapshot(), JsonOptions);
    }

    public JsonNode? GetActionConfiguration(Guid actionGuid)
    {
        lock (_sync)
        {
            return _actionConfigurations.TryGetValue(actionGuid, out var configuration)
                ? configuration?.DeepClone()
                : null;
        }
    }

    public void Dispose()
    {
        DeactivateConfig();
    }

    private async Task RunServerAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(context), cancellationToken);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            if (!IsLoopbackRequest(context.Request))
            {
                await WriteResponseAsync(context.Response, 403, "text/plain; charset=utf-8", "Forbidden").ConfigureAwait(false);
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            Log($"{context.Request.HttpMethod} {path}");

            if (context.Request.HttpMethod == "GET" && path == "/")
            {
                await WriteResponseAsync(context.Response, 200, "text/html; charset=utf-8", BuildIndexHtml()).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/style.css")
            {
                await WriteResponseAsync(context.Response, 200, "text/css; charset=utf-8", LoadAsset("style.css")).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/loupedeck-config.js")
            {
                await WriteResponseAsync(context.Response, 200, "text/javascript; charset=utf-8", LoadAsset("loupedeck-config.js")).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/config")
            {
                await WriteResponseAsync(context.Response, 200, "application/json; charset=utf-8", GetConfig()).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/events")
            {
                await HandleServerEventsAsync(context).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/config")
            {
                await StoreAllActionConfigurationsAsync(context).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/close")
            {
                await WriteResponseAsync(context.Response, 200, "application/json; charset=utf-8", """{"closing":true}""").ConfigureAwait(false);
                _ = Task.Run(DeactivateConfig);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path.StartsWith("/actions/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/config", StringComparison.OrdinalIgnoreCase))
            {
                await WriteActionConfigurationAsync(context).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path.StartsWith("/actions/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/config", StringComparison.OrdinalIgnoreCase))
            {
                await StoreActionConfigurationAsync(context).ConfigureAwait(false);
                return;
            }

            await WriteResponseAsync(context.Response, 404, "text/plain; charset=utf-8", "Not found").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"Request failed: {ex}");
            if (context.Response.OutputStream.CanWrite)
            {
                await WriteResponseAsync(context.Response, 500, "text/plain; charset=utf-8", "Internal server error").ConfigureAwait(false);
            }
        }
    }

    private async Task StoreActionConfigurationAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? string.Empty;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3 || !Guid.TryParse(segments[1], out var actionGuid))
        {
            await WriteResponseAsync(context.Response, 400, "text/plain; charset=utf-8", "Invalid action GUID").ConfigureAwait(false);
            return;
        }

        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);
        JsonNode? config;

        try
        {
            config = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            await WriteResponseAsync(context.Response, 400, "text/plain; charset=utf-8", "Body must be valid JSON").ConfigureAwait(false);
            return;
        }

        if (!TryStoreActionConfiguration(actionGuid, config))
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        await WriteResponseAsync(context.Response, 200, "application/json; charset=utf-8", """{"saved":true}""").ConfigureAwait(false);
    }

    private async Task StoreAllActionConfigurationsAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);
        JsonObject? configs;

        try
        {
            configs = string.IsNullOrWhiteSpace(body) ? [] : JsonNode.Parse(body)?.AsObject();
        }
        catch (JsonException)
        {
            await WriteResponseAsync(context.Response, 400, "text/plain; charset=utf-8", "Body must be a JSON object").ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException)
        {
            await WriteResponseAsync(context.Response, 400, "text/plain; charset=utf-8", "Body must be a JSON object").ConfigureAwait(false);
            return;
        }

        if (configs is null)
        {
            await WriteResponseAsync(context.Response, 400, "text/plain; charset=utf-8", "Body must be a JSON object").ConfigureAwait(false);
            return;
        }

        foreach (var entry in configs)
        {
            if (!Guid.TryParse(entry.Key, out var actionGuid))
            {
                await WriteResponseAsync(context.Response, 400, "text/plain; charset=utf-8", $"Invalid action GUID '{entry.Key}'").ConfigureAwait(false);
                return;
            }

            if (!TryStoreActionConfiguration(actionGuid, entry.Value?.DeepClone()))
            {
                await WriteResponseAsync(context.Response, 404, "text/plain; charset=utf-8", $"Unknown action GUID '{entry.Key}'").ConfigureAwait(false);
                return;
            }
        }

        await WriteResponseAsync(context.Response, 200, "application/json; charset=utf-8", GetConfig()).ConfigureAwait(false);
    }

    private async Task WriteActionConfigurationAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? string.Empty;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3 || !Guid.TryParse(segments[1], out var actionGuid))
        {
            await WriteResponseAsync(context.Response, 400, "text/plain; charset=utf-8", "Invalid action GUID").ConfigureAwait(false);
            return;
        }

        JsonNode? configuration;
        lock (_sync)
        {
            if (!_actions.ContainsKey(actionGuid))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            configuration = _actionConfigurations.TryGetValue(actionGuid, out var stored) ? stored?.DeepClone() : null;
        }

        await WriteResponseAsync(context.Response, 200, "application/json; charset=utf-8", configuration?.ToJsonString(JsonOptions) ?? "null").ConfigureAwait(false);
    }

    private bool TryStoreActionConfiguration(Guid actionGuid, JsonNode? config)
    {
        ILoupedeckConfigAction action;

        lock (_sync)
        {
            if (!_actions.TryGetValue(actionGuid, out action!))
            {
                return false;
            }

            _actionConfigurations[actionGuid] = config?.DeepClone();
        }

        action.OnConfigurationUpdated(config?.DeepClone());
        lock (_sync)
        {
            _persistedActionConfigurations[GetConfigurationKey(action.Registration)] = action.GetConfiguration()?.DeepClone();
        }

        PersistConfigurations();
        Log($"Stored configuration for action {actionGuid}.");
        return true;
    }

    private async Task HandleServerEventsAsync(HttpListenerContext context)
    {
        var response = context.Response;
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Connection"] = "keep-alive";

        var writer = new StreamWriter(response.OutputStream, new UTF8Encoding(false))
        {
            AutoFlush = true
        };
        var client = new SseClient(Guid.NewGuid(), response, writer, new SemaphoreSlim(1, 1));

        lock (_sync)
        {
            _sseClients.Add(client);
        }

        Log($"SSE client connected ({client.Id}).");

        try
        {
            await WriteServerEventAsync(client, "connected", new { connected = true }).ConfigureAwait(false);
            var cancellationToken = _cancellation?.Token ?? CancellationToken.None;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            lock (_sync)
            {
                _sseClients.Remove(client);
            }

            client.WriteLock.Dispose();
            writer.Dispose();
            response.Close();
            Log($"SSE client disconnected ({client.Id}).");
        }
    }

    private async Task BroadcastServerEventAsync(string eventName, object data)
    {
        SseClient[] clients;
        lock (_sync)
        {
            clients = _sseClients.ToArray();
        }

        foreach (var client in clients)
        {
            try
            {
                await WriteServerEventAsync(client, eventName, data).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or HttpListenerException)
            {
                lock (_sync)
                {
                    _sseClients.Remove(client);
                }
            }
        }
    }

    private static async Task WriteServerEventAsync(SseClient client, string eventName, object data)
    {
        await client.WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var payload = JsonSerializer.Serialize(data, JsonOptions);
            await client.Writer.WriteAsync($"event: {eventName}\n").ConfigureAwait(false);
            await client.Writer.WriteAsync($"data: {payload}\n\n").ConfigureAwait(false);
            await client.Writer.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            client.WriteLock.Release();
        }
    }

    private string BuildIndexHtml()
    {
        var html = LoadAsset("index.html");
        var snapshot = GetConfigSnapshot();
        var title = snapshot.Plugin?.Title ?? "Loupedeck Configuration";
        var heading = snapshot.Plugin?.Heading ?? title;
        var actionsHtml = string.Join(Environment.NewLine, snapshot.Actions.Select(BuildActionHtml));
        var bootstrapJson = JsonSerializer.Serialize(snapshot, JsonOptions);

        return html
            .Replace("{{title}}", WebUtility.HtmlEncode(title), StringComparison.Ordinal)
            .Replace("{{heading}}", WebUtility.HtmlEncode(heading), StringComparison.Ordinal)
            .Replace("{{actions}}", actionsHtml, StringComparison.Ordinal)
            .Replace("{{configJson}}", bootstrapJson, StringComparison.Ordinal);
    }

    private static string BuildActionHtml(LoupedeckActionRegistration action)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"""<section class="action" data-action-guid="{action.ActionGuid}">""");
        builder.AppendLine($"""  <h2>{WebUtility.HtmlEncode(action.Name)}</h2>""");

        if (!string.IsNullOrWhiteSpace(action.HtmlSnippet))
        {
            builder.AppendLine("""  <div class="snippet">""");
            builder.AppendLine(RenderActionSnippet(action));
            builder.AppendLine("""  </div>""");
        }

        builder.AppendLine("</section>");
        return builder.ToString();
    }

    private static string RenderActionSnippet(LoupedeckActionRegistration action)
    {
        return (action.HtmlSnippet ?? string.Empty)
            .Replace("{{actionGuid}}", action.ActionGuid.ToString(), StringComparison.Ordinal)
            .Replace("{{configurationKey}}", WebUtility.HtmlEncode(GetConfigurationKey(action)), StringComparison.Ordinal);
    }

    private static string GetConfigurationKey(LoupedeckActionRegistration registration)
    {
        return string.IsNullOrWhiteSpace(registration.ConfigurationKey)
            ? registration.ActionGuid.ToString()
            : registration.ConfigurationKey;
    }

    private void LoadPersistedConfiguration()
    {
        var json = _options.ConfigStore?.Load();
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            var persisted = JsonSerializer.Deserialize<PersistedLoupedeckConfig>(json, JsonOptions);
            if (persisted is null)
            {
                return;
            }

            foreach (var entry in persisted.ActionConfigurations)
            {
                _persistedActionConfigurations[entry.Key] = entry.Value?.DeepClone();
            }

            Log($"Loaded {_persistedActionConfigurations.Count} persisted action configuration(s).");
        }
        catch (JsonException ex)
        {
            Log($"Could not load persisted configuration: {ex.Message}");
        }
    }

    private void PersistConfigurations()
    {
        var store = _options.ConfigStore;
        if (store is null)
        {
            return;
        }

        Dictionary<string, JsonNode?> snapshot;
        lock (_sync)
        {
            snapshot = _persistedActionConfigurations.ToDictionary(
                static entry => entry.Key,
                static entry => entry.Value?.DeepClone(),
                StringComparer.OrdinalIgnoreCase);
        }

        var persisted = new PersistedLoupedeckConfig(1, snapshot);
        store.Save(JsonSerializer.Serialize(persisted, JsonOptions));
        Log($"Persisted {snapshot.Count} action configuration(s).");
    }

    private static bool IsLoopbackRequest(HttpListenerRequest request)
    {
        var remoteAddress = request.RemoteEndPoint?.Address;
        var localAddress = request.LocalEndPoint?.Address;

        return remoteAddress is not null
            && localAddress is not null
            && IPAddress.IsLoopback(remoteAddress)
            && IPAddress.IsLoopback(localAddress);
    }

    private static int FindFreeLocalPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string LoadAsset(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Required web asset '{fileName}' was not found at '{path}'.", path);
        }

        return File.ReadAllText(path, Encoding.UTF8);
    }

    private static async Task WriteResponseAsync(HttpListenerResponse response, int statusCode, string contentType, string body)
    {
        var buffer = Encoding.UTF8.GetBytes(body);
        response.StatusCode = statusCode;
        response.ContentType = contentType;
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer).ConfigureAwait(false);
        response.Close();
    }

    private void OpenDefaultBrowser(Uri uri)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(uri.ToString()) { UseShellExecute = true }
                : OperatingSystem.IsMacOS()
                    ? new ProcessStartInfo("open", uri.ToString())
                    : new ProcessStartInfo("xdg-open", uri.ToString());
            process.Start();
            Log($"Opened default browser for {uri}.");
        }
        catch (Exception ex)
        {
            Log($"Could not open default browser: {ex.Message}");
        }
    }

    private void Log(string message)
    {
        if (_options.EnableStdoutLogging)
        {
            Console.WriteLine($"[LoupedeckWebConfigLib] {message}");
        }
    }

    private sealed record SseClient(Guid Id, HttpListenerResponse Response, StreamWriter Writer, SemaphoreSlim WriteLock);
}
