using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hibira.AspNetCore;

/// <summary>
/// Puts an endpoint (a controller, an action or a minimal API route) behind a
/// Hibira waiting room. Needs <c>app.UseHibiraWaitingRoom()</c> after routing.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireWaitingRoomAttribute : Attribute
{
    /// <summary>Protects the endpoint with <paramref name="room"/>.</summary>
    /// <param name="room">The waiting room name.</param>
    public RequireWaitingRoomAttribute(string room) => Room = room;

    /// <summary>The waiting room name.</summary>
    public string Room { get; }
}

/// <summary>Waiting room settings, under "Hibira:WaitingRoom".</summary>
public sealed class HibiraWaitingRoomOptions
{
    /// <summary>The hosted waiting page origin; null means the Hibira host (https://ws.hibira.io).</summary>
    public string? Host { get; set; }

    /// <summary>The waiting page language (en, ja, ko, zh-TW, ...); null follows the browser.</summary>
    public string? Lang { get; set; }

    /// <summary>The previous app secret, still accepted during a secret rotation.</summary>
    public string? PreviousSecret { get; set; }
}

/// <summary>Adds waiting rooms to the request pipeline.</summary>
public static class HibiraWaitingRoomExtensions
{
    /// <summary>The <see cref="HttpContext.Items"/> key of the <see cref="WaitingRoomPass"/>.</summary>
    public const string ItemKey = "hibira.pass";

    /// <summary>
    /// Enforces <see cref="RequireWaitingRoomAttribute"/> and
    /// <see cref="RequireWaitingRoom{TBuilder}"/> on endpoints. Call it after
    /// <c>UseRouting</c> (the default with WebApplication).
    /// </summary>
    /// <param name="app">The application.</param>
    public static IApplicationBuilder UseHibiraWaitingRoom(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var room = context.GetEndpoint()?.Metadata.GetMetadata<RequireWaitingRoomAttribute>()?.Room;
            if (room is null || await CheckAsync(context, room)) await next(context);
        });

    /// <summary>Puts every request whose path starts with <paramref name="pathPrefix"/> behind <paramref name="room"/>.</summary>
    /// <param name="app">The application.</param>
    /// <param name="pathPrefix">For example "/drop".</param>
    /// <param name="room">The waiting room name.</param>
    public static IApplicationBuilder UseHibiraWaitingRoom(this IApplicationBuilder app, PathString pathPrefix, string room) =>
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(pathPrefix) || await CheckAsync(context, room)) await next(context);
        });

    /// <summary>Puts this endpoint behind <paramref name="room"/>.</summary>
    /// <param name="builder">The endpoint convention builder.</param>
    /// <param name="room">The waiting room name.</param>
    public static TBuilder RequireWaitingRoom<TBuilder>(this TBuilder builder, string room) where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpoint => endpoint.Metadata.Add(new RequireWaitingRoomAttribute(room)));
        return builder;
    }

    /// <summary>The waiting room pass of an admitted request, or null.</summary>
    /// <param name="context">The request.</param>
    public static WaitingRoomPass? GetWaitingRoomPass(this HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var pass) ? pass as WaitingRoomPass : null;

    /// <summary>
    /// Checks the request. Returns true to let it through (the pass is then in
    /// <see cref="GetWaitingRoomPass"/>), or writes the redirect or 403 and
    /// returns false.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="room">The waiting room name.</param>
    public static async Task<bool> CheckAsync(HttpContext context, string room)
    {
        var hibira = context.RequestServices.GetRequiredService<IOptions<HibiraOptions>>().Value;
        var options = context.RequestServices.GetService<IOptions<HibiraWaitingRoomOptions>>()?.Value ?? new HibiraWaitingRoomOptions();
        if (string.IsNullOrEmpty(hibira.Secret))
            throw new InvalidOperationException("Hibira: waiting rooms need the app secret (Hibira:Secret).");
        var secrets = string.IsNullOrEmpty(options.PreviousSecret) ? new[] { hibira.Secret } : new[] { hibira.Secret, options.PreviousSecret! };
        var appId = string.IsNullOrEmpty(hibira.AppId) ? null : hibira.AppId;
        var cookieName = "hibira_wr_" + room;
        var request = context.Request;
        var response = context.Response;

        var kept = new QueryBuilder(request.Query
            .Where(q => q.Key != WaitingRoom.PassParameter)
            .SelectMany(q => q.Value.Select(v => new KeyValuePair<string, string>(q.Key, v ?? ""))));
        var clean = UriHelper.BuildAbsolute(request.Scheme, request.Host, request.PathBase, request.Path, kept.ToQueryString());

        if (request.Query.TryGetValue(WaitingRoom.PassParameter, out var fresh) && fresh.Count > 0)
        {
            var token = fresh.ToString();
            var claims = WaitingRoom.VerifyPass(token, secrets, room, appId);
            if (claims != null)
            {
                response.Cookies.Append(cookieName, token, new CookieOptions
                {
                    Path = "/",
                    MaxAge = TimeSpan.FromSeconds(Math.Max(0, claims.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds())),
                    HttpOnly = true,
                    Secure = request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                });
                response.Headers.CacheControl = "no-store";
                response.Redirect(clean);
                return false;
            }
        }

        var pass = WaitingRoom.VerifyPass(request.Cookies[cookieName], secrets, room, appId);
        if (pass != null)
        {
            context.Items[ItemKey] = pass;
            return true;
        }

        response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            await response.WriteAsJsonAsync(new { error = "waiting_room", message = "join the waiting room first" });
            return false;
        }
        var host = options.Host ?? (hibira.UseTls ? "https://" : "http://") + hibira.Host
            + (hibira.EffectivePort == (hibira.UseTls ? 443 : 80) ? "" : ":" + hibira.EffectivePort);
        response.Redirect(WaitingRoom.Url(hibira.Key, room, clean, host, options.Lang));
        return false;
    }
}
