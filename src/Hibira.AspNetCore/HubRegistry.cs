using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hibira.AspNetCore;

/// <summary>The hubs of the app, by type and by name.</summary>
internal sealed class HubRegistry
{
    private readonly ConcurrentDictionary<Type, HubRuntime> _byType = new();
    private readonly ConcurrentDictionary<string, HubRuntime> _byName = new(StringComparer.Ordinal);
    private readonly IHibiraClient _client;
    private readonly HibiraHubOptions _options;
    private readonly IHibiraConnectionStore _store;
    private readonly ILoggerFactory _loggers;

    public HubRegistry(IHibiraClient client, IOptions<HibiraHubOptions> options, IHibiraConnectionStore store, ILoggerFactory loggers)
    {
        _client = client;
        _options = options.Value;
        _store = store;
        _loggers = loggers;
    }

    public IEnumerable<HubRuntime> All => _byName.Values;

    public HubRuntime Get(Type hubType, string? name = null)
    {
        if (_byType.TryGetValue(hubType, out var existing)) return existing;
        var hubName = HubChannels.NameOf(hubType, name);
        var runtime = _byType.GetOrAdd(hubType, t => new HubRuntime(t, hubName, _client, _options, _store, _loggers));
        if (!_byName.TryAdd(runtime.Channels.Hub, runtime) && _byName[runtime.Channels.Hub] != runtime)
            throw new InvalidOperationException($"Hibira: two hubs are named \"{runtime.Channels.Hub}\". Rename one with [HibiraHubName].");
        return runtime;
    }

    /// <summary>The hub and connection ID of a connection channel, if it is one.</summary>
    public (HubRuntime Hub, string ConnectionId)? FindConnectionChannel(string channel)
    {
        if (!channel.StartsWith(HubChannels.ChannelRoot, StringComparison.Ordinal)) return null;
        foreach (var hub in _byName.Values)
            if (hub.Channels.ConnectionOf(channel) is { } id) return (hub, id);
        return null;
    }
}

internal sealed class HubContext<THub> : IHubContext<THub> where THub : HibiraHub
{
    public HubContext(HubRegistry registry)
    {
        var runtime = registry.Get(typeof(THub));
        Clients = new HubClients(runtime.Sender);
        Groups = runtime.Groups;
    }

    public IHubClients Clients { get; }
    public IGroupManager Groups { get; }
}

internal sealed class HubContext<THub, T> : IHubContext<THub, T> where THub : HibiraHub<T> where T : class
{
    public HubContext(HubRegistry registry)
    {
        var runtime = registry.Get(typeof(THub));
        Clients = HubRuntime.MakeTypedHubClients<T>(runtime.Sender);
        Groups = runtime.Groups;
    }

    public IHubClients<T> Clients { get; }
    public IGroupManager Groups { get; }
}
