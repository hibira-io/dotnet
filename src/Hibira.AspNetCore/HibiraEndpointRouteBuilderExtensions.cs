using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hibira.AspNetCore;

/// <summary>Maps hubs and the webhook endpoint.</summary>
public static class HibiraEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps a hub at <paramref name="pattern"/> (for example "/hubs/chat").
    /// Clients connect with the URL, as with SignalR. [Authorize] on the hub
    /// class applies to every endpoint; chain RequireAuthorization or
    /// RequireCors on the result for more.
    /// </summary>
    public static IEndpointConventionBuilder MapHibiraHub<THub>(this IEndpointRouteBuilder endpoints, string pattern) where THub : HibiraHub
    {
        var registry = endpoints.ServiceProvider.GetRequiredService<HubRegistry>();
        var hub = registry.Get(typeof(THub));
        var group = endpoints.MapGroup(pattern);
        foreach (var attr in typeof(THub).GetCustomAttributes(true).Where(a => a is IAuthorizeData or IAllowAnonymous))
            group.WithMetadata(attr);

        group.MapGet("/negotiate", (HttpContext http) => Negotiate(hub, http));
        group.MapPost("/auth", (Delegate)((HttpContext http) => AuthorizeAsync(hub, http)));
        group.MapPost("/user-auth", (Delegate)((HttpContext http) => UserAuthAsync(hub, http)));
        group.MapPost("/handshake", (Delegate)((HttpContext http) => HandshakeAsync(hub, http)));
        group.MapPost("/connect", (Delegate)((HttpContext http) => ConnectAsync(hub, http)));
        group.MapPost("/invoke", (Delegate)((HttpContext http) => InvokeAsync(hub, http)));
        group.MapPost("/disconnect", (Delegate)((HttpContext http) => DisconnectAsync(hub, http)));
        return group;
    }

    /// <summary>
    /// Maps the webhook endpoint (for example "/hibira/webhooks"). Add its URL
    /// in the Hibira portal with the channel_vacated event so hubs hear about
    /// connections that close without saying goodbye. <paramref name="onEvent"/>
    /// also receives every verified event.
    /// </summary>
    public static IEndpointConventionBuilder MapHibiraWebhooks(this IEndpointRouteBuilder endpoints, string pattern,
        Func<HibiraWebhookEvent, IServiceProvider, Task>? onEvent = null) =>
        endpoints.MapPost(pattern, (Delegate)((HttpContext http) => WebhookAsync(http, onEvent))).AllowAnonymous().DisableAntiforgery();

    private static IResult Negotiate(HubRuntime hub, HttpContext http)
    {
        var o = hub.Client.Options;
        return Results.Json(new
        {
            hub = hub.Channels.Hub,
            key = o.Key,
            host = o.Host,
            port = o.EffectivePort,
            useTls = o.UseTls,
            channel = hub.Channels.All,
            connectionChannelPrefix = hub.Channels.ConnectionPrefix,
            userId = hub.Options.UserIdProvider(http.User),
        });
    }

    private static async Task<IResult> AuthorizeAsync(HubRuntime hub, HttpContext http)
    {
        var (socketId, channel) = await ReadSocketAsync(http, "channel_name").ConfigureAwait(false);
        if (!HubChannels.IsSocketId(socketId) || string.IsNullOrEmpty(channel)) return Results.BadRequest(new { error = "socket_id and channel_name are required" });

        if (channel == hub.Channels.All) return Results.Json(hub.Client.AuthorizeChannel(socketId!, channel));
        if (hub.Channels.ConnectionOf(channel) is { } owner)
            return owner == socketId ? Results.Json(hub.Client.AuthorizeChannel(socketId!, channel)) : Forbidden("not your connection channel");
        if (hub.Channels.IsGroupChannel(channel))
        {
            if (!await hub.Store.IsInGroupAsync(hub.Channels.Hub, socketId!, channel, http.RequestAborted).ConfigureAwait(false))
                return Forbidden("not a member of this group");
            await hub.Store.MarkSubscribedAsync(hub.Channels.Hub, socketId!, channel, http.RequestAborted).ConfigureAwait(false);
            return Results.Json(hub.Client.AuthorizeChannel(socketId!, channel));
        }
        return Forbidden("not a channel of this hub");
    }

    private static async Task<IResult> UserAuthAsync(HubRuntime hub, HttpContext http)
    {
        var (socketId, _) = await ReadSocketAsync(http, null).ConfigureAwait(false);
        if (!HubChannels.IsSocketId(socketId)) return Results.BadRequest(new { error = "socket_id is required" });
        var userId = hub.Options.UserIdProvider(http.User);
        if (string.IsNullOrEmpty(userId)) return Forbidden("not signed in");
        return Results.Json(hub.Client.AuthenticateUser(socketId!, userId));
    }

    private static async Task<IResult> HandshakeAsync(HubRuntime hub, HttpContext http)
    {
        var body = await ReadJsonAsync(http).ConfigureAwait(false);
        var id = body?["connectionId"]?.GetValue<string>();
        if (!HubChannels.IsSocketId(id)) return Results.BadRequest(new { error = "connectionId is required" });
        var token = hub.Token(id!, hub.Options.UserIdProvider(http.User));
        // Only the socket subscribed to its own connection channel receives the token.
        await hub.Sender.SendControlAsync(id!, "hibira:handshake", new { token }, http.RequestAborted).ConfigureAwait(false);
        return Results.Json(new { });
    }

    private static async Task<IResult> ConnectAsync(HubRuntime hub, HttpContext http)
    {
        var (id, error) = await ReadCallerAsync(hub, http).ConfigureAwait(false);
        if (error != null) return error;
        await hub.Store.AddConnectionAsync(hub.Channels.Hub, id!, http.RequestAborted).ConfigureAwait(false);
        var instance = hub.CreateHub(http.RequestServices, id!, http.User, http);
        try
        {
            await instance.OnConnectedAsync().ConfigureAwait(false);
            return Results.Json(new { });
        }
        catch (Exception e)
        {
            await hub.Store.RemoveConnectionAsync(hub.Channels.Hub, id!, CancellationToken.None).ConfigureAwait(false);
            return Results.Json(new { error = hub.ErrorMessage("connecting", e) });
        }
        finally
        {
            await HubRuntime.DisposeAsync(instance).ConfigureAwait(false);
        }
    }

    private static async Task<IResult> InvokeAsync(HubRuntime hub, HttpContext http)
    {
        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "body must be JSON" });
        }
        using (doc)
        {
            var root = doc.RootElement;
            var id = root.TryGetProperty("connectionId", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var token = root.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var target = root.TryGetProperty("target", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            if (!hub.CheckToken(id ?? "", hub.Options.UserIdProvider(http.User), token))
                return Results.Json(new { error = "the connection is not valid for this user; start the connection again" }, statusCode: StatusCodes.Status401Unauthorized);
            if (string.IsNullOrEmpty(target)) return Results.BadRequest(new { error = "target is required" });
            root.TryGetProperty("arguments", out var args);
            var result = await hub.InvokeAsync(http, id!, target, args).ConfigureAwait(false);
            if (!result.Success) return Results.Json(new { error = result.Error });
            var response = new JsonObject();
            if (result.HasValue) response["result"] = JsonSerializer.SerializeToNode(result.Value, hub.Options.JsonSerializerOptions);
            return Results.Text(response.ToJsonString(), "application/json");
        }
    }

    private static async Task<IResult> DisconnectAsync(HubRuntime hub, HttpContext http)
    {
        var (id, error) = await ReadCallerAsync(hub, http).ConfigureAwait(false);
        if (error != null) return error;
        await DisconnectedAsync(hub, http.RequestServices, id!, http.User, http).ConfigureAwait(false);
        return Results.Json(new { });
    }

    internal static async Task DisconnectedAsync(HubRuntime hub, IServiceProvider services, string id, ClaimsPrincipal user, HttpContext? http)
    {
        if (!await hub.Store.RemoveConnectionAsync(hub.Channels.Hub, id, CancellationToken.None).ConfigureAwait(false)) return;
        var instance = hub.CreateHub(services, id, user, http);
        try
        {
            await instance.OnDisconnectedAsync(null).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            hub.ErrorMessage("disconnecting", e);
        }
        finally
        {
            await HubRuntime.DisposeAsync(instance).ConfigureAwait(false);
        }
    }

    private static async Task<IResult> WebhookAsync(HttpContext http, Func<HibiraWebhookEvent, IServiceProvider, Task>? onEvent)
    {
        var client = http.RequestServices.GetRequiredService<IHibiraClient>();
        var registry = http.RequestServices.GetRequiredService<HubRegistry>();
        using var ms = new MemoryStream();
        await http.Request.Body.CopyToAsync(ms, http.RequestAborted).ConfigureAwait(false);
        var webhook = client.VerifyWebhook(http.Request.Headers["X-Pusher-Key"], http.Request.Headers["X-Pusher-Signature"],
            http.Request.Headers["X-Hibira-Previous-Signature"], ms.ToArray());
        if (webhook == null) return Results.Unauthorized();

        using var scope = http.RequestServices.CreateScope();
        foreach (var e in webhook.Events)
        {
            if (e.Name == "channel_vacated" && registry.FindConnectionChannel(e.Channel) is { } conn)
                await DisconnectedAsync(conn.Hub, scope.ServiceProvider, conn.ConnectionId, new ClaimsPrincipal(new ClaimsIdentity()), null).ConfigureAwait(false);
            if (onEvent != null) await onEvent(e, scope.ServiceProvider).ConfigureAwait(false);
        }
        return Results.Ok();
    }

    private static async Task<(string? Id, IResult? Error)> ReadCallerAsync(HubRuntime hub, HttpContext http)
    {
        var body = await ReadJsonAsync(http).ConfigureAwait(false);
        var id = body?["connectionId"]?.GetValue<string>();
        var token = body?["token"]?.GetValue<string>();
        if (!hub.CheckToken(id ?? "", hub.Options.UserIdProvider(http.User), token))
            return (null, Results.Json(new { error = "the connection is not valid for this user; start the connection again" }, statusCode: StatusCodes.Status401Unauthorized));
        return (id, null);
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpContext http)
    {
        try
        {
            return await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<(string? SocketId, string? Channel)> ReadSocketAsync(HttpContext http, string? channelField)
    {
        if (http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync(http.RequestAborted).ConfigureAwait(false);
            return (form["socket_id"].ToString(), channelField == null ? null : form[channelField].ToString());
        }
        var json = await ReadJsonAsync(http).ConfigureAwait(false);
        return (json?["socket_id"]?.ToString(), channelField == null ? null : json?[channelField]?.ToString());
    }

    private static IResult Forbidden(string message) => Results.Json(new { error = message }, statusCode: StatusCodes.Status403Forbidden);
}
