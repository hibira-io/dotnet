using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hibira;

/// <summary>The default <see cref="IHibiraClient"/>, over <see cref="HttpClient"/>.</summary>
public sealed class HibiraClient : IHibiraClient
{
    /// <summary>The library version, sent in the User-Agent.</summary>
    public const string Version = "0.1.0";

    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;

    /// <summary>Creates a client with its own <see cref="HttpClient"/>.</summary>
    public HibiraClient(HibiraOptions options) : this(options, new HttpClient()) { }

    /// <summary>Creates a client over <paramref name="http"/> (for example from IHttpClientFactory).</summary>
    public HibiraClient(HibiraOptions options, HttpClient http, JsonSerializerOptions? json = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(http);
        options.Validate();
        Options = options;
        _http = http;
        _json = json ?? HibiraJson.Options;
        if (_http.Timeout == TimeSpan.FromSeconds(100)) _http.Timeout = options.Timeout;
    }

    /// <inheritdoc />
    public HibiraOptions Options { get; }

    /// <summary>Clock used for request timestamps. Tests replace it.</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public Task TriggerAsync(string channel, string eventName, object? data, string? socketId = null, CancellationToken cancellationToken = default) =>
        TriggerAsync(new[] { channel }, eventName, data, socketId, cancellationToken);

    /// <inheritdoc />
    public async Task TriggerAsync(IEnumerable<string> channels, string eventName, object? data, string? socketId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        var list = channels.Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0) return;
        foreach (var chunk in list.Chunk(100))
        {
            var body = new JsonObject
            {
                ["name"] = eventName,
                ["data"] = DataString(data),
                ["channels"] = new JsonArray(chunk.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
            };
            if (!string.IsNullOrEmpty(socketId)) body["socket_id"] = socketId;
            await SendAsync(HttpMethod.Post, "events", null, body.ToJsonString(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task TriggerBatchAsync(IEnumerable<HibiraBatchEvent> events, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var chunk in events.Chunk(10))
        {
            var batch = new JsonArray();
            foreach (var e in chunk)
            {
                var o = new JsonObject { ["channel"] = e.Channel, ["name"] = e.Name, ["data"] = DataString(e.Data) };
                if (!string.IsNullOrEmpty(e.SocketId)) o["socket_id"] = e.SocketId;
                batch.Add(o);
            }
            await SendAsync(HttpMethod.Post, "batch_events", null, new JsonObject { ["batch"] = batch }.ToJsonString(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task SendToUserAsync(string userId, string eventName, object? data, CancellationToken cancellationToken = default) =>
        TriggerAsync(HibiraChannelName.ForUser(userId), eventName, data, null, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, HibiraChannelInfo>> GetChannelsAsync(string? prefix = null, string? info = null, CancellationToken cancellationToken = default)
    {
        var q = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrEmpty(prefix)) q.Add(new("filter_by_prefix", prefix));
        if (!string.IsNullOrEmpty(info)) q.Add(new("info", info));
        var text = await SendAsync(HttpMethod.Get, "channels", q, null, cancellationToken).ConfigureAwait(false);
        var doc = JsonNode.Parse(text)?["channels"]?.AsObject();
        var result = new Dictionary<string, HibiraChannelInfo>(StringComparer.Ordinal);
        if (doc != null)
            foreach (var (name, node) in doc)
                result[name] = node?.Deserialize<HibiraChannelInfo>(_json) ?? new HibiraChannelInfo();
        foreach (var v in result.Values) v.Occupied = true;
        return result;
    }

    /// <inheritdoc />
    public async Task<HibiraChannelInfo> GetChannelAsync(string channel, string? info = null, CancellationToken cancellationToken = default)
    {
        var q = string.IsNullOrEmpty(info) ? null : new List<KeyValuePair<string, string>> { new("info", info) };
        var text = await SendAsync(HttpMethod.Get, "channels/" + channel, q, null, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<HibiraChannelInfo>(text, _json) ?? new HibiraChannelInfo();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetUsersAsync(string presenceChannel, CancellationToken cancellationToken = default)
    {
        var text = await SendAsync(HttpMethod.Get, "channels/" + presenceChannel + "/users", null, null, cancellationToken).ConfigureAwait(false);
        var users = JsonNode.Parse(text)?["users"]?.AsArray();
        return users?.Select(u => u?["id"]?.ToString() ?? "").Where(id => id.Length > 0).ToList() ?? new List<string>();
    }

    /// <inheritdoc />
    public Task TerminateUserConnectionsAsync(string userId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "users/" + HibiraChannelName.Escape(userId) + "/terminate_connections", null, "{}", cancellationToken);

    /// <inheritdoc />
    public async Task<HibiraApproval> RequestApprovalAsync(HibiraApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.Title);
        var text = await SendAsync(HttpMethod.Post, "approvals", null, JsonSerializer.Serialize(request, _json), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<HibiraApproval>(text, _json)!;
    }

    /// <inheritdoc />
    public async Task<HibiraApproval> GetApprovalAsync(string approvalId, TimeSpan? wait = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(approvalId);
        List<KeyValuePair<string, string>>? q = null;
        if (wait is { } w && w > TimeSpan.Zero)
            q = new() { new("wait", ((int)Math.Min(25, Math.Ceiling(w.TotalSeconds))).ToString(CultureInfo.InvariantCulture)) };
        var text = await SendAsync(HttpMethod.Get, "approvals/" + Uri.EscapeDataString(approvalId), q, null, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<HibiraApproval>(text, _json)!;
    }

    /// <inheritdoc />
    public HibiraChannelAuth AuthorizeChannel(string socketId, string channel)
    {
        CheckSocketId(socketId);
        ArgumentException.ThrowIfNullOrEmpty(channel);
        return new HibiraChannelAuth { Auth = Options.Key + ":" + HibiraSignature.Hmac(Options.Secret, socketId + ":" + channel) };
    }

    /// <inheritdoc />
    public HibiraChannelAuth AuthorizePresenceChannel(string socketId, string channel, string userId, object? userInfo = null)
    {
        CheckSocketId(socketId);
        ArgumentException.ThrowIfNullOrEmpty(userId);
        var data = new JsonObject { ["user_id"] = userId };
        if (userInfo != null) data["user_info"] = JsonSerializer.SerializeToNode(userInfo, _json);
        var channelData = data.ToJsonString();
        return new HibiraChannelAuth
        {
            Auth = Options.Key + ":" + HibiraSignature.Hmac(Options.Secret, socketId + ":" + channel + ":" + channelData),
            ChannelData = channelData,
        };
    }

    /// <inheritdoc />
    public HibiraUserAuth AuthenticateUser(string socketId, string userId, IReadOnlyDictionary<string, object?>? userData = null)
    {
        CheckSocketId(socketId);
        ArgumentException.ThrowIfNullOrEmpty(userId);
        var data = new JsonObject();
        if (userData != null)
            foreach (var (k, v) in userData)
                data[k] = JsonSerializer.SerializeToNode(v, _json);
        data["id"] = HibiraChannelName.Escape(userId);
        var userDataJson = data.ToJsonString();
        return new HibiraUserAuth
        {
            Auth = Options.Key + ":" + HibiraSignature.Hmac(Options.Secret, socketId + "::user::" + userDataJson),
            UserData = userDataJson,
        };
    }

    /// <inheritdoc />
    public HibiraWebhook? VerifyWebhook(string? key, string? signature, string? previousSignature, ReadOnlySpan<byte> body)
    {
        if (key != Options.Key) return null;
        var expected = HibiraSignature.Hmac(Options.Secret, body);
        if (!HibiraSignature.Equal(expected, signature) && !HibiraSignature.Equal(expected, previousSignature)) return null;
        try
        {
            return JsonSerializer.Deserialize<HibiraWebhook>(body, _json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string DataString(object? data) => data switch
    {
        null => "null",
        string s => s,
        JsonNode n => n.ToJsonString(),
        _ => JsonSerializer.Serialize(data, data.GetType(), _json),
    };

    private static void CheckSocketId(string socketId)
    {
        ArgumentException.ThrowIfNullOrEmpty(socketId);
        var dot = socketId.IndexOf('.');
        if (socketId.Length > 64 || dot <= 0 || dot == socketId.Length - 1 || !socketId.Where((c, i) => i != dot).All(char.IsAsciiDigit))
            throw new ArgumentException($"not a socket ID: {socketId}", nameof(socketId));
    }

    private async Task<string> SendAsync(HttpMethod method, string resource, IEnumerable<KeyValuePair<string, string>>? query, string? body, CancellationToken ct)
    {
        var path = "/apps/" + Options.AppId + "/" + resource;
        var bytes = body == null ? null : Encoding.UTF8.GetBytes(body);
        var qs = HibiraSignature.SignedQuery(Options.Key, Options.Secret, method.Method, path, query, bytes, Clock());
        using var req = new HttpRequestMessage(method, new Uri(Options.BaseUri, path + "?" + qs));
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("hibira-dotnet", Version));
        if (bytes != null)
        {
            req.Content = new ByteArrayContent(bytes);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            var message = text;
            try { message = JsonNode.Parse(text)?["error"]?.ToString() ?? text; } catch (JsonException) { }
            throw new HibiraException(res.StatusCode, string.IsNullOrWhiteSpace(message) ? res.ReasonPhrase ?? "" : message.Trim(), text);
        }
        return text;
    }
}
