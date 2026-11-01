namespace Hibira.Client;

/// <summary>On, InvokeAsync and SendAsync overloads, as in the SignalR client.</summary>
public static class HubConnectionExtensions
{
    /// <summary>Handles <paramref name="methodName"/> with no arguments.</summary>
    public static IDisposable On(this HubConnection c, string methodName, Action handler) =>
        c.On(methodName, Type.EmptyTypes, _ => { handler(); return Task.CompletedTask; });

    /// <summary>Handles <paramref name="methodName"/> with one argument.</summary>
    public static IDisposable On<T1>(this HubConnection c, string methodName, Action<T1> handler) =>
        c.On(methodName, new[] { typeof(T1) }, a => { handler((T1)a[0]!); return Task.CompletedTask; });

    /// <summary>Handles <paramref name="methodName"/> with two arguments.</summary>
    public static IDisposable On<T1, T2>(this HubConnection c, string methodName, Action<T1, T2> handler) =>
        c.On(methodName, new[] { typeof(T1), typeof(T2) }, a => { handler((T1)a[0]!, (T2)a[1]!); return Task.CompletedTask; });

    /// <summary>Handles <paramref name="methodName"/> with three arguments.</summary>
    public static IDisposable On<T1, T2, T3>(this HubConnection c, string methodName, Action<T1, T2, T3> handler) =>
        c.On(methodName, new[] { typeof(T1), typeof(T2), typeof(T3) }, a => { handler((T1)a[0]!, (T2)a[1]!, (T3)a[2]!); return Task.CompletedTask; });

    /// <summary>Handles <paramref name="methodName"/> with four arguments.</summary>
    public static IDisposable On<T1, T2, T3, T4>(this HubConnection c, string methodName, Action<T1, T2, T3, T4> handler) =>
        c.On(methodName, new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) }, a => { handler((T1)a[0]!, (T2)a[1]!, (T3)a[2]!, (T4)a[3]!); return Task.CompletedTask; });

    /// <summary>Handles <paramref name="methodName"/> asynchronously with no arguments.</summary>
    public static IDisposable On(this HubConnection c, string methodName, Func<Task> handler) =>
        c.On(methodName, Type.EmptyTypes, _ => handler());

    /// <summary>Handles <paramref name="methodName"/> asynchronously with one argument.</summary>
    public static IDisposable On<T1>(this HubConnection c, string methodName, Func<T1, Task> handler) =>
        c.On(methodName, new[] { typeof(T1) }, a => handler((T1)a[0]!));

    /// <summary>Handles <paramref name="methodName"/> asynchronously with two arguments.</summary>
    public static IDisposable On<T1, T2>(this HubConnection c, string methodName, Func<T1, T2, Task> handler) =>
        c.On(methodName, new[] { typeof(T1), typeof(T2) }, a => handler((T1)a[0]!, (T2)a[1]!));

    /// <summary>Handles <paramref name="methodName"/> asynchronously with three arguments.</summary>
    public static IDisposable On<T1, T2, T3>(this HubConnection c, string methodName, Func<T1, T2, T3, Task> handler) =>
        c.On(methodName, new[] { typeof(T1), typeof(T2), typeof(T3) }, a => handler((T1)a[0]!, (T2)a[1]!, (T3)a[2]!));

    /// <summary>
    /// Calls a hub method and returns its result. A CancellationToken passed
    /// as the last argument cancels the call instead of being sent.
    /// </summary>
    public static async Task<TResult> InvokeAsync<TResult>(this HubConnection c, string methodName, params object?[] args)
    {
        var (a, ct) = Split(args);
        return (TResult)(await c.InvokeCoreAsync(methodName, typeof(TResult), a, ct).ConfigureAwait(false))!;
    }

    /// <summary>Calls a hub method and waits for it to finish.</summary>
    public static Task InvokeAsync(this HubConnection c, string methodName, params object?[] args)
    {
        var (a, ct) = Split(args);
        return c.InvokeCoreAsync(methodName, typeof(void), a, ct);
    }

    /// <summary>Calls a hub method.</summary>
    public static Task SendAsync(this HubConnection c, string methodName, params object?[] args)
    {
        var (a, ct) = Split(args);
        return c.SendCoreAsync(methodName, a, ct);
    }

    private static (object?[], CancellationToken) Split(object?[]? args)
    {
        args ??= Array.Empty<object?>();
        return args.Length > 0 && args[^1] is CancellationToken ct ? (args[..^1], ct) : (args, CancellationToken.None);
    }
}
