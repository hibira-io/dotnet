using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hibira.Client;

/// <summary>One Pusher Channels protocol (v7) WebSocket connection.</summary>
internal sealed class PusherSocket : IAsyncDisposable
{
    public const string Version = "0.1.0";

    private readonly Uri _url;
    private readonly Func<ClientWebSocket> _factory;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _subscriptions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private ClientWebSocket? _ws;
    private TaskCompletionSource<string>? _established;
    private TaskCompletionSource? _signin;
    private Task? _loop;
    private int _closed;
    private DateTime _lastActivity = DateTime.UtcNow;
    private DateTime? _pingSent;
    private TimeSpan _activityTimeout = TimeSpan.FromSeconds(120);

    public PusherSocket(Uri url, Func<ClientWebSocket>? factory = null)
    {
        _url = url;
        _factory = factory ?? (() => new ClientWebSocket());
    }

    public static Uri UrlFor(string host, int port, bool tls, string key)
    {
        var scheme = tls ? "wss" : "ws";
        var authority = port == (tls ? 443 : 80) ? host : host + ":" + port;
        return new Uri($"{scheme}://{authority}/app/{Uri.EscapeDataString(key)}?protocol=7&client=hibira-dotnet&version={Version}&flash=false");
    }

    public string? SocketId { get; private set; }

    /// <summary>An event arrived: channel (null for connection events), event name, data as a JSON string or raw JSON.</summary>
    public event Action<string?, string, JsonNode?>? Message;

    /// <summary>The socket closed; the exception is null after <see cref="DisposeAsync"/>.</summary>
    public event Action<Exception?>? Closed;

    internal TimeSpan PongTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public async Task<string> ConnectAsync(CancellationToken ct)
    {
        _ws = _factory();
        _established = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _ws.ConnectAsync(_url, ct).ConfigureAwait(false);
        _loop = Task.Run(ReceiveLoopAsync);
        _ = Task.Run(ActivityLoopAsync);
        using var reg = ct.Register(() => _established.TrySetCanceled(ct));
        SocketId = await _established.Task.ConfigureAwait(false);
        return SocketId;
    }

    public async Task SubscribeAsync(string channel, JsonObject? auth, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _subscriptions[channel] = tcs;
        var data = new JsonObject { ["channel"] = channel };
        if (auth != null)
            foreach (var (k, v) in auth)
                data[k] = v?.DeepClone();
        await SendAsync("pusher:subscribe", data, null, ct).ConfigureAwait(false);
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        await tcs.Task.ConfigureAwait(false);
    }

    public Task UnsubscribeAsync(string channel, CancellationToken ct)
    {
        _subscriptions.TryRemove(channel, out _);
        return SendAsync("pusher:unsubscribe", new JsonObject { ["channel"] = channel }, null, ct);
    }

    public async Task SigninAsync(JsonObject auth, CancellationToken ct)
    {
        _signin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await SendAsync("pusher:signin", auth.DeepClone(), null, ct).ConfigureAwait(false);
        using var reg = ct.Register(() => _signin.TrySetCanceled(ct));
        await _signin.Task.ConfigureAwait(false);
    }

    public async Task SendAsync(string ev, JsonNode? data, string? channel, CancellationToken ct)
    {
        var msg = new JsonObject { ["event"] = ev, ["data"] = data };
        if (channel != null) msg["channel"] = channel;
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        var ws = _ws ?? throw new InvalidOperationException("not connected");
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var message = new MemoryStream();
        Exception? error = null;
        try
        {
            while (!_cts.IsCancellationRequested && _ws!.State == WebSocketState.Open)
            {
                var r = await _ws.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    error = new WebSocketException($"closed by the server: {(int?)r.CloseStatus ?? 0} {r.CloseStatusDescription}");
                    break;
                }
                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                _lastActivity = DateTime.UtcNow;
                _pingSent = null;
                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                Handle(text);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            error = e;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        Close(error ?? (_cts.IsCancellationRequested ? null : new WebSocketException("the connection closed")));
    }

    private void Handle(string text)
    {
        JsonObject? msg;
        try { msg = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { return; }
        if (msg == null) return;
        var ev = msg["event"]?.GetValue<string>() ?? "";
        var channel = msg["channel"]?.GetValue<string>();
        var data = msg["data"];
        switch (ev)
        {
            case "pusher:connection_established":
            {
                var d = ParseData(data);
                var id = d?["socket_id"]?.GetValue<string>();
                if (d?["activity_timeout"] is JsonValue t && t.TryGetValue<int>(out var secs) && secs > 0)
                    _activityTimeout = TimeSpan.FromSeconds(Math.Min(120, secs));
                if (id != null) _established?.TrySetResult(id);
                return;
            }
            case "pusher:error":
            {
                var d = ParseData(data);
                var err = new HibiraClientException($"pusher:error {d?["code"]}: {d?["message"]}");
                if (_established is { Task.IsCompleted: false }) _established.TrySetException(err);
                return;
            }
            case "pusher:ping":
                _ = SendAsync("pusher:pong", new JsonObject(), null, CancellationToken.None);
                return;
            case "pusher:pong":
                return;
            case "pusher_internal:subscription_succeeded":
                if (channel != null && _subscriptions.TryGetValue(channel, out var ok)) ok.TrySetResult();
                break;
            case "pusher:subscription_error":
                if (channel != null && _subscriptions.TryRemove(channel, out var bad))
                    bad.TrySetException(new HibiraClientException($"could not subscribe to {channel}: {data?.ToJsonString()}"));
                return;
            case "pusher:signin_success":
                _signin?.TrySetResult();
                return;
        }
        if (ev.StartsWith("pusher_internal:", StringComparison.Ordinal)) return;
        Message?.Invoke(channel, ev, data);
    }

    internal static JsonNode? ParseData(JsonNode? data)
    {
        if (data is JsonValue v && v.TryGetValue<string>(out var s))
        {
            try { return JsonNode.Parse(s); } catch (JsonException) { return data; }
        }
        return data;
    }

    private async Task ActivityLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                if (_pingSent is { } sent)
                {
                    if (now - sent > PongTimeout) { Close(new TimeoutException("no pong from the server")); return; }
                }
                else if (now - _lastActivity > _activityTimeout && SocketId != null)
                {
                    _pingSent = now;
                    await SendAsync("pusher:ping", new JsonObject(), null, _cts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested) { }
        catch (Exception e) { Close(e); }
    }

    private void Close(Exception? error)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        _cts.Cancel();
        var ex = error ?? new OperationCanceledException("the connection was closed");
        _established?.TrySetException(ex);
        _signin?.TrySetException(ex);
        foreach (var s in _subscriptions.Values) s.TrySetException(ex);
        try { _ws?.Abort(); } catch { }
        Closed?.Invoke(error);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws is { State: WebSocketState.Open } ws && Volatile.Read(ref _closed) == 0)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client disconnect", timeout.Token).ConfigureAwait(false);
            }
            catch { }
        }
        Close(null);
        if (_loop != null) await Task.WhenAny(_loop, Task.Delay(2000)).ConfigureAwait(false);
        _ws?.Dispose();
        _cts.Dispose();
    }
}

/// <summary>The hub connection failed.</summary>
public sealed class HibiraClientException : Exception
{
    /// <summary>Creates the exception.</summary>
    public HibiraClientException(string message) : base(message) { }
}
