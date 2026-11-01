using System.Security.Claims;
using System.Text.Json;

namespace Hibira.AspNetCore;

/// <summary>Options shared by every <see cref="HibiraHub"/>.</summary>
public sealed class HibiraHubOptions
{
    /// <summary>
    /// How hub arguments, return values and messages are written. Defaults to
    /// the web defaults (camelCase), like SignalR's JSON protocol.
    /// </summary>
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);

    /// <summary>Send exception messages to callers. Turn on only while developing.</summary>
    public bool EnableDetailedErrors { get; set; }

    /// <summary>
    /// How long <see cref="IGroupManager.AddToGroupAsync"/> waits for the
    /// connection to subscribe to the group's channel, so a message sent to
    /// the group right after reaches it. Zero does not wait.
    /// </summary>
    public TimeSpan GroupJoinTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a connection's group memberships are kept without any
    /// activity when its disconnect is never reported (no webhook, closed
    /// browser).
    /// </summary>
    public TimeSpan ConnectionIdleTimeout { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// The user ID of a request, used for <see cref="HubCallerContext.UserIdentifier"/>
    /// and Clients.User. Defaults to the NameIdentifier claim, as in SignalR.
    /// </summary>
    public Func<ClaimsPrincipal, string?> UserIdProvider { get; set; } =
        user => user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}
