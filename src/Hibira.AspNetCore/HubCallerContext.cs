using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Hibira.AspNetCore;

/// <summary>Who called a hub method.</summary>
public sealed class HubCallerContext
{
    private readonly HttpContext? _httpContext;

    internal HubCallerContext(string connectionId, ClaimsPrincipal user, string? userIdentifier, HttpContext? httpContext)
    {
        ConnectionId = connectionId;
        User = user;
        UserIdentifier = userIdentifier;
        _httpContext = httpContext;
        ConnectionAborted = httpContext?.RequestAborted ?? CancellationToken.None;
    }

    /// <summary>The caller's connection ID (its Hibira socket ID).</summary>
    public string ConnectionId { get; }

    /// <summary>The caller's user ID, from <see cref="HibiraHubOptions.UserIdProvider"/>.</summary>
    public string? UserIdentifier { get; }

    /// <summary>The caller's user.</summary>
    public ClaimsPrincipal User { get; }

    /// <summary>Data for the length of this call.</summary>
    public IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

    /// <summary>Canceled when the caller's request is aborted.</summary>
    public CancellationToken ConnectionAborted { get; }

    /// <summary>The HTTP request of this call; null when Hibira reported a disconnect by webhook.</summary>
    public HttpContext? GetHttpContext() => _httpContext;
}
