using Microsoft.Extensions.Logging;

namespace Hibira.AspNetCore;

internal sealed class GroupManager : IGroupManager
{
    private readonly HubSender _sender;
    private readonly IHibiraConnectionStore _store;
    private readonly ILogger _logger;

    public GroupManager(HubSender sender, IHibiraConnectionStore store, ILogger logger)
    {
        _sender = sender;
        _store = store;
        _logger = logger;
    }

    public async Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        var hub = _sender.Channels.Hub;
        var channel = _sender.Channels.Group(groupName);
        await _store.AddToGroupAsync(hub, connectionId, channel, cancellationToken).ConfigureAwait(false);
        await _sender.SendControlAsync(connectionId, "hibira:join", new { group = groupName, channel }, cancellationToken).ConfigureAwait(false);

        var timeout = _sender.Options.GroupJoinTimeout;
        if (timeout <= TimeSpan.Zero || !await _store.IsConnectedAsync(hub, connectionId, cancellationToken).ConfigureAwait(false)) return;
        var deadline = DateTime.UtcNow + timeout;
        var delay = 20;
        while (DateTime.UtcNow < deadline)
        {
            if (await _store.IsSubscribedAsync(hub, connectionId, channel, cancellationToken).ConfigureAwait(false))
            {
                // The subscription is signed; give Hibira a moment to finish it.
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                return;
            }
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = Math.Min(delay * 2, 200);
        }
        _logger.LogWarning("Hibira: connection {ConnectionId} did not subscribe to group {Group} of hub {Hub} within {Timeout}.",
            connectionId, groupName, hub, timeout);
    }

    public async Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        var channel = _sender.Channels.Group(groupName);
        await _store.RemoveFromGroupAsync(_sender.Channels.Hub, connectionId, channel, cancellationToken).ConfigureAwait(false);
        await _sender.SendControlAsync(connectionId, "hibira:leave", new { group = groupName, channel }, cancellationToken).ConfigureAwait(false);
    }
}
