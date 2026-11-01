namespace Hibira.AspNetCore;

/// <summary>
/// A hub: public methods clients invoke, and <see cref="Clients"/> to send
/// to them. It mirrors SignalR's <c>Hub</c>: a new instance handles each call.
/// <code>
/// public class ChatHub : HibiraHub
/// {
///     public Task SendMessage(string user, string message) =>
///         Clients.All.SendAsync("ReceiveMessage", user, message);
/// }
/// </code>
/// </summary>
public abstract class HibiraHub : IDisposable
{
    private HubCallerContext? _context;
    private IHubCallerClients? _clients;
    private IGroupManager? _groups;
    private bool _disposed;

    /// <summary>Who called.</summary>
    public HubCallerContext Context
    {
        get => _context ?? throw new InvalidOperationException("Context is set when the hub handles a call.");
        set => _context = value;
    }

    /// <summary>The hub's clients, relative to the caller.</summary>
    public IHubCallerClients Clients
    {
        get => _clients ?? throw new InvalidOperationException("Clients is set when the hub handles a call.");
        set => _clients = value;
    }

    /// <summary>The hub's groups.</summary>
    public IGroupManager Groups
    {
        get => _groups ?? throw new InvalidOperationException("Groups is set when the hub handles a call.");
        set => _groups = value;
    }

    /// <summary>A client has connected (after its connection is set up, before its first call).</summary>
    public virtual Task OnConnectedAsync() => Task.CompletedTask;

    /// <summary>
    /// A client has disconnected: it stopped, or Hibira reported its
    /// connection gone by webhook (see MapHibiraWebhooks). Its groups are
    /// already removed.
    /// </summary>
    public virtual Task OnDisconnectedAsync(Exception? exception) => Task.CompletedTask;

    /// <summary>Releases resources.</summary>
    protected virtual void Dispose(bool disposing) { }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// A strongly typed hub: <see cref="Clients"/> returns <typeparamref name="T"/>,
/// whose methods (returning Task) send messages of the same name.
/// </summary>
public abstract class HibiraHub<T> : HibiraHub where T : class
{
    private IHubCallerClients<T>? _typed;

    /// <summary>The hub's clients, typed.</summary>
    public new IHubCallerClients<T> Clients
    {
        get => _typed ?? throw new InvalidOperationException("Clients is set when the hub handles a call.");
        set => _typed = value;
    }
}
