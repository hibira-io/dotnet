using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hibira.AspNetCore;

/// <summary>Where a message goes: channels, minus at most one connection.</summary>
internal readonly record struct HubTarget(IReadOnlyList<string> Channels, string? ExcludeConnectionId);

/// <summary>Turns hub messages into Hibira events.</summary>
internal sealed class HubSender
{
    private readonly IHibiraClient _client;
    private readonly HibiraHubOptions _options;

    public HubSender(HubChannels channels, IHibiraClient client, HibiraHubOptions options)
    {
        Channels = channels;
        _client = client;
        _options = options;
    }

    public HubChannels Channels { get; }
    public IHibiraClient Client => _client;
    public HibiraHubOptions Options => _options;

    public Task SendAsync(HubTarget target, string method, object?[] args, CancellationToken ct)
    {
        ValidateMethod(method);
        if (target.Channels.Count == 0) return Task.CompletedTask;
        var payload = new JsonObject
        {
            ["hub"] = Channels.Hub,
            ["arguments"] = JsonSerializer.SerializeToNode(args ?? Array.Empty<object?>(), _options.JsonSerializerOptions),
        };
        return _client.TriggerAsync(target.Channels, method, payload.ToJsonString(), target.ExcludeConnectionId, ct);
    }

    public Task SendControlAsync(string connectionId, string name, object data, CancellationToken ct) =>
        _client.TriggerAsync(Channels.Connection(connectionId), name, JsonSerializer.Serialize(data, HibiraJsonWeb), null, ct);

    private static readonly JsonSerializerOptions HibiraJsonWeb = new(JsonSerializerDefaults.Web);

    internal static void ValidateMethod(string method)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        if (method.Length > 200 || method.StartsWith("pusher", StringComparison.Ordinal) ||
            method.StartsWith("hibira:", StringComparison.Ordinal) || method.StartsWith("client-", StringComparison.Ordinal))
            throw new ArgumentException($"\"{method}\" can't be a hub method name: names starting with pusher, hibira: or client- are reserved.", nameof(method));
    }
}

internal sealed class ClientProxy : IClientProxy
{
    private readonly HubSender _sender;
    private readonly HubTarget _target;

    public ClientProxy(HubSender sender, HubTarget target)
    {
        _sender = sender;
        _target = target;
    }

    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
        _sender.SendAsync(_target, method, args, cancellationToken);
}

/// <summary>Implements T by sending each call as a hub message of the method's name.</summary>
public class TypedClientProxy : DispatchProxy
{
    private IClientProxy _proxy = null!;

    internal static T Create<T>(IClientProxy proxy) where T : class
    {
        if (!typeof(T).IsInterface) throw new InvalidOperationException($"Hibira: typed hub clients must be interfaces ({typeof(T).Name} is not).");
        foreach (var m in typeof(T).GetMethods().Concat(typeof(T).GetInterfaces().SelectMany(i => i.GetMethods())))
            if (m.ReturnType != typeof(Task))
                throw new InvalidOperationException($"Hibira: {typeof(T).Name}.{m.Name} must return Task.");
        var t = Create<T, TypedClientProxy>();
        ((TypedClientProxy)(object)t)._proxy = proxy;
        return t;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= Array.Empty<object?>();
        var ct = CancellationToken.None;
        var ps = targetMethod.GetParameters();
        if (ps.Length > 0 && ps[^1].ParameterType == typeof(CancellationToken))
        {
            ct = (CancellationToken)(args[^1] ?? CancellationToken.None);
            args = args[..^1];
        }
        var name = targetMethod.GetCustomAttribute<HubMethodNameAttribute>()?.Name ?? targetMethod.Name;
        return _proxy.SendCoreAsync(name, args, ct);
    }
}

internal class HubClientsBase<T> : IHubClients<T>
{
    protected readonly HubSender Sender;
    private readonly Func<IClientProxy, T> _wrap;

    public HubClientsBase(HubSender sender, Func<IClientProxy, T> wrap)
    {
        Sender = sender;
        _wrap = wrap;
    }

    protected T To(IReadOnlyList<string> channels, string? exclude = null) => _wrap(new ClientProxy(Sender, new HubTarget(channels, exclude)));

    protected static string? OneExcluded(IReadOnlyList<string> ids)
    {
        if (ids.Count > 1)
            throw new NotSupportedException("Hibira can leave out one connection per message. Send to a group or to the connections you want instead.");
        return ids.Count == 1 ? ids[0] : null;
    }

    public T All => To(new[] { Sender.Channels.All });
    public T AllExcept(IReadOnlyList<string> excludedConnectionIds) => To(new[] { Sender.Channels.All }, OneExcluded(excludedConnectionIds));
    public T Client(string connectionId) => To(new[] { Sender.Channels.Connection(connectionId) });
    public T Clients(IReadOnlyList<string> connectionIds) => To(connectionIds.Select(Sender.Channels.Connection).ToList());
    public T Group(string groupName) => To(new[] { Sender.Channels.Group(groupName) });
    public T GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => To(new[] { Sender.Channels.Group(groupName) }, OneExcluded(excludedConnectionIds));
    public T Groups(IReadOnlyList<string> groupNames) => To(groupNames.Select(Sender.Channels.Group).ToList());
    public T User(string userId) => To(new[] { HibiraChannelName.ForUser(userId) });
    public T Users(IReadOnlyList<string> userIds) => To(userIds.Select(HibiraChannelName.ForUser).ToList());
}

internal class HubCallerClientsBase<T> : HubClientsBase<T>, IHubCallerClients<T>
{
    private readonly string _caller;

    public HubCallerClientsBase(HubSender sender, Func<IClientProxy, T> wrap, string callerConnectionId) : base(sender, wrap) =>
        _caller = callerConnectionId;

    public T Caller => Client(_caller);
    public T Others => To(new[] { Sender.Channels.All }, _caller);
    public T OthersInGroup(string groupName) => To(new[] { Sender.Channels.Group(groupName) }, _caller);
}

internal sealed class HubClients : HubClientsBase<IClientProxy>, IHubClients
{
    public HubClients(HubSender sender) : base(sender, p => p) { }
}

internal sealed class HubCallerClients : HubCallerClientsBase<IClientProxy>, IHubCallerClients
{
    public HubCallerClients(HubSender sender, string caller) : base(sender, p => p, caller) { }
}
