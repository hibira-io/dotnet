using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Hibira.Tests;

public class CoreTests
{
    private static readonly HibiraOptions Pusher = new() { AppId = "3", Key = "278d425bdf160c739803", Secret = "7ad3773142a6692b25b8" };

    [Fact]
    public void PrivateChannelAuthMatchesThePusherDocs()
    {
        var auth = new HibiraClient(Pusher).AuthorizeChannel("1234.1234", "private-foobar");
        Assert.Equal("278d425bdf160c739803:58df8b0c36d6982b82c3ecf6b4662e34fe8c25bba48f5369f135bf843651c3a4", auth.Auth);
        Assert.Null(auth.ChannelData);
    }

    [Fact]
    public void PresenceAuthSignsTheChannelData()
    {
        var auth = new HibiraClient(Pusher).AuthorizePresenceChannel("1234.1234", "presence-foobar", "10", new { name = "Mr. Channels" });
        Assert.Equal("{\"user_id\":\"10\",\"user_info\":{\"name\":\"Mr. Channels\"}}", auth.ChannelData);
        Assert.Equal("278d425bdf160c739803:" + HibiraSignature.Hmac(Pusher.Secret, "1234.1234:presence-foobar:" + auth.ChannelData), auth.Auth);
    }

    [Fact]
    public void UserAuthSignsTheUserData()
    {
        var auth = new HibiraClient(Pusher).AuthenticateUser("1234.1234", "ann+1@example.com");
        Assert.Equal("{\"id\":\"ann=2B1@example.com\"}", auth.UserData);
        Assert.Equal("278d425bdf160c739803:" + HibiraSignature.Hmac(Pusher.Secret, "1234.1234::user::" + auth.UserData), auth.Auth);
    }

    [Fact]
    public void RejectsBadSocketIds()
    {
        var c = new HibiraClient(Pusher);
        Assert.Throws<ArgumentException>(() => c.AuthorizeChannel("1234", "private-a"));
        Assert.Throws<ArgumentException>(() => c.AuthorizeChannel("12a.4", "private-a"));
    }

    [Fact]
    public void HttpSignatureMatchesThePusherDocs()
    {
        var body = Encoding.UTF8.GetBytes("{\"name\":\"foo\",\"channels\":[\"project-3\"],\"data\":\"{\\\"some\\\":\\\"data\\\"}\"}");
        var q = HibiraSignature.SignedQuery(Pusher.Key, Pusher.Secret, "POST", "/apps/3/events", null, body, DateTimeOffset.FromUnixTimeSeconds(1353088179));
        Assert.Contains("body_md5=ec365a775a4cd0599faeb73354201b6f", q);
        Assert.EndsWith("auth_signature=da454824c97ba181a32ccc17a72625ba02771f50b50e1e7430e47a1f3f457e6c", q);
    }

    [Theory]
    [InlineData("room-1", "room-1")]
    [InlineData("a b", "a=20b")]
    [InlineData("x=y", "x=3Dy")]
    [InlineData("日本", "=E6=97=A5=E6=9C=AC")]
    [InlineData("😀", "=F0=9F=98=80")]
    public void EscapeRoundTrips(string raw, string escaped)
    {
        Assert.Equal(escaped, HibiraChannelName.Escape(raw));
        Assert.Equal(raw, HibiraChannelName.Unescape(escaped));
        Assert.True(HibiraChannelName.IsValid("private-" + escaped));
    }

    [Fact]
    public void VerifiesWebhooksWithTheCurrentOrPreviousSecret()
    {
        var c = new HibiraClient(Pusher);
        var body = Encoding.UTF8.GetBytes("{\"time_ms\":1,\"events\":[{\"name\":\"channel_vacated\",\"channel\":\"private-x\"}]}");
        var sig = HibiraSignature.Hmac(Pusher.Secret, body);
        var hook = c.VerifyWebhook(Pusher.Key, sig, null, body);
        Assert.NotNull(hook);
        Assert.Equal("channel_vacated", hook!.Events[0].Name);
        Assert.NotNull(c.VerifyWebhook(Pusher.Key, "bad", sig, body));
        Assert.Null(c.VerifyWebhook(Pusher.Key, "bad", null, body));
        Assert.Null(c.VerifyWebhook("other", sig, null, body));
    }

    [Fact]
    public async Task TriggerSendsASignedRequestAndChunksChannels()
    {
        var handler = new RecordingHandler();
        var c = new HibiraClient(new HibiraOptions { AppId = "1", Key = "k", Secret = "s", Host = "ws.example", UseTls = true }, new HttpClient(handler));
        await c.TriggerAsync(Enumerable.Range(0, 150).Select(i => "c" + i), "ev", new { a = 1 }, "1.2");
        Assert.Equal(2, handler.Requests.Count);
        var (uri, body) = handler.Requests[0];
        Assert.Equal("https://ws.example/apps/1/events", uri.GetLeftPart(UriPartial.Path));
        Assert.Contains("auth_key=k", uri.Query);
        var json = JsonNode.Parse(body)!;
        Assert.Equal("{\"a\":1}", json["data"]!.GetValue<string>());
        Assert.Equal(100, json["channels"]!.AsArray().Count);
        Assert.Equal("1.2", json["socket_id"]!.GetValue<string>());
        Assert.Equal(50, JsonNode.Parse(handler.Requests[1].Body)!["channels"]!.AsArray().Count);
    }

    [Fact]
    public async Task ErrorsCarryTheServerMessage()
    {
        var handler = new RecordingHandler { Status = HttpStatusCode.RequestEntityTooLarge, Response = "{\"error\":\"Event data exceeds the maximum size\"}" };
        var c = new HibiraClient(new HibiraOptions { AppId = "1", Key = "k", Secret = "s" }, new HttpClient(handler));
        var e = await Assert.ThrowsAsync<HibiraException>(() => c.TriggerAsync("a", "b", "c"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, e.StatusCode);
        Assert.Contains("maximum size", e.Message);
    }

    [Fact]
    public void MissingCredentialsExplainWhereToFindThem()
    {
        var e = Assert.Throws<InvalidOperationException>(() => new HibiraClient(new HibiraOptions()));
        Assert.Contains("hibira.io/portal", e.Message);
    }
}

internal sealed class RecordingHandler : HttpMessageHandler
{
    public List<(Uri Uri, string Body)> Requests { get; } = new();
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public string Response { get; set; } = "{}";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.RequestUri!, body));
        return new HttpResponseMessage(Status) { Content = new StringContent(Response) };
    }
}
