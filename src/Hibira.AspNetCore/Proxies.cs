namespace Hibira.AspNetCore;

/// <summary>Sends hub messages to a set of connections.</summary>
public interface IClientProxy
{
    /// <summary>
    /// Calls <paramref name="method"/> on the connections with the given
    /// arguments. The task completes when Hibira has accepted the message.
    /// </summary>
    Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default);
}

/// <summary>Picks connections of a hub. Mirrors SignalR's IHubClients&lt;T&gt;.</summary>
public interface IHubClients<T>
{
    /// <summary>Every connection of the hub.</summary>
    T All { get; }
    /// <summary>Every connection except these. Hibira can leave out one connection per message.</summary>
    T AllExcept(IReadOnlyList<string> excludedConnectionIds);
    /// <summary>One connection.</summary>
    T Client(string connectionId);
    /// <summary>These connections.</summary>
    T Clients(IReadOnlyList<string> connectionIds);
    /// <summary>The connections in a group.</summary>
    T Group(string groupName);
    /// <summary>The connections in a group except these (at most one).</summary>
    T GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds);
    /// <summary>The connections in these groups.</summary>
    T Groups(IReadOnlyList<string> groupNames);
    /// <summary>Every connection signed in as the user.</summary>
    T User(string userId);
    /// <summary>Every connection signed in as these users.</summary>
    T Users(IReadOnlyList<string> userIds);
}

/// <summary>Picks connections from inside a hub method, relative to the caller.</summary>
public interface IHubCallerClients<T> : IHubClients<T>
{
    /// <summary>The connection that called the method.</summary>
    T Caller { get; }
    /// <summary>Every connection except the caller.</summary>
    T Others { get; }
    /// <summary>The connections in a group except the caller.</summary>
    T OthersInGroup(string groupName);
}

/// <summary>Picks connections of a hub.</summary>
public interface IHubClients : IHubClients<IClientProxy> { }

/// <summary>Picks connections from inside a hub method.</summary>
public interface IHubCallerClients : IHubCallerClients<IClientProxy>, IHubClients { }

/// <summary>Adds and removes connections from groups.</summary>
public interface IGroupManager
{
    /// <summary>
    /// Adds a connection to a group. The connection's client subscribes to the
    /// group's channel; the task waits for that (up to
    /// <see cref="HibiraHubOptions.GroupJoinTimeout"/>) so the next message to
    /// the group reaches it.
    /// </summary>
    Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default);

    /// <summary>Removes a connection from a group.</summary>
    Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default);
}

/// <summary>Sends to a hub's clients from outside the hub (controllers, background services).</summary>
public interface IHubContext<THub> where THub : HibiraHub
{
    /// <summary>The hub's clients.</summary>
    IHubClients Clients { get; }
    /// <summary>The hub's groups.</summary>
    IGroupManager Groups { get; }
}

/// <summary>Sends to a strongly typed hub's clients from outside the hub.</summary>
public interface IHubContext<THub, T> where THub : HibiraHub<T> where T : class
{
    /// <summary>The hub's clients.</summary>
    IHubClients<T> Clients { get; }
    /// <summary>The hub's groups.</summary>
    IGroupManager Groups { get; }
}

/// <summary>SendAsync overloads, as in SignalR.</summary>
public static class ClientProxyExtensions
{
    /// <summary>Calls <paramref name="method"/> with no arguments.</summary>
    public static Task SendAsync(this IClientProxy clientProxy, string method, CancellationToken cancellationToken = default) =>
        clientProxy.SendCoreAsync(method, Array.Empty<object?>(), cancellationToken);
    /// <summary>Calls <paramref name="method"/> with one argument.</summary>
    public static Task SendAsync(this IClientProxy clientProxy, string method, object? arg1, CancellationToken cancellationToken = default) =>
        clientProxy.SendCoreAsync(method, new[] { arg1 }, cancellationToken);
    /// <summary>Calls <paramref name="method"/> with two arguments.</summary>
    public static Task SendAsync(this IClientProxy clientProxy, string method, object? arg1, object? arg2, CancellationToken cancellationToken = default) =>
        clientProxy.SendCoreAsync(method, new[] { arg1, arg2 }, cancellationToken);
    /// <summary>Calls <paramref name="method"/> with three arguments.</summary>
    public static Task SendAsync(this IClientProxy clientProxy, string method, object? arg1, object? arg2, object? arg3, CancellationToken cancellationToken = default) =>
        clientProxy.SendCoreAsync(method, new[] { arg1, arg2, arg3 }, cancellationToken);
    /// <summary>Calls <paramref name="method"/> with four arguments.</summary>
    public static Task SendAsync(this IClientProxy clientProxy, string method, object? arg1, object? arg2, object? arg3, object? arg4, CancellationToken cancellationToken = default) =>
        clientProxy.SendCoreAsync(method, new[] { arg1, arg2, arg3, arg4 }, cancellationToken);
    /// <summary>Calls <paramref name="method"/> with five arguments.</summary>
    public static Task SendAsync(this IClientProxy clientProxy, string method, object? arg1, object? arg2, object? arg3, object? arg4, object? arg5, CancellationToken cancellationToken = default) =>
        clientProxy.SendCoreAsync(method, new[] { arg1, arg2, arg3, arg4, arg5 }, cancellationToken);
    /// <summary>Calls <paramref name="method"/> with six arguments.</summary>
    public static Task SendAsync(this IClientProxy clientProxy, string method, object? arg1, object? arg2, object? arg3, object? arg4, object? arg5, object? arg6, CancellationToken cancellationToken = default) =>
        clientProxy.SendCoreAsync(method, new[] { arg1, arg2, arg3, arg4, arg5, arg6 }, cancellationToken);
    /// <summary>Calls <paramref name="method"/> with seven arguments.</summary>
    public static Task SendAsync(this IClientProxy clientProxy, string method, object? arg1, object? arg2, object? arg3, object? arg4, object? arg5, object? arg6, object? arg7, CancellationToken cancellationToken = default) =>
        clientProxy.SendCoreAsync(method, new[] { arg1, arg2, arg3, arg4, arg5, arg6, arg7 }, cancellationToken);
    /// <summary>Calls <paramref name="method"/> with eight arguments.</summary>
    public static Task SendAsync(this IClientProxy clientProxy, string method, object? arg1, object? arg2, object? arg3, object? arg4, object? arg5, object? arg6, object? arg7, object? arg8, CancellationToken cancellationToken = default) =>
        clientProxy.SendCoreAsync(method, new[] { arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8 }, cancellationToken);
}
