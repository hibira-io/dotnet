using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hibira.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hibira.Tests;

public sealed class WaitingRoomTests : IAsyncLifetime
{
    // Signed by the Hibira server (internal/waitroom NewPass) at 1800000000, ttl 30 min.
    private const string ServerSecret = "wr-secret-0123456789";
    private const string ServerPass = "eyJ2IjoxLCJhcHAiOiJ3ci1hcHAiLCJyb29tIjoicHJlc2FsZSIsIm4iOjQyLCJ0aWQiOiJkNzlmZTc4OWEzOGI4NjMxYjUiLCJpYXQiOjE4MDAwMDAwMDAsImV4cCI6MTgwMDAwMTgwMH0.q7Uc_O6XKwNA_LVLQITlgv_YtDi9dIwiRzUUUSsGa08";
    private const string ServerTicket = "eyJ2IjoxLCJhcHAiOiJ3ci1hcHAiLCJyb29tIjoicHJlc2FsZSIsIm4iOjQyLCJ0aWQiOiJkNzlmZTc4OWEzOGI4NjMxYjUiLCJpYXQiOjE4MDAwMDAwMDAsImV4cCI6MTgwMDA4NjQwMH0.l-KNMOVwV_xJJCQaQ-JXQKsrj-XlEdsTxG7X4Kp_2ck";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000100);

    private const string Secret = "app-secret";
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddHibira(o =>
        {
            o.AppId = "1";
            o.Key = "app-key";
            o.Secret = Secret;
            o.Host = "hibira.test";
        });
        _app = builder.Build();
        _app.UseHibiraWaitingRoom("/vip", "vip");
        _app.UseRouting();
        _app.UseHibiraWaitingRoom();
        _app.MapGet("/drop/{id}", (HttpContext c) => $"in #{c.GetWaitingRoomPass()!.Number}").RequireWaitingRoom("presale");
        _app.MapPost("/drop/{id}", (HttpContext c) => $"bought #{c.GetWaitingRoomPass()!.Number}").RequireWaitingRoom("presale");
        _app.MapGet("/vip/{id}", (HttpContext c) => $"vip #{c.GetWaitingRoomPass()!.Number}");
        _app.MapGet("/open", () => "open");
        await _app.StartAsync();
        _http = _app.GetTestServer().CreateClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static string Sign(string json, string secret = Secret)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = B64(Encoding.UTF8.GetBytes(json));
        return payload + "." + B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes("hibira-waitroom-pass." + payload)));
    }

    private static string Fresh(string room = "presale")
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Sign($"{{\"v\":1,\"app\":\"1\",\"room\":\"{room}\",\"n\":7,\"tid\":\"t7\",\"iat\":{now},\"exp\":{now + 600}}}");
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, string? cookie = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (cookie != null) req.Headers.Add("Cookie", cookie);
        return _http.SendAsync(req);
    }

    [Fact]
    public void VerifiesAServerPass()
    {
        var pass = WaitingRoom.VerifyPass(ServerPass, new[] { ServerSecret }, "presale", "wr-app", Now);
        Assert.Equal(new WaitingRoomPass("wr-app", "presale", 42, "d79fe789a38b8631b5", 1800000000, 1800001800), pass);
        Assert.NotNull(WaitingRoom.VerifyPass(ServerPass, new[] { "old", ServerSecret }, now: Now));
    }

    [Fact]
    public void RejectsForgedExpiredForeignAndTickets()
    {
        var s = new[] { ServerSecret };
        Assert.Null(WaitingRoom.VerifyPass(ServerPass, new[] { "wrong" }, now: Now));
        Assert.Null(WaitingRoom.VerifyPass(ServerPass, s, "other", now: Now));
        Assert.Null(WaitingRoom.VerifyPass(ServerPass, s, appId: "other", now: Now));
        Assert.Null(WaitingRoom.VerifyPass(ServerPass, s, now: DateTimeOffset.FromUnixTimeSeconds(1800001800)));
        Assert.Null(WaitingRoom.VerifyPass(ServerTicket, s, now: Now));
        Assert.Null(WaitingRoom.VerifyPass(ServerPass[..^3] + "AAA", s, now: Now));
        foreach (var bad in new[] { "", null, "a.b.c", "!!.??" })
            Assert.Null(WaitingRoom.VerifyPass(bad, s, now: Now));
        Assert.Null(WaitingRoom.VerifyPass(Sign("{\"v\":2,\"exp\":9999999999}", ServerSecret), s, now: Now));
    }

    [Fact]
    public async Task SendsVisitorsWithoutAPassToTheQueue()
    {
        var res = await Send(HttpMethod.Get, "https://shop.example.com/drop/1?a=b");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("https://hibira.test/wait/app-key/presale?return=https%3A%2F%2Fshop.example.com%2Fdrop%2F1%3Fa%3Db", res.Headers.Location!.OriginalString);
        Assert.Equal("open", await (await Send(HttpMethod.Get, "/open")).Content.ReadAsStringAsync());
        var post = await Send(HttpMethod.Post, "/drop/1");
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal("waiting_room", JsonDocument.Parse(await post.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task TurnsThePassIntoACookie()
    {
        var pass = Fresh();
        var res = await Send(HttpMethod.Get, "https://shop.example.com/drop/1?a=b&hibira_pass=" + pass);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("https://shop.example.com/drop/1?a=b", res.Headers.Location!.OriginalString);
        var cookie = Assert.Single(res.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith($"hibira_wr_presale={pass}; max-age=", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("samesite=lax", cookie);
        Assert.Contains("httponly", cookie);

        Assert.Equal("in #7", await (await Send(HttpMethod.Get, "/drop/1", $"hibira_wr_presale={pass}")).Content.ReadAsStringAsync());
        Assert.Equal("bought #7", await (await Send(HttpMethod.Post, "/drop/1", $"hibira_wr_presale={pass}")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await Send(HttpMethod.Get, "/drop/1", $"hibira_wr_presale={Fresh("other")}")).StatusCode);
    }

    [Fact]
    public async Task ProtectsPathPrefixes()
    {
        Assert.Contains("/wait/app-key/vip?", (await Send(HttpMethod.Get, "/vip/1")).Headers.Location!.OriginalString);
        Assert.Equal("vip #7", await (await Send(HttpMethod.Get, "/vip/1", $"hibira_wr_vip={Fresh("vip")}")).Content.ReadAsStringAsync());
    }
}
