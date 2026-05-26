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
#if DEBUG
    private const LoupedeckWebConfigLogLevel MinimumLogLevel = LoupedeckWebConfigLogLevel.Verbose;
#else
    private const LoupedeckWebConfigLogLevel MinimumLogLevel = LoupedeckWebConfigLogLevel.Warning;
#endif

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
    private Action<JsonNode?>? _pluginConfigurationUpdated;
    private JsonNode? _pluginConfiguration;
    private JsonNode? _persistedPluginConfiguration;
    private HttpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _autoDeactivateCancellation;
    private Task? _serverTask;

    public LoupedeckWebConfigService(LoupedeckWebConfigOptions? options = null)
    {
        _options = options ?? new LoupedeckWebConfigOptions();
        LoadPersistedConfiguration();
    }

    public LoupedeckPluginRegistration? Plugin { get; private set; }

    public Uri? ServiceUri { get; private set; }

    public bool IsActive => _listener is not null;

    public void RegisterPlugin(LoupedeckPluginRegistration plugin, Action<JsonNode?>? configurationUpdated = null)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        JsonNode? pluginConfiguration;
        lock (_sync)
        {
            Plugin = plugin;
            _pluginConfigurationUpdated = configurationUpdated;
            _pluginConfiguration = _persistedPluginConfiguration?.DeepClone() ?? _pluginConfiguration;
            pluginConfiguration = _pluginConfiguration?.DeepClone();
        }

        configurationUpdated?.Invoke(pluginConfiguration?.DeepClone());
        LogVerbose($"Registered plugin '{plugin.PluginId}' with title '{plugin.Title}'.");
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

        LogVerbose($"Registered action '{registration.Name}' ({registration.ActionGuid}) key='{GetConfigurationKey(registration)}' parameters={registration.Parameters.Count} options=[{DescribeParameterOptions(registration)}].");
    }

    public void UpdateActionRegistration(ILoupedeckConfigAction action)
    {
        RegisterAction(action);
        _ = BroadcastServerEventAsync("registration-updated", new
        {
            actionGuid = action.Registration.ActionGuid
        });
    }

    public void UnregisterAction(Guid actionGuid)
    {
        lock (_sync)
        {
            _actions.Remove(actionGuid);
            _actionConfigurations.Remove(actionGuid);
        }

        LogVerbose($"Unregistered action {actionGuid}.");
        _ = BroadcastServerEventAsync("registration-updated", new
        {
            actionGuid
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
        LogVerbose($"Refreshed configuration snapshot for action {registration.ActionGuid}.");
        _ = BroadcastServerEventAsync("configuration-updated", new
        {
            actionGuid = registration.ActionGuid
        });
    }

    public void UpdatePluginConfiguration(JsonNode? configuration)
    {
        StorePluginConfiguration(configuration?.DeepClone());
        _ = BroadcastServerEventAsync("configuration-updated", new
        {
            plugin = Plugin?.PluginId
        });
    }

    public Uri ActivateConfig()
    {
        Uri uri;

        lock (_sync)
        {
            if (ServiceUri is not null)
            {
                uri = ServiceUri;
            }
            else
            {
                var port = FindFreeLocalPort();
                uri = new Uri($"http://{_options.Host}:{port}/");
                _listener = new HttpListener();
                _listener.Prefixes.Add(uri.ToString());
                _listener.Start();

                _cancellation = new CancellationTokenSource();
                _serverTask = Task.Run(() => RunServerAsync(_listener, _cancellation.Token));
                ServiceUri = uri;
                LogInfo($"Started local config web server at {uri}.", isLifecycle: true);
            }
        }

        if (_options.OpenBrowser)
        {
            OpenDefaultBrowser(uri);
        }

        return uri;
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
            _autoDeactivateCancellation?.Cancel();
            _autoDeactivateCancellation?.Dispose();
            _autoDeactivateCancellation = null;
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
        LogInfo("Stopped local config web server.", isLifecycle: true);
    }

    public LoupedeckConfigSnapshot GetConfigSnapshot()
    {
        lock (_sync)
        {
            return new LoupedeckConfigSnapshot(
                Plugin,
                _pluginConfiguration?.DeepClone(),
                _actions.Values.Select(static action => action.Registration).OrderBy(static action => action.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                new Dictionary<Guid, JsonNode?>(_actionConfigurations));
        }
    }

    public string GetConfig()
    {
        var json = JsonSerializer.Serialize(GetConfigSnapshot(), JsonOptions);
        LogVerbose($"Config JSON: {json}");
        return json;
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

    public JsonNode? GetPluginConfiguration()
    {
        lock (_sync)
        {
            return _pluginConfiguration?.DeepClone();
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
            catch (Exception ex)
            {
                LogError(ex, "Local config web server accept loop failed.");
                break;
            }

            _ = HandleRequestAsync(context);
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
            LogVerbose($"{context.Request.HttpMethod} {path}");

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

            if (context.Request.HttpMethod == "GET" && path == "/plugin/config")
            {
                await WritePluginConfigurationAsync(context).ConfigureAwait(false);
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

            if (context.Request.HttpMethod == "POST" && path == "/plugin/config")
            {
                await StorePluginConfigurationAsync(context).ConfigureAwait(false);
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
        catch (Exception ex) when (IsClientDisconnect(ex))
        {
            LogVerbose($"Client disconnected while handling request: {ex.Message}");
        }
        catch (Exception ex)
        {
            LogError(ex, "Request failed.");
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

    private async Task StorePluginConfigurationAsync(HttpListenerContext context)
    {
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

        StorePluginConfiguration(config);
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

        if (configs.TryGetPropertyValue("plugin", out var pluginConfig))
        {
            StorePluginConfiguration(pluginConfig?.DeepClone());
        }

        var actionConfigs = configs.TryGetPropertyValue("actions", out var actionsNode)
            ? actionsNode as JsonObject
            : configs;

        if (actionConfigs is null)
        {
            await WriteResponseAsync(context.Response, 400, "text/plain; charset=utf-8", "'actions' must be a JSON object").ConfigureAwait(false);
            return;
        }

        foreach (var entry in actionConfigs)
        {
            if (entry.Key is "plugin" or "actions")
            {
                continue;
            }

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

    private async Task WritePluginConfigurationAsync(HttpListenerContext context)
    {
        await WriteResponseAsync(context.Response, 200, "application/json; charset=utf-8", GetPluginConfiguration()?.ToJsonString(JsonOptions) ?? "null").ConfigureAwait(false);
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
        LogVerbose($"Stored configuration for action {actionGuid}.");
        return true;
    }

    private void StorePluginConfiguration(JsonNode? config)
    {
        Action<JsonNode?>? callback;
        lock (_sync)
        {
            _pluginConfiguration = config?.DeepClone();
            _persistedPluginConfiguration = _pluginConfiguration?.DeepClone();
            callback = _pluginConfigurationUpdated;
        }

        callback?.Invoke(config?.DeepClone());
        PersistConfigurations();
        LogVerbose("Stored plugin configuration.");
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
            CancelAutoDeactivate();
            _sseClients.Add(client);
        }

        LogVerbose($"SSE client connected ({client.Id}).");

        try
        {
            await WriteServerEventAsync(client, "connected", new { connected = true }).ConfigureAwait(false);
            var cancellationToken = _cancellation?.Token ?? CancellationToken.None;
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_options.SseHeartbeatInterval, cancellationToken).ConfigureAwait(false);
                await WriteServerEventAsync(client, "heartbeat", new { utc = DateTimeOffset.UtcNow }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (IsClientDisconnect(ex))
        {
            LogVerbose($"SSE client disconnected ({client.Id}): {ex.Message}");
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
            LogVerbose($"SSE client closed ({client.Id}).");
            ScheduleAutoDeactivateIfNoBrowserClients();
        }
    }

    private void ScheduleAutoDeactivateIfNoBrowserClients()
    {
        if (!_options.AutoDeactivateWhenBrowserClosed)
        {
            return;
        }

        CancellationTokenSource autoDeactivateCancellation;
        lock (_sync)
        {
            if (_listener is null || _sseClients.Count > 0)
            {
                return;
            }

            CancelAutoDeactivate();
            _autoDeactivateCancellation = new CancellationTokenSource();
            autoDeactivateCancellation = _autoDeactivateCancellation;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_options.BrowserDisconnectGracePeriod, autoDeactivateCancellation.Token).ConfigureAwait(false);
                lock (_sync)
                {
                    if (_listener is null || _sseClients.Count > 0)
                    {
                        return;
                    }
                }

                LogInfo("No browser clients remain; stopping local config web server.", isLifecycle: true);
                DeactivateConfig();
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void CancelAutoDeactivate()
    {
        _autoDeactivateCancellation?.Cancel();
        _autoDeactivateCancellation?.Dispose();
        _autoDeactivateCancellation = null;
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

    private static bool IsClientDisconnect(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is FileNotFoundException or DirectoryNotFoundException)
            {
                return false;
            }

            if (current is IOException or ObjectDisposedException)
            {
                return true;
            }

            if (current is HttpListenerException httpListenerException)
            {
                return httpListenerException.ErrorCode is 32 or 64 or 995
                    || httpListenerException.Message.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase)
                    || httpListenerException.Message.Contains("transport connection", StringComparison.OrdinalIgnoreCase);
            }

            if (current is SocketException socketException)
            {
                return socketException.NativeErrorCode == 32
                    || socketException.SocketErrorCode is SocketError.ConnectionAborted
                    or SocketError.ConnectionReset
                    or SocketError.Shutdown;
            }
        }

        return false;
    }

    private string BuildIndexHtml()
    {
        var html = LoadAsset("index.html");
        var snapshot = GetConfigSnapshot();
        var title = snapshot.Plugin?.Title ?? "Loupedeck Configuration";
        var heading = snapshot.Plugin?.Heading ?? title;
        var pluginSettingsHtml = BuildPluginHtml(snapshot.Plugin);
        var actionsHtml = string.Join(Environment.NewLine, snapshot.Actions.Select(BuildActionHtml));
        var bootstrapJson = JsonSerializer.Serialize(snapshot, JsonOptions);
        LogVerbose($"Rendering config HTML with actions=[{string.Join(", ", snapshot.Actions.Select(static action => $"{action.Name}:{action.ActionGuid}:params={action.Parameters.Count}:options=[{DescribeParameterOptions(action)}]"))}]");

        return html
            .Replace("{{title}}", WebUtility.HtmlEncode(title), StringComparison.Ordinal)
            .Replace("{{heading}}", WebUtility.HtmlEncode(heading), StringComparison.Ordinal)
            .Replace("{{pluginSettings}}", pluginSettingsHtml, StringComparison.Ordinal)
            .Replace("{{actions}}", actionsHtml, StringComparison.Ordinal)
            .Replace("{{configJson}}", bootstrapJson, StringComparison.Ordinal);
    }

    private static string BuildPluginHtml(LoupedeckPluginRegistration? plugin)
    {
        if (plugin is null || string.IsNullOrWhiteSpace(plugin.HtmlSnippet))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"""<section class="plugin-settings" data-plugin-config="{WebUtility.HtmlEncode(GetPluginConfigurationKey(plugin))}">""");
        builder.AppendLine("""  <h2>Plugin Settings</h2>""");
        builder.AppendLine("""  <div class="snippet">""");
        builder.AppendLine(RenderPluginSnippet(plugin));
        builder.AppendLine("""  </div>""");
        builder.AppendLine("</section>");
        return builder.ToString();
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

    private static string RenderPluginSnippet(LoupedeckPluginRegistration plugin)
    {
        return (plugin.HtmlSnippet ?? string.Empty)
            .Replace("{{pluginId}}", WebUtility.HtmlEncode(plugin.PluginId), StringComparison.Ordinal)
            .Replace("{{configurationKey}}", WebUtility.HtmlEncode(GetPluginConfigurationKey(plugin)), StringComparison.Ordinal);
    }

    private static string GetConfigurationKey(LoupedeckActionRegistration registration)
    {
        return string.IsNullOrWhiteSpace(registration.ConfigurationKey)
            ? registration.ActionGuid.ToString()
            : registration.ConfigurationKey;
    }

    private static string GetPluginConfigurationKey(LoupedeckPluginRegistration registration)
    {
        return string.IsNullOrWhiteSpace(registration.ConfigurationKey)
            ? registration.PluginId
            : registration.ConfigurationKey;
    }

    private static string DescribeParameterOptions(LoupedeckActionRegistration registration)
    {
        return string.Join(", ", registration.Parameters.Select(static parameter =>
            $"{parameter.Name}:{(parameter.Options is null ? 0 : parameter.Options.Count)}"));
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

            _persistedPluginConfiguration = persisted.PluginConfiguration?.DeepClone();
            _pluginConfiguration = _persistedPluginConfiguration?.DeepClone();

            LogVerbose($"Loaded {_persistedActionConfigurations.Count} persisted action configuration(s) and {(persisted.PluginConfiguration is null ? 0 : 1)} plugin configuration(s).");
        }
        catch (JsonException ex)
        {
            LogWarning(ex, "Could not load persisted configuration.");
        }
    }

    private void PersistConfigurations()
    {
        var store = _options.ConfigStore;
        if (store is null)
        {
            return;
        }

        Dictionary<string, JsonNode?> actionSnapshot;
        JsonNode? pluginSnapshot;
        lock (_sync)
        {
            actionSnapshot = _persistedActionConfigurations.ToDictionary(
                static entry => entry.Key,
                static entry => entry.Value?.DeepClone(),
                StringComparer.OrdinalIgnoreCase);
            pluginSnapshot = _persistedPluginConfiguration?.DeepClone();
        }

        var persisted = new PersistedLoupedeckConfig(1, actionSnapshot, pluginSnapshot);
        store.Save(JsonSerializer.Serialize(persisted, JsonOptions));
        LogVerbose($"Persisted {actionSnapshot.Count} action configuration(s) and {(pluginSnapshot is null ? 0 : 1)} plugin configuration(s).");
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
        var assembly = typeof(LoupedeckWebConfigService).Assembly;
        var assemblyDirectory = Path.GetDirectoryName(assembly.Location);
        var searchDirectories = new[]
        {
            assemblyDirectory,
            AppContext.BaseDirectory
        }
            .Where(static directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var searchedPaths = new List<string>();
        foreach (var directory in searchDirectories)
        {
            var path = Path.Combine(directory!, "wwwroot", fileName);
            searchedPaths.Add(path);
            if (File.Exists(path))
            {
                return File.ReadAllText(path, Encoding.UTF8);
            }
        }

        var resourceName = $"wwwroot/{fileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is not null)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        throw new FileNotFoundException(
            $"Required web asset '{fileName}' was not found. Searched files: {string.Join(", ", searchedPaths)}. Searched resource: {resourceName}.",
            searchedPaths.FirstOrDefault());
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
            LogVerbose($"Opened default browser for {uri}.");
        }
        catch (Exception ex)
        {
            LogWarning(ex, "Could not open default browser.");
        }
    }

    private void LogVerbose(string message) => Log(LoupedeckWebConfigLogLevel.Verbose, message);

    private void LogInfo(string message, bool isLifecycle = false) => Log(LoupedeckWebConfigLogLevel.Info, message, isLifecycle);

    private void LogWarning(Exception exception, string message) => Log(LoupedeckWebConfigLogLevel.Warning, message, exception);

    private void LogError(Exception exception, string message) => Log(LoupedeckWebConfigLogLevel.Error, message, exception);

    private void Log(LoupedeckWebConfigLogLevel level, string message, Exception? exception = null)
    {
        Log(level, message, isLifecycle: false, exception);
    }

    private void Log(LoupedeckWebConfigLogLevel level, string message, bool isLifecycle, Exception? exception = null)
    {
        if (level < MinimumLogLevel && !(isLifecycle && _options.LogLifecycleMessages))
        {
            return;
        }

        var entry = new LoupedeckWebConfigLogEntry(level, message, exception);
        (_options.Log ?? LoupedeckWebConfigLog.WriteToConsole)(entry);
    }

    private sealed record SseClient(Guid Id, HttpListenerResponse Response, StreamWriter Writer, SemaphoreSlim WriteLock);
}
