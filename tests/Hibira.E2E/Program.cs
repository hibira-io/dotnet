// End-to-end run against a real Hibira (or any Pusher-compatible) server:
// hosts a hub on Kestrel, connects two .NET clients and one browser client
// (hibira-hub.js under Node), and checks every way of sending.
//
//   E2E_HOST=127.0.0.1 E2E_PORT=6001 E2E_TLS=0 E2E_APP_ID=... E2E_KEY=... E2E_SECRET=... dotnet run --project tests/Hibira.E2E
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Hibira;
using Hibira.AspNetCore;
using Hibira.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

string Env(string k, string d) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : d;
const string hubPort = "5099";

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://127.0.0.1:{hubPort}");
builder.Services.AddSingleton<Counter>();
builder.Services.AddHibira(o =>
{
    o.AppId = Env("E2E_APP_ID", "app1");
    o.Key = Env("E2E_KEY", "key1");
    o.Secret = Env("E2E_SECRET", "secret1secret1secret1");
    o.Host = Env("E2E_HOST", "127.0.0.1");
    o.Port = int.Parse(Env("E2E_PORT", "6001"));
    o.UseTls = Env("E2E_TLS", "0") == "1";
}).AddHubOptions(o => o.EnableDetailedErrors = true);
builder.Services.AddAuthentication("header").AddScheme<AuthenticationSchemeOptions, HeaderAuth>("header", _ => { });
builder.Services.AddAuthorization();
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapHibiraHub<E2EHub>("/hubs/e2e");
await app.StartAsync();

var hubUrl = $"http://127.0.0.1:{hubPort}/hubs/e2e";
var failures = new List<string>();
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "ok    " : "FAIL  ") + what);
    if (!ok) failures.Add(what);
}

HubConnection Connect(string user) => new HubConnectionBuilder()
    .WithUrl(hubUrl, o => o.Headers["X-User"] = user)
    .WithAutomaticReconnect()
    .Build();

var inbox = new ConcurrentDictionary<string, ConcurrentQueue<string>>();
void Listen(HubConnection c, string who)
{
    foreach (var m in new[] { "ReceiveMessage", "Shout", "Room", "Direct", "Echo", "News" })
    {
        var method = m;
        c.On<string, string>(method, (from, text) => inbox.GetOrAdd(who, _ => new()).Enqueue($"{method}:{from}:{text}"));
    }
}
async Task<bool> Got(string who, string message, int ms = 5000)
{
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < ms)
    {
        if (inbox.TryGetValue(who, out var q) && q.Contains(message)) return true;
        await Task.Delay(25);
    }
    return false;
}

var ann = Connect("ann");
var bob = Connect("bob");
Listen(ann, "ann");
Listen(bob, "bob");
await ann.StartAsync();
await bob.StartAsync();
Check(ann.State == HubConnectionState.Connected && ann.ConnectionId != null, $"ann connected as {ann.ConnectionId}");
Check(app.Services.GetRequiredService<Counter>().Connected == 2, "OnConnectedAsync ran for both");

await ann.InvokeAsync("SendMessage", "hello all");
Check(await Got("ann", "ReceiveMessage:ann:hello all") && await Got("bob", "ReceiveMessage:ann:hello all"), "Clients.All reaches everyone");

Check(await ann.InvokeAsync<int>("Add", 2, 3) == 5, "InvokeAsync returns the result");
Check(await ann.InvokeAsync<string>("WhoAmI") == "ann", "Context.UserIdentifier is the caller");

await ann.InvokeAsync("Echo", "me");
Check(await Got("ann", "Echo:ann:me") && !await Got("bob", "Echo:ann:me", 300), "Clients.Caller reaches only the caller");

await ann.InvokeAsync("Shout", "others");
Check(await Got("bob", "Shout:ann:others") && !await Got("ann", "Shout:ann:others", 300), "Clients.Others skips the caller");

await bob.InvokeAsync("Join", "room 1");
await ann.InvokeAsync("ToRoom", "room 1", "in the room");
Check(await Got("bob", "Room:ann:in the room") && !await Got("ann", "Room:ann:in the room", 300), "Clients.Group reaches members right after AddToGroupAsync");

await ann.InvokeAsync("ToUser", "bob", "psst");
Check(await Got("bob", "Direct:ann:psst") && !await Got("ann", "Direct:ann:psst", 300), "Clients.User reaches the signed-in user");

try
{
    await ann.InvokeAsync("Fail");
    Check(false, "hub errors reach the caller");
}
catch (HibiraClientException e)
{
    Check(e.Message.Contains("nope"), "hub errors reach the caller");
}

var ctx = app.Services.GetRequiredService<IHubContext<E2EHub>>();
await ctx.Clients.All.SendAsync("News", "server", "from IHubContext");
Check(await Got("ann", "News:server:from IHubContext") && await Got("bob", "News:server:from IHubContext"), "IHubContext sends from outside the hub");

// The browser client, under Node: it joins group "js", then waits for us.
var js = Path.Combine(Path.GetDirectoryName(Source.Here())!, "js", "e2e.mjs");
var node = Process.Start(new ProcessStartInfo("node", $"\"{js}\" {hubUrl}")
{
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    WorkingDirectory = Path.GetDirectoryName(js)!,
})!;
var ready = new TaskCompletionSource();
node.OutputDataReceived += (_, e) =>
{
    if (e.Data == null) return;
    Console.WriteLine("  js: " + e.Data);
    if (e.Data == "READY") ready.TrySetResult();
};
node.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine("  js! " + e.Data); };
node.BeginOutputReadLine();
node.BeginErrorReadLine();
if (await Task.WhenAny(ready.Task, Task.Delay(20000)) != ready.Task) Check(false, "js client got ready");
else
{
    await ann.InvokeAsync("SendMessage", "from-dotnet");
    await ann.InvokeAsync("ToRoom", "js", "to-js-room");
    await ann.InvokeAsync("ToUser", "carol", "to-carol");
    Check(await Got("ann", "ReceiveMessage:carol:from-js", 10000), ".NET client receives a message sent from the browser client");
}
await node.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)).ContinueWith(_ => { });
Check(node.HasExited && node.ExitCode == 0, "browser client (hibira-hub.js) passed its checks");

await bob.InvokeAsync("Leave", "room 1");
await ann.InvokeAsync("ToRoom", "room 1", "after leave");
Check(!await Got("bob", "Room:ann:after leave", 1000), "RemoveFromGroupAsync stops group messages");

var rest = app.Services.GetRequiredService<IHibiraClient>();
var channels = await rest.GetChannelsAsync("private-hibira.e2e");
Check(channels.ContainsKey("private-hibira.e2e"), "HTTP API lists the hub channel");

var reconnected = new TaskCompletionSource<string?>();
ann.Reconnected += id => { reconnected.TrySetResult(id); return Task.CompletedTask; };
var oldId = ann.ConnectionId;
await rest.TerminateUserConnectionsAsync("ann");
var newId = await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(15)).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);
Check(newId != null && newId != oldId, $"automatic reconnect after the server closed the socket ({oldId} -> {newId})");
Check(newId != null && await ann.InvokeAsync<string>("WhoAmI") == "ann", "invoke works after reconnecting");

await bob.StopAsync();
await Task.Delay(200);
Check(app.Services.GetRequiredService<Counter>().Disconnected >= 1, "StopAsync runs OnDisconnectedAsync");
await ann.DisposeAsync();
await app.StopAsync();

Console.WriteLine(failures.Count == 0 ? "\nall checks passed" : $"\n{failures.Count} check(s) failed");
return failures.Count == 0 ? 0 : 1;

public static class Source
{
    public static string Here([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}

public sealed class Counter
{
    public int Connected;
    public int Disconnected;
}

public sealed class E2EHub : HibiraHub
{
    private readonly Counter _counter;
    public E2EHub(Counter counter) => _counter = counter;

    private string Me => Context.UserIdentifier ?? "anon";
    public Task SendMessage(string text) => Clients.All.SendAsync("ReceiveMessage", Me, text);
    public Task Echo(string text) => Clients.Caller.SendAsync("Echo", Me, text);
    public Task Shout(string text) => Clients.Others.SendAsync("Shout", Me, text);
    public Task Join(string room) => Groups.AddToGroupAsync(Context.ConnectionId, room);
    public Task Leave(string room) => Groups.RemoveFromGroupAsync(Context.ConnectionId, room);
    public Task ToRoom(string room, string text) => Clients.Group(room).SendAsync("Room", Me, text);
    public Task ToUser(string user, string text) => Clients.User(user).SendAsync("Direct", Me, text);
    public int Add(int a, int b) => a + b;
    public string WhoAmI() => Me;
    public void Fail() => throw new HubException("nope");
    public override Task OnConnectedAsync() { Interlocked.Increment(ref _counter.Connected); return Task.CompletedTask; }
    public override Task OnDisconnectedAsync(Exception? e) { Interlocked.Increment(ref _counter.Disconnected); return Task.CompletedTask; }
}

public sealed class HeaderAuth : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public HeaderAuth(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e) : base(o, l, e) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(Request.Headers.TryGetValue("X-User", out var u)
            ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, u.ToString()) }, "header")), "header"))
            : AuthenticateResult.NoResult());
}
