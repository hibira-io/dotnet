using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace Hibira.AspNetCore;

/// <summary>
/// Remembers which hub connections are connected and which group channels
/// each may subscribe to. The default keeps them in memory, which suits one
/// server; with several servers call <c>AddHibira().AddDistributedConnectionStore()</c>
/// over an IDistributedCache such as Redis.
/// </summary>
public interface IHibiraConnectionStore
{
    /// <summary>Records a connection as connected.</summary>
    Task AddConnectionAsync(string hub, string connectionId, CancellationToken cancellationToken = default);

    /// <summary>Whether the connection is connected.</summary>
    Task<bool> IsConnectedAsync(string hub, string connectionId, CancellationToken cancellationToken = default);

    /// <summary>Forgets a connection and its groups. Returns false if it was not connected.</summary>
    Task<bool> RemoveConnectionAsync(string hub, string connectionId, CancellationToken cancellationToken = default);

    /// <summary>Lets the connection subscribe to a group channel.</summary>
    Task AddToGroupAsync(string hub, string connectionId, string channel, CancellationToken cancellationToken = default);

    /// <summary>Takes a group channel away from the connection.</summary>
    Task RemoveFromGroupAsync(string hub, string connectionId, string channel, CancellationToken cancellationToken = default);

    /// <summary>Whether the connection may subscribe to the group channel.</summary>
    Task<bool> IsInGroupAsync(string hub, string connectionId, string channel, CancellationToken cancellationToken = default);

    /// <summary>Records that the connection asked to subscribe to the group channel.</summary>
    Task MarkSubscribedAsync(string hub, string connectionId, string channel, CancellationToken cancellationToken = default);

    /// <summary>Whether the connection asked to subscribe to the group channel since it was added.</summary>
    Task<bool> IsSubscribedAsync(string hub, string connectionId, string channel, CancellationToken cancellationToken = default);
}

/// <summary>The in-memory <see cref="IHibiraConnectionStore"/>, for a single server.</summary>
public sealed class MemoryHibiraConnectionStore : IHibiraConnectionStore
{
    private sealed class Entry
    {
        public bool Connected;
        public readonly Dictionary<string, bool> Groups = new(StringComparer.Ordinal);
        public DateTime Touched = DateTime.UtcNow;
    }

    private readonly ConcurrentDictionary<(string, string), Entry> _entries = new();
    private readonly TimeSpan _idle;
    private DateTime _nextSweep = DateTime.UtcNow;

    /// <summary>Creates the store.</summary>
    public MemoryHibiraConnectionStore(IOptions<HibiraHubOptions> options) => _idle = options.Value.ConnectionIdleTimeout;

    internal int Count => _entries.Count;

    private Entry Get(string hub, string id)
    {
        Sweep();
        var e = _entries.GetOrAdd((hub, id), _ => new Entry());
        lock (e) e.Touched = DateTime.UtcNow;
        return e;
    }

    private bool TryGet(string hub, string id, out Entry e)
    {
        Sweep();
        if (!_entries.TryGetValue((hub, id), out e!)) return false;
        lock (e) e.Touched = DateTime.UtcNow;
        return true;
    }

    private void Sweep()
    {
        var now = DateTime.UtcNow;
        if (now < _nextSweep) return;
        _nextSweep = now.AddMinutes(1);
        foreach (var (key, e) in _entries)
            if (now - e.Touched > _idle) _entries.TryRemove(key, out _);
    }

    /// <inheritdoc />
    public Task AddConnectionAsync(string hub, string connectionId, CancellationToken cancellationToken = default)
    {
        var e = Get(hub, connectionId);
        lock (e) e.Connected = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> IsConnectedAsync(string hub, string connectionId, CancellationToken cancellationToken = default)
    {
        if (!TryGet(hub, connectionId, out var e)) return Task.FromResult(false);
        lock (e) return Task.FromResult(e.Connected);
    }

    /// <inheritdoc />
    public Task<bool> RemoveConnectionAsync(string hub, string connectionId, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryRemove((hub, connectionId), out var e)) return Task.FromResult(false);
        lock (e) return Task.FromResult(e.Connected);
    }

    /// <inheritdoc />
    public Task AddToGroupAsync(string hub, string connectionId, string channel, CancellationToken cancellationToken = default)
    {
        var e = Get(hub, connectionId);
        lock (e) e.Groups[channel] = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveFromGroupAsync(string hub, string connectionId, string channel, CancellationToken cancellationToken = default)
    {
        if (TryGet(hub, connectionId, out var e))
            lock (e) e.Groups.Remove(channel);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> IsInGroupAsync(string hub, string connectionId, string channel, CancellationToken cancellationToken = default)
    {
        if (!TryGet(hub, connectionId, out var e)) return Task.FromResult(false);
        lock (e) return Task.FromResult(e.Groups.ContainsKey(channel));
    }

    /// <inheritdoc />
    public Task MarkSubscribedAsync(string hub, string connectionId, string channel, CancellationToken cancellationToken = default)
    {
        if (TryGet(hub, connectionId, out var e))
            lock (e)
                if (e.Groups.ContainsKey(channel)) e.Groups[channel] = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> IsSubscribedAsync(string hub, string connectionId, string channel, CancellationToken cancellationToken = default)
    {
        if (!TryGet(hub, connectionId, out var e)) return Task.FromResult(false);
        lock (e) return Task.FromResult(e.Groups.TryGetValue(channel, out var s) && s);
    }
}

/// <summary>
/// An <see cref="IHibiraConnectionStore"/> over IDistributedCache (Redis, SQL
/// Server, ...), for apps on several servers.
/// </summary>
public sealed class DistributedHibiraConnectionStore : IHibiraConnectionStore
{
    private readonly IDistributedCache _cache;
    private readonly DistributedCacheEntryOptions _entry;

    /// <summary>Creates the store.</summary>
    public DistributedHibiraConnectionStore(IDistributedCache cache, IOptions<HibiraHubOptions> options)
    {
        _cache = cache;
        _entry = new DistributedCacheEntryOptions { SlidingExpiration = options.Value.ConnectionIdleTimeout };
    }

    private static string ConnKey(string hub, string id) => $"hibira:{hub}:c:{id}";
    private static string MemberKey(string hub, string id, string channel) => $"hibira:{hub}:m:{id}:{channel}";

    /// <inheritdoc />
    public Task AddConnectionAsync(string hub, string connectionId, CancellationToken cancellationToken = default) =>
        _cache.SetStringAsync(ConnKey(hub, connectionId), "[]", _entry, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> IsConnectedAsync(string hub, string connectionId, CancellationToken cancellationToken = default) =>
        await _cache.GetStringAsync(ConnKey(hub, connectionId), cancellationToken).ConfigureAwait(false) != null;

    /// <inheritdoc />
    public async Task<bool> RemoveConnectionAsync(string hub, string connectionId, CancellationToken cancellationToken = default)
    {
        var key = ConnKey(hub, connectionId);
        var groups = await _cache.GetStringAsync(key, cancellationToken).ConfigureAwait(false);
        if (groups == null) return false;
        await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        foreach (var channel in JsonSerializer.Deserialize<string[]>(groups) ?? Array.Empty<string>())
            await _cache.RemoveAsync(MemberKey(hub, connectionId, channel), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task AddToGroupAsync(string hub, string connectionId, string channel, CancellationToken cancellationToken = default)
    {
        await _cache.SetStringAsync(MemberKey(hub, connectionId, channel), "0", _entry, cancellationToken).ConfigureAwait(false);
        await EditGroupsAsync(hub, connectionId, g => g.Add(channel), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveFromGroupAsync(string hub, string connectionId, string channel, CancellationToken cancellationToken = default)
    {
        await _cache.RemoveAsync(MemberKey(hub, connectionId, channel), cancellationToken).ConfigureAwait(false);
        await EditGroupsAsync(hub, connectionId, g => g.Remove(channel), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsInGroupAsync(string hub, string connectionId, string channel, CancellationToken cancellationToken = default) =>
        await _cache.GetStringAsync(MemberKey(hub, connectionId, channel), cancellationToken).ConfigureAwait(false) != null;

    /// <inheritdoc />
    public async Task MarkSubscribedAsync(string hub, string connectionId, string channel, CancellationToken cancellationToken = default)
    {
        var key = MemberKey(hub, connectionId, channel);
        if (await _cache.GetStringAsync(key, cancellationToken).ConfigureAwait(false) != null)
            await _cache.SetStringAsync(key, "1", _entry, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsSubscribedAsync(string hub, string connectionId, string channel, CancellationToken cancellationToken = default) =>
        await _cache.GetStringAsync(MemberKey(hub, connectionId, channel), cancellationToken).ConfigureAwait(false) == "1";

    private async Task EditGroupsAsync(string hub, string connectionId, Action<HashSet<string>> edit, CancellationToken ct)
    {
        var key = ConnKey(hub, connectionId);
        var current = await _cache.GetStringAsync(key, ct).ConfigureAwait(false);
        if (current == null) return;
        var set = new HashSet<string>(JsonSerializer.Deserialize<string[]>(current) ?? Array.Empty<string>(), StringComparer.Ordinal);
        edit(set);
        await _cache.SetStringAsync(key, JsonSerializer.Serialize(set), _entry, ct).ConfigureAwait(false);
    }
}
