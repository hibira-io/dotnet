using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Hibira.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hibira.Tests;

public sealed record Triggered(IReadOnlyList<string> Channels, string Event, JsonNode? Data, string? SocketId);

public sealed class FakeHibiraClient : IHibiraClient
{
    private readonly HibiraClient _inner = new(new HibiraOptions { AppId = "1", Key = "key", Secret = "secret" });
    public List<Triggered> Sent { get; } = new();
    public Action<Triggered>? OnTrigger { get; set; }
    public HibiraOptions Options => _inner.Options;

    public Task TriggerAsync(IEnumerable<string> channels, string eventName, object? data, string? socketId = null, CancellationToken cancellationToken = default)
    {
        var t = new Triggered(channels.ToList(), eventName, data is string s ? JsonNode.Parse(s) : null, socketId);
        lock (Sent) Sent.Add(t);
        OnTrigger?.Invoke(t);
        return Task.CompletedTask;
    }

    public Task TriggerAsync(string channel, string eventName, object? data, string? socketId = null, CancellationToken cancellationToken = default) =>
        TriggerAsync(new[] { channel }, eventName, data, socketId, cancellationToken);

    public Task TriggerBatchAsync(IEnumerable<HibiraBatchEvent> events, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SendToUserAsync(string userId, string eventName, object? data, CancellationToken cancellationToken = default) =>
        TriggerAsync(HibiraChannelName.ForUser(userId), eventName, data, null, cancellationToken);
    public Task<IReadOnlyDictionary<string, HibiraChannelInfo>> GetChannelsAsync(string? prefix = null, string? info = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<HibiraChannelInfo> GetChannelAsync(string channel, string? info = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<string>> GetUsersAsync(string presenceChannel, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task TerminateUserConnectionsAsync(string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<HibiraApproval> RequestApprovalAsync(HibiraApprovalRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<HibiraApproval> GetApprovalAsync(string approvalId, TimeSpan? wait = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public HibiraChannelAuth AuthorizeChannel(string socketId, string channel) => _inner.AuthorizeChannel(socketId, channel);
    public HibiraChannelAuth AuthorizePresenceChannel(string socketId, string channel, string userId, object? userInfo = null) => _inner.AuthorizePresenceChannel(socketId, channel, userId, userInfo);
    public HibiraUserAuth AuthenticateUser(string socketId, string userId, IReadOnlyDictionary<string, object?>? userData = null) => _inner.AuthenticateUser(socketId, userId, userData);
    public HibiraWebhook? VerifyWebhook(string? key, string? signature, string? previousSignature, ReadOnlySpan<byte> body) => _inner.VerifyWebhook(key, signature, previousSignature, body);
}

public interface IChatClient
{
    Task ReceiveMessage(string user, string message);
}

public class Counter { public int Connected; public int Disconnected; }

public class ChatHub : HibiraHub
{
    private readonly Counter _counter;
    public ChatHub(Counter counter) => _counter = counter;

    public Task SendMessage(string user, string message) => Clients.All.SendAsync("ReceiveMessage", user, message);
    public Task Echo(string text) => Clients.Caller.SendAsync("Echo", text);
    public Task Shout(string text) => Clients.Others.SendAsync("Shout", text);
    public Task Join(string room) => Groups.AddToGroupAsync(Context.ConnectionId, room);
    public Task Leave(string room) => Groups.RemoveFromGroupAsync(Context.ConnectionId, room);
    public Task ToRoom(string room, string text) => Clients.Group(room).SendAsync("Room", text);
    public Task ToUser(string user, string text) => Clients.User(user).SendAsync("Direct", text);
    public int Add(int a, int b) => a + b;
    public async Task<Point> Move(Point p, Counter counter) { await Task.Yield(); return p with { X = p.X + counter.Connected }; }
    public string? WhoAmI() => Context.UserIdentifier;
    [Authorize(Roles = "admin")] public string Admin() => "ok";
    public void Fail() => throw new InvalidOperationException("secret detail");
    public void FailNicely() => throw new HubException("try again");
    [HubMethodName("renamed")] public string Original() => "renamed!";
    public override Task OnConnectedAsync() { Interlocked.Increment(ref _counter.Connected); return Task.CompletedTask; }
    public override Task OnDisconnectedAsync(Exception? exception) { Interlocked.Increment(ref _counter.Disconnected); return Task.CompletedTask; }
}

public record Point(int X, int Y);

[HibiraHubName("typed")]
public class TypedHub : HibiraHub<IChatClient>
{
    public Task Say(string text) => Clients.All.ReceiveMessage(Context.UserIdentifier ?? "anon", text);
}

[Authorize]
public class SecretHub : HibiraHub
{
    public string Ping() => "pong";
}

public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-User", out var user)) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.ToString()) };
        if (Request.Headers.TryGetValue("X-Role", out var role)) claims.Add(new Claim(ClaimTypes.Role, role.ToString()));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), "test")));
    }
}

public sealed class HubTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private readonly FakeHibiraClient _hibira = new();
    private readonly Counter _counter = new();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHibiraClient>(_hibira);
        builder.Services.AddSingleton(_counter);
        builder.Services.AddHibira().AddHubOptions(o => o.GroupJoinTimeout = TimeSpan.FromSeconds(2));
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapHibiraHub<ChatHub>("/hubs/chat");
        _app.MapHibiraHub<TypedHub>("/hubs/typed");
        _app.MapHibiraHub<SecretHub>("/hubs/secret");
        _app.MapHibiraWebhooks("/hibira/webhooks");
        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task<(string Id, string Token)> ConnectAsync(string hub, string id, string? user = null)
    {
        var auth = await Post($"/hubs/{hub}/auth", null, user, form: new() { ["socket_id"] = id, ["channel_name"] = $"private-hibira.{hub}.c.{id}" });
        Assert.Equal(HttpStatusCode.OK, auth.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post($"/hubs/{hub}/handshake", new JsonObject { ["connectionId"] = id }, user)).StatusCode);
        var handshake = _hibira.Sent.Last(t => t.Event == "hibira:handshake");
        Assert.Equal($"private-hibira.{hub}.c.{id}", Assert.Single(handshake.Channels));
        var token = handshake.Data!["token"]!.GetValue<string>();
        var connect = await Post($"/hubs/{hub}/connect", new JsonObject { ["connectionId"] = id, ["token"] = token }, user);
        Assert.Equal(HttpStatusCode.OK, connect.StatusCode);
        return (id, token);
    }

    private async Task<JsonNode> Invoke(string hub, (string Id, string Token) c, string target, string? user = null, string? role = null, params object?[] args)
    {
        var res = await Post($"/hubs/{hub}/invoke", new JsonObject
        {
            ["connectionId"] = c.Id,
            ["token"] = c.Token,
            ["target"] = target,
            ["arguments"] = System.Text.Json.JsonSerializer.SerializeToNode(args),
        }, user, role: role);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
    }

    private async Task<HttpResponseMessage> Post(string path, JsonObject? json, string? user = null, Dictionary<string, string>? form = null, string? role = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = form != null ? new FormUrlEncodedContent(form) : new StringContent(json?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json"),
        };
        if (user != null) req.Headers.Add("X-User", user);
        if (role != null) req.Headers.Add("X-Role", role);
        return await _http.SendAsync(req);
    }

    [Fact]
    public async Task NegotiateDescribesTheHub()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/hubs/chat/negotiate");
        req.Headers.Add("X-User", "ann");
        var n = JsonNode.Parse(await (await _http.SendAsync(req)).Content.ReadAsStringAsync())!;
        Assert.Equal("chat", n["hub"]!.GetValue<string>());
        Assert.Equal("key", n["key"]!.GetValue<string>());
        Assert.Equal("private-hibira.chat", n["channel"]!.GetValue<string>());
        Assert.Equal("private-hibira.chat.c.", n["connectionChannelPrefix"]!.GetValue<string>());
        Assert.Equal("ann", n["userId"]!.GetValue<string>());
        Assert.DoesNotContain("secret", n.ToJsonString());
    }

    [Fact]
    public async Task ConnectionChannelsAreOnlySignedForTheirOwnSocket()
    {
        var bad = await Post("/hubs/chat/auth", null, form: new() { ["socket_id"] = "1.1", ["channel_name"] = "private-hibira.chat.c.2.2" });
        Assert.Equal(HttpStatusCode.Forbidden, bad.StatusCode);
        var other = await Post("/hubs/chat/auth", null, form: new() { ["socket_id"] = "1.1", ["channel_name"] = "private-something-else" });
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        var all = await Post("/hubs/chat/auth", null, form: new() { ["socket_id"] = "1.1", ["channel_name"] = "private-hibira.chat" });
        var json = JsonNode.Parse(await all.Content.ReadAsStringAsync())!;
        Assert.Equal("key:" + HibiraSignature.Hmac("secret", "1.1:private-hibira.chat"), json["auth"]!.GetValue<string>());
    }

    [Fact]
    public async Task InvokeBroadcastsAndReturnsResults()
    {
        var c = await ConnectAsync("chat", "11.1");
        Assert.Equal(1, _counter.Connected);

        Assert.Null((await Invoke("chat", c, "SendMessage", null, null, "ann", "hi"))["error"]);
        var msg = _hibira.Sent.Last();
        Assert.Equal("ReceiveMessage", msg.Event);
        Assert.Equal("private-hibira.chat", Assert.Single(msg.Channels));
        Assert.Equal("{\"hub\":\"chat\",\"arguments\":[\"ann\",\"hi\"]}", msg.Data!.ToJsonString());
        Assert.Null(msg.SocketId);

        await Invoke("chat", c, "echo", null, null, "x");
        Assert.Equal("private-hibira.chat.c.11.1", Assert.Single(_hibira.Sent.Last().Channels));

        await Invoke("chat", c, "Shout", null, null, "x");
        Assert.Equal("11.1", _hibira.Sent.Last().SocketId);

        Assert.Equal(5, (await Invoke("chat", c, "Add", null, null, 2, 3))["result"]!.GetValue<int>());
        var moved = await Invoke("chat", c, "Move", null, null, new { x = 1, y = 2 });
        Assert.Equal(2, moved["result"]!["x"]!.GetValue<int>());
        Assert.Equal("renamed!", (await Invoke("chat", c, "renamed"))["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task ErrorsAreHiddenUnlessHubException()
    {
        var c = await ConnectAsync("chat", "12.1");
        var e1 = (await Invoke("chat", c, "Fail"))["error"]!.GetValue<string>();
        Assert.DoesNotContain("secret detail", e1);
        Assert.Contains("try again", (await Invoke("chat", c, "FailNicely"))["error"]!.GetValue<string>());
        Assert.Contains("Method does not exist", (await Invoke("chat", c, "Nope"))["error"]!.GetValue<string>());
        Assert.Contains("argument", (await Invoke("chat", c, "Add", null, null, 1))["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TokensAreBoundToConnectionAndUser()
    {
        var c = await ConnectAsync("chat", "13.1", user: "ann");
        Assert.Equal("ann", (await Invoke("chat", c, "WhoAmI", "ann"))["result"]!.GetValue<string>());
        var stolen = await Post("/hubs/chat/invoke", new JsonObject { ["connectionId"] = "13.1", ["token"] = c.Token, ["target"] = "WhoAmI", ["arguments"] = new JsonArray() }, user: "bob");
        Assert.Equal(HttpStatusCode.Unauthorized, stolen.StatusCode);
        var forged = await Post("/hubs/chat/invoke", new JsonObject { ["connectionId"] = "13.2", ["token"] = c.Token, ["target"] = "WhoAmI", ["arguments"] = new JsonArray() }, user: "ann");
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }

    [Fact]
    public async Task MethodAuthorizeChecksPolicies()
    {
        var c = await ConnectAsync("chat", "14.1", user: "ann");
        Assert.Contains("unauthorized", (await Invoke("chat", c, "Admin", "ann"))["error"]!.GetValue<string>());
        Assert.Equal("ok", (await Invoke("chat", c, "Admin", "ann", "admin"))["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task HubAuthorizeProtectsEveryEndpoint()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("/hubs/secret/negotiate")).StatusCode);
        var c = await ConnectAsync("secret", "15.1", user: "ann");
        Assert.Equal("pong", (await Invoke("secret", c, "Ping", "ann"))["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task GroupsSignOnlyMembersAndWaitForTheSubscription()
    {
        var c = await ConnectAsync("chat", "16.1");
        var groupChannel = "private-hibira.chat.g.room=201";
        var refused = await Post("/hubs/chat/auth", null, form: new() { ["socket_id"] = "16.1", ["channel_name"] = groupChannel });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // Act as the client: when told to join, subscribe (ask for a signature).
        _hibira.OnTrigger = t =>
        {
            if (t.Event == "hibira:join")
                _ = Task.Run(() => Post("/hubs/chat/auth", null, form: new() { ["socket_id"] = "16.1", ["channel_name"] = t.Data!["channel"]!.GetValue<string>() }));
        };
        var started = DateTime.UtcNow;
        await Invoke("chat", c, "Join", null, null, "room 1");
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1.5), "join waited for the timeout");
        var join = _hibira.Sent.Single(t => t.Event == "hibira:join");
        Assert.Equal(groupChannel, join.Data!["channel"]!.GetValue<string>());
        Assert.Equal("private-hibira.chat.c.16.1", Assert.Single(join.Channels));

        await Invoke("chat", c, "ToRoom", null, null, "room 1", "hello");
        Assert.Equal(groupChannel, Assert.Single(_hibira.Sent.Last().Channels));

        await Invoke("chat", c, "Leave", null, null, "room 1");
        Assert.Equal("hibira:leave", _hibira.Sent.Last().Event);
        var after = await Post("/hubs/chat/auth", null, form: new() { ["socket_id"] = "16.1", ["channel_name"] = groupChannel });
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
    }

    [Fact]
    public async Task UsersGetTheirOwnChannel()
    {
        var c = await ConnectAsync("chat", "17.1", user: "ann");
        await Invoke("chat", c, "ToUser", "ann", null, "bob@example.com", "hi");
        Assert.Equal("#server-to-user-bob@example.com", Assert.Single(_hibira.Sent.Last().Channels));

        var signin = await Post("/hubs/chat/user-auth", null, "ann", form: new() { ["socket_id"] = "17.1" });
        var json = JsonNode.Parse(await signin.Content.ReadAsStringAsync())!;
        Assert.Equal("{\"id\":\"ann\"}", json["user_data"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/hubs/chat/user-auth", null, form: new() { ["socket_id"] = "17.1" })).StatusCode);
    }

    [Fact]
    public async Task TypedHubsSendByMethodName()
    {
        var c = await ConnectAsync("typed", "18.1", user: "ann");
        await Invoke("typed", c, "Say", "ann", null, "hey");
        var msg = _hibira.Sent.Last();
        Assert.Equal("ReceiveMessage", msg.Event);
        Assert.Equal("private-hibira.typed", Assert.Single(msg.Channels));
        Assert.Equal("[\"ann\",\"hey\"]", msg.Data!["arguments"]!.ToJsonString());
    }

    [Fact]
    public async Task HubContextSendsFromOutside()
    {
        var ctx = _app.Services.GetRequiredService<IHubContext<ChatHub>>();
        await ctx.Clients.Groups(new[] { "a", "b" }).SendAsync("News", 1);
        Assert.Equal(new[] { "private-hibira.chat.g.a", "private-hibira.chat.g.b" }, _hibira.Sent.Last().Channels);
        var typed = _app.Services.GetRequiredService<IHubContext<TypedHub, IChatClient>>();
        await typed.Clients.User("ann").ReceiveMessage("system", "hi");
        Assert.Equal("#server-to-user-ann", Assert.Single(_hibira.Sent.Last().Channels));
        Assert.Throws<NotSupportedException>(() => ctx.Clients.AllExcept(new[] { "1.1", "2.2" }));
        await Assert.ThrowsAsync<ArgumentException>(() => ctx.Clients.All.SendAsync("hibira:join"));
    }

    [Fact]
    public async Task DisconnectRunsOnceFromStopOrWebhook()
    {
        var a = await ConnectAsync("chat", "19.1");
        Assert.Equal(HttpStatusCode.OK, (await Post("/hubs/chat/disconnect", new JsonObject { ["connectionId"] = a.Id, ["token"] = a.Token })).StatusCode);
        Assert.Equal(1, _counter.Disconnected);

        await ConnectAsync("chat", "19.2");
        var body = Encoding.UTF8.GetBytes("{\"time_ms\":1,\"events\":[{\"name\":\"channel_vacated\",\"channel\":\"private-hibira.chat.c.19.2\"},{\"name\":\"channel_vacated\",\"channel\":\"private-hibira.chat.c.19.1\"}]}");
        var unsigned = new HttpRequestMessage(HttpMethod.Post, "/hibira/webhooks") { Content = new ByteArrayContent(body) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(unsigned)).StatusCode);
        var signed = new HttpRequestMessage(HttpMethod.Post, "/hibira/webhooks") { Content = new ByteArrayContent(body) };
        signed.Headers.Add("X-Pusher-Key", "key");
        signed.Headers.Add("X-Pusher-Signature", HibiraSignature.Hmac("secret", body));
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(signed)).StatusCode);
        Assert.Equal(2, _counter.Disconnected);
    }

    [Fact]
    public void LongGroupNamesAreHashed()
    {
        var channels = new HubChannels("chat");
        var name = channels.Group(new string('x', 300));
        Assert.StartsWith("private-hibira.chat.h.", name);
        Assert.True(HibiraChannelName.IsValid(name));
        Assert.Equal(name, channels.Group(new string('x', 300)));
    }
}
