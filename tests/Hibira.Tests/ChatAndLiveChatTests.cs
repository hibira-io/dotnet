using System.Net;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Hibira.AspNetCore;
using Xunit;

namespace Hibira.Tests;

public class ChatAndLiveChatTests
{
    private static (HibiraClient Client, RecordingHandler Handler) Client(string response = "{\"ok\":true}")
    {
        var handler = new RecordingHandler { Response = response };
        return (new HibiraClient(new HibiraOptions { AppId = "3", Key = "k", Secret = "s3cr3t", Host = "ws.example" }, new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task CallsEveryChatRoute()
    {
        var (c, h) = Client();
        var put = await c.PutConversationAsync("order 42", new { name = "Order", members = new[] { "u1", "agent" } });
        Assert.True(put["ok"]!.GetValue<bool>());
        await c.SendMessageAsync("order 42", "u1", "Hi", new Dictionary<string, object?> { ["push"] = "always" });
        await c.ListMessagesAsync("order 42", after: 10, limit: 50);
        await c.GetConversationAsync("c1");
        await c.AddMembersAsync("c1", new[] { "u2" });
        await c.RemoveMemberAsync("c1", "u/2");
        await c.MarkReadAsync("c1", "u1", 12);
        await c.SystemMessageAsync("c1", "Alice joined");
        await c.UserConversationsAsync("u1", limit: 20);
        await c.DeleteConversationAsync("c1");
        await c.ChatAsync(HttpMethod.Post, "/conversations/c1/typing", body: new { user_id = "u1" });

        Assert.Equal(new[]
        {
            "PUT /apps/3/chat/conversations/order%2042",
            "POST /apps/3/chat/conversations/order%2042/messages",
            "GET /apps/3/chat/conversations/order%2042/messages",
            "GET /apps/3/chat/conversations/c1",
            "POST /apps/3/chat/conversations/c1/members",
            "DELETE /apps/3/chat/conversations/c1/members/u%2F2",
            "POST /apps/3/chat/conversations/c1/read",
            "POST /apps/3/chat/conversations/c1/system",
            "GET /apps/3/chat/users/u1/conversations",
            "DELETE /apps/3/chat/conversations/c1",
            "POST /apps/3/chat/conversations/c1/typing",
        }, h.Requests.Select((r, i) => h.Methods[i].Method + " " + r.Uri.AbsolutePath));

        Assert.Equal("{\"name\":\"Order\",\"members\":[\"u1\",\"agent\"]}", h.Requests[0].Body);
        var msg = JsonNode.Parse(h.Requests[1].Body)!;
        Assert.Equal("u1", msg["user_id"]!.GetValue<string>());
        Assert.Equal("always", msg["push"]!.GetValue<string>());
        var q = HttpUtility.ParseQueryString(h.Requests[2].Uri.Query);
        Assert.Equal("10", q["after"]);
        Assert.Equal("50", q["limit"]);
        Assert.NotNull(q["auth_signature"]);
        Assert.Equal(12, JsonNode.Parse(h.Requests[6].Body)!["seq"]!.GetValue<long>());
        var pq = HttpUtility.ParseQueryString(h.Requests[0].Uri.Query);
        var canonical = string.Join("&", pq.AllKeys.Where(k => k != "auth_signature").OrderBy(k => k, StringComparer.Ordinal).Select(k => k + "=" + pq[k]));
        Assert.Equal(HibiraSignature.Hmac("s3cr3t", "PUT\n/apps/3/chat/conversations/order 42\n" + canonical), pq["auth_signature"]);
        await Assert.ThrowsAsync<ArgumentException>(() => c.GetConversationAsync(""));
    }

    [Fact]
    public async Task ChatErrorsCarryTheServerMessage()
    {
        var handler = new RecordingHandler { Status = HttpStatusCode.NotFound, Response = "{\"error\":\"conversation not found\"}" };
        var c = new HibiraClient(new HibiraOptions { AppId = "3", Key = "k", Secret = "s" }, new HttpClient(handler));
        var e = await Assert.ThrowsAsync<HibiraException>(() => c.GetConversationAsync("nope"));
        Assert.Contains("not found", e.Message);
    }

    [Fact]
    public void LiveChatSignsVerifiedVisitors()
    {
        Assert.Equal(HibiraSignature.Hmac("lcid_s", "42"), HibiraLiveChat.UserHash("lcid_s", "42"));
        Assert.Throws<ArgumentException>(() => HibiraLiveChat.UserHash("", "1"));

        Assert.Equal("", new HibiraLiveChat(null).ScriptTag());
        Assert.Equal("<script src=\"https://chat.hibira.io/w/v1.js\" data-workspace=\"ws_1\" async></script>", new HibiraLiveChat("ws_1").ScriptTag("7", "A"));

        var chat = new HibiraLiveChat("ws_1", "lcid_s");
        var hash = HibiraLiveChat.UserHash("lcid_s", "7");
        Assert.Equal(
            "<script src=\"https://chat.hibira.io/w/v1.js\" data-workspace=\"ws_1\" data-user-id=\"7\" data-user-hash=\"" + hash
            + "\" data-user-name=\"Aiko &quot;A&quot; &lt;B&gt;\" data-user-email=\"aiko@example.com\" nonce=\"n1\" async></script>",
            chat.ScriptTag("7", "Aiko \"A\" <B>", "aiko@example.com", "n1"));
        Assert.Equal(hash, chat.Identity("7")!["hash"]);
        Assert.Null(chat.Identity(null));
    }

    [Fact]
    public void AddHibiraRegistersLiveChatFromConfiguration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hibira:AppId"] = "3", ["Hibira:Key"] = "k", ["Hibira:Secret"] = "s",
            ["Hibira:LiveChat:Workspace"] = "ws_cfg", ["Hibira:LiveChat:IdentitySecret"] = "lcid_s",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddHibira();
        var chat = services.BuildServiceProvider().GetRequiredService<HibiraLiveChat>();
        Assert.Equal("ws_cfg", chat.Workspace);
        Assert.Contains("data-user-hash", chat.ScriptTag("1"));
    }
}
