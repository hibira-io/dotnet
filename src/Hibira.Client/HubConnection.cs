using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Hibira.Client;

/// <summary>The state of a <see cref="HubConnection"/>.</summary>
public enum HubConnectionState
{
    /// <summary>Not connected.</summary>
    Disconnected,
    /// <summary>Connected; methods can be invoked.</summary>
    Connected,
    /// <summary>Starting.</summary>
    Connecting,
    /// <summary>The connection dropped and is coming back.</summary>
    Reconnecting,
}

/// <summary>Options for <see cref="HubConnectionBuilder.WithUrl(Uri, Action{HubConnectionOptions})"/>.</summary>
public sealed class HubConnectionOptions
{
    /// <summary>Returns a bearer token sent with every request to the hub.</summary>
    public Func<Task<string?>>? AccessTokenProvider { get; set; }

    /// <summary>Headers sent with every request to the hub.</summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>();

    /// <summary>The HttpClient for hub requests; one is created when null.</summary>
    public HttpClient? HttpClient { get; set; }

    /// <summary>Creates the WebSocket to Hibira; a plain ClientWebSocket when null.</summary>
    public Func<ClientWebSocket>? WebSocketFactory { get; set; }

    /// <summary>How hub arguments and return values are read and written (web defaults).</summary>
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);

    /// <summary>How long to wait for the hub to answer during start.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>Builds a <see cref="HubConnection"/>, like SignalR's HubConnectionBuilder.</summary>
public sealed class HubConnectionBuilder
{
    private Uri? _url;
    private readonly HubConnectionOptions _options = new();
    private TimeSpan[]? _reconnectDelays;

    /// <summary>The hub URL, as mapped with MapHibiraHub (absolute).</summary>
    public HubConnectionBuilder WithUrl(string url, Action<HubConnectionOptions>? configure = null) => WithUrl(new Uri(url), configure);

    /// <summary>The hub URL, as mapped with MapHibiraHub (absolute).</summary>
    public HubConnectionBuilder WithUrl(Uri url, Action<HubConnectionOptions>? configure = null)
    {
        if (!url.IsAbsoluteUri) throw new ArgumentException("the hub URL must be absolute (in Blazor use NavigationManager.ToAbsoluteUri)", nameof(url));
        _url = url;
        configure?.Invoke(_options);
        return this;
    }

    /// <summary>Reconnects after 0, 2, 10 and 30 seconds, then every 30 seconds.</summary>
    public HubConnectionBuilder WithAutomaticReconnect() =>
        WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) });

    /// <summary>Reconnects after these delays; the last one repeats.</summary>
    public HubConnectionBuilder WithAutomaticReconnect(TimeSpan[] delays)
    {
        if (delays.Length == 0) throw new ArgumentException("at least one delay", nameof(delays));
        _reconnectDelays = delays;
        return this;
    }

    /// <summary>Builds the connection.</summary>
    public HubConnection Build() =>
        new(_url ?? throw new InvalidOperationException("call WithUrl before Build"), _options, _reconnectDelays);
}

/// <summary>
/// A connection to a Hibira.AspNetCore hub. The API mirrors the SignalR .NET
/// client: register handlers with On, then StartAsync, InvokeAsync and SendAsync.
/// </summary>
public sealed class HubConnection : IAsyncDisposable
{
    private sealed record Handler(Type[] Types, Func<object?[], Task> Callback);
    private sealed record Negotiation(string Hub, string Key, string Host, int Port, bool UseTls, string Channel, string ConnectionChannelPrefix, string? UserId);

    private readonly Uri _base;
    private readonly HubConnectionOptions _options;
    private readonly TimeSpan[]? _reconnectDelays;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Dictionary<string, List<Handler>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Channel<(string Event, JsonNode? Data)> _inbox = System.Threading.Channels.Channel.CreateUnbounded<(string, JsonNode?)>();
    private readonly HashSet<string> _groups = new(StringComparer.Ordinal);
    private PusherSocket? _socket;
    private Negotiation? _info;
    private string? _token;
    private TaskCompletionSource<string>? _handshake;
    private CancellationTokenSource? _run;
    private bool _stopping;

    internal HubConnection(Uri url, HubConnectionOptions options, TimeSpan[]? reconnectDelays)
    {
        _base = new Uri(url.ToString().TrimEnd('/') + "/");
        _options = options;
        _reconnectDelays = reconnectDelays;
        _ownsHttp = options.HttpClient == null;
        _http = options.HttpClient ?? new HttpClient();
    }

    /// <summary>The state.</summary>
    public HubConnectionState State { get; private set; } = HubConnectionState.Disconnected;

    /// <summary>The connection ID (Hibira socket ID) while connected.</summary>
    public string? ConnectionId => State == HubConnectionState.Connected ? _socket?.SocketId : null;

    /// <summary>The connection closed; the exception is null after StopAsync.</summary>
    public event Func<Exception?, Task>? Closed;

    /// <summary>The connection dropped and is reconnecting.</summary>
    public event Func<Exception?, Task>? Reconnecting;

    /// <summary>The connection is back, with a new connection ID. Groups must be joined again.</summary>
    public event Func<string?, Task>? Reconnected;

    /// <summary>
    /// Registers a handler for <paramref name="methodName"/>: <paramref name="parameterTypes"/>
    /// say how to read the arguments. Dispose the result to remove it.
    /// </summary>
    public IDisposable On(string methodName, Type[] parameterTypes, Func<object?[], Task> handler)
    {
        var h = new Handler(parameterTypes, handler);
        lock (_handlers)
        {
            if (!_handlers.TryGetValue(methodName, out var list)) _handlers[methodName] = list = new List<Handler>();
            list.Add(h);
        }
        return new Subscription(() => { lock (_handlers) if (_handlers.TryGetValue(methodName, out var l)) l.Remove(h); });
    }

    /// <summary>Removes every handler of <paramref name="methodName"/>.</summary>
    public void Remove(string methodName)
    {
        lock (_handlers) _handlers.Remove(methodName);
    }

    /// <summary>Connects to the hub.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != HubConnectionState.Disconnected)
                throw new InvalidOperationException("The HubConnection cannot be started if it is not in the Disconnected state.");
            State = HubConnectionState.Connecting;
            _stopping = false;
            _run = new CancellationTokenSource();
            _inbox = System.Threading.Channels.Channel.CreateUnbounded<(string, JsonNode?)>();
            _ = Task.Run(() => DispatchLoopAsync(_inbox.Reader));
            try
            {
                await ConnectAsync(cancellationToken).ConfigureAwait(false);
                State = HubConnectionState.Connected;
            }
            catch
            {
                await TeardownAsync().ConfigureAwait(false);
                State = HubConnectionState.Disconnected;
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Disconnects from the hub.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == HubConnectionState.Disconnected) return;
            _stopping = true;
            if (_token != null && _socket?.SocketId is { } id)
            {
                try { await PostAsync("disconnect", new JsonObject { ["connectionId"] = id, ["token"] = _token }, cancellationToken).ConfigureAwait(false); }
                catch (Exception) { }
            }
            await TeardownAsync().ConfigureAwait(false);
            State = HubConnectionState.Disconnected;
        }
        finally
        {
            _lifecycle.Release();
        }
        await RaiseAsync(Closed, null).ConfigureAwait(false);
    }

    /// <summary>Calls a hub method and returns its result.</summary>
    public async Task<object?> InvokeCoreAsync(string methodName, Type returnType, object?[] args, CancellationToken cancellationToken = default)
    {
        if (State != HubConnectionState.Connected || _socket?.SocketId is not { } id || _token == null)
            throw new InvalidOperationException($"The '{nameof(InvokeCoreAsync)}' method cannot be called if the connection is not active");
        var body = new JsonObject
        {
            ["connectionId"] = id,
            ["token"] = _token,
            ["target"] = methodName,
            ["arguments"] = JsonSerializer.SerializeToNode(args, _options.JsonSerializerOptions),
        };
        var res = await PostAsync("invoke", body, cancellationToken).ConfigureAwait(false);
        if (res?["error"] is { } error) throw new HibiraClientException(error.ToString());
        if (returnType == typeof(void) || res?["result"] is not { } result) return null;
        return result.Deserialize(returnType, _options.JsonSerializerOptions);
    }

    /// <summary>Calls a hub method without reading its result.</summary>
    public Task SendCoreAsync(string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        InvokeCoreAsync(methodName, typeof(void), args, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (_ownsHttp) _http.Dispose();
        _lifecycle.Dispose();
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.HandshakeTimeout);
        ct = timeout.Token;
        var info = _info = await GetAsync<Negotiation>("negotiate", ct).ConfigureAwait(false)
            ?? throw new HibiraClientException("the hub did not answer negotiate");
        var socket = new PusherSocket(PusherSocket.UrlFor(info.Host, info.Port, info.UseTls, info.Key), _options.WebSocketFactory);
        socket.Message += OnMessage;
        socket.Closed += e => _ = OnSocketClosedAsync(socket, e);
        _socket = socket;
        var socketId = await socket.ConnectAsync(ct).ConfigureAwait(false);

        await socket.SubscribeAsync(info.Channel, await AuthAsync(socketId, info.Channel, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(info.UserId))
        {
            var userAuth = await PostFormAsync("user-auth", new Dictionary<string, string> { ["socket_id"] = socketId }, ct).ConfigureAwait(false);
            if (userAuth != null)
            {
                await socket.SigninAsync(userAuth, ct).ConfigureAwait(false);
                var id = JsonNode.Parse(userAuth["user_data"]?.GetValue<string>() ?? "{}")?["id"]?.GetValue<string>();
                if (id != null) await socket.SubscribeAsync("#server-to-user-" + id, null, ct).ConfigureAwait(false);
            }
        }

        var connChannel = info.ConnectionChannelPrefix + socketId;
        await socket.SubscribeAsync(connChannel, await AuthAsync(socketId, connChannel, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        _handshake = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await PostAsync("handshake", new JsonObject { ["connectionId"] = socketId }, ct).ConfigureAwait(false);
        using (ct.Register(() => _handshake.TrySetCanceled(ct)))
            _token = await _handshake.Task.ConfigureAwait(false);
        var res = await PostAsync("connect", new JsonObject { ["connectionId"] = socketId, ["token"] = _token }, ct).ConfigureAwait(false);
        if (res?["error"] is { } error) throw new HibiraClientException(error.ToString());
    }

    private void OnMessage(string? channel, string ev, JsonNode? raw)
    {
        if (ev.StartsWith("pusher", StringComparison.Ordinal)) return;
        var data = PusherSocket.ParseData(raw);
        if (ev == "hibira:handshake")
        {
            if (data?["token"]?.GetValue<string>() is { } token) _handshake?.TrySetResult(token);
            return;
        }
        _inbox.Writer.TryWrite((ev, data));
    }

    private async Task DispatchLoopAsync(ChannelReader<(string Event, JsonNode? Data)> reader)
    {
        await foreach (var (ev, data) in reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (ev == "hibira:join" || ev == "hibira:leave")
                {
                    await GroupAsync(ev == "hibira:join", data?["channel"]?.GetValue<string>()).ConfigureAwait(false);
                    continue;
                }
                if (data?["hub"]?.GetValue<string>() != _info?.Hub) continue;
                List<Handler> handlers;
                lock (_handlers)
                {
                    if (!_handlers.TryGetValue(ev, out var list)) continue;
                    handlers = list.ToList();
                }
                var args = data?["arguments"] as JsonArray ?? new JsonArray();
                foreach (var h in handlers)
                {
                    var values = new object?[h.Types.Length];
                    for (var i = 0; i < values.Length; i++)
                        values[i] = i < args.Count ? args[i]?.Deserialize(h.Types[i], _options.JsonSerializerOptions) : null;
                    await h.Callback(values).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // A failing handler must not stop the others.
            }
        }
    }

    private async Task GroupAsync(bool join, string? channel)
    {
        var socket = _socket;
        if (channel == null || socket?.SocketId is not { } id) return;
        if (join)
        {
            lock (_groups) if (!_groups.Add(channel)) return;
            await socket.SubscribeAsync(channel, await AuthAsync(id, channel, CancellationToken.None).ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            lock (_groups) _groups.Remove(channel);
            await socket.UnsubscribeAsync(channel, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task OnSocketClosedAsync(PusherSocket socket, Exception? error)
    {
        if (_stopping || socket != _socket || error == null) return;
        if (_reconnectDelays == null)
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (socket != _socket) return;
                await TeardownAsync().ConfigureAwait(false);
                State = HubConnectionState.Disconnected;
            }
            finally
            {
                _lifecycle.Release();
            }
            await RaiseAsync(Closed, error).ConfigureAwait(false);
            return;
        }

        State = HubConnectionState.Reconnecting;
        await RaiseAsync(Reconnecting, error).ConfigureAwait(false);
        var run = _run;
        for (var attempt = 0; run is { IsCancellationRequested: false }; attempt++)
        {
            try
            {
                await Task.Delay(_reconnectDelays[Math.Min(attempt, _reconnectDelays.Length - 1)], run.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_stopping || run.IsCancellationRequested) return;
                lock (_groups) _groups.Clear();
                _token = null;
                var old = _socket;
                _socket = null;
                if (old != null) await old.DisposeAsync().ConfigureAwait(false);
                await ConnectAsync(run.Token).ConfigureAwait(false);
                State = HubConnectionState.Connected;
            }
            catch (Exception)
            {
                continue;
            }
            finally
            {
                _lifecycle.Release();
            }
            await RaiseAsync(Reconnected, _socket?.SocketId).ConfigureAwait(false);
            return;
        }
    }

    private async Task TeardownAsync()
    {
        _run?.Cancel();
        _inbox.Writer.TryComplete();
        var socket = _socket;
        _socket = null;
        _token = null;
        lock (_groups) _groups.Clear();
        if (socket != null) await socket.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task RaiseAsync<T>(Func<T, Task>? handler, T arg)
    {
        if (handler == null) return;
        foreach (var d in handler.GetInvocationList().Cast<Func<T, Task>>())
        {
            try { await d(arg).ConfigureAwait(false); } catch (Exception) { }
        }
    }

    private async Task<JsonObject?> AuthAsync(string socketId, string channel, CancellationToken ct) =>
        await PostFormAsync("auth", new Dictionary<string, string> { ["socket_id"] = socketId, ["channel_name"] = channel }, ct).ConfigureAwait(false)
        ?? throw new HibiraClientException($"the hub did not sign {channel}");

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, new Uri(_base, path));
        foreach (var (k, v) in _options.Headers) req.Headers.TryAddWithoutValidation(k, v);
        if (_options.AccessTokenProvider != null && await _options.AccessTokenProvider().ConfigureAwait(false) is { Length: > 0 } token)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        using var req = await RequestAsync(HttpMethod.Get, path).ConfigureAwait(false);
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureAsync(res, path, ct).ConfigureAwait(false);
        return await res.Content.ReadFromJsonAsync<T>(_options.JsonSerializerOptions, ct).ConfigureAwait(false);
    }

    private async Task<JsonObject?> PostAsync(string path, JsonObject body, CancellationToken ct)
    {
        using var req = await RequestAsync(HttpMethod.Post, path).ConfigureAwait(false);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureAsync(res, path, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject;
    }

    private async Task<JsonObject?> PostFormAsync(string path, Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = await RequestAsync(HttpMethod.Post, path).ConfigureAwait(false);
        req.Content = new FormUrlEncodedContent(form);
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureAsync(res, path, ct).ConfigureAwait(false);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject;
    }

    private static async Task EnsureAsync(HttpResponseMessage res, string path, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string? message = null;
        try { message = JsonNode.Parse(text)?["error"]?.ToString(); } catch (JsonException) { }
        throw new HibiraClientException($"{path} answered {(int)res.StatusCode}{(message != null ? ": " + message : "")}");
    }

    private sealed class Subscription : IDisposable
    {
        private Action? _dispose;
        public Subscription(Action dispose) => _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
