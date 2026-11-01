namespace Hibira;

/// <summary>
/// The Hibira HTTP API and the signatures your server makes for clients.
/// Register it with <c>services.AddHibira()</c> (Hibira.AspNetCore) or create
/// a <see cref="HibiraClient"/> yourself.
/// </summary>
public interface IHibiraClient
{
    /// <summary>The app this client signs for.</summary>
    HibiraOptions Options { get; }

    /// <summary>
    /// Sends an event to up to 100 channels. <paramref name="data"/> is sent as
    /// is when it is a string and as JSON otherwise. <paramref name="socketId"/>
    /// leaves that connection out (usually the sender).
    /// </summary>
    Task TriggerAsync(IEnumerable<string> channels, string eventName, object? data, string? socketId = null, CancellationToken cancellationToken = default);

    /// <summary>Sends an event to one channel.</summary>
    Task TriggerAsync(string channel, string eventName, object? data, string? socketId = null, CancellationToken cancellationToken = default);

    /// <summary>Sends up to 10 events in one request.</summary>
    Task TriggerBatchAsync(IEnumerable<HibiraBatchEvent> events, CancellationToken cancellationToken = default);

    /// <summary>Sends an event to every connection signed in as <paramref name="userId"/>.</summary>
    Task SendToUserAsync(string userId, string eventName, object? data, CancellationToken cancellationToken = default);

    /// <summary>Lists occupied channels, optionally only those starting with <paramref name="prefix"/>.</summary>
    Task<IReadOnlyDictionary<string, HibiraChannelInfo>> GetChannelsAsync(string? prefix = null, string? info = null, CancellationToken cancellationToken = default);

    /// <summary>Reads one channel. <paramref name="info"/> is a comma-separated list such as "user_count,subscription_count".</summary>
    Task<HibiraChannelInfo> GetChannelAsync(string channel, string? info = null, CancellationToken cancellationToken = default);

    /// <summary>Lists the user IDs on a presence channel.</summary>
    Task<IReadOnlyList<string>> GetUsersAsync(string presenceChannel, CancellationToken cancellationToken = default);

    /// <summary>Closes every connection signed in as <paramref name="userId"/>.</summary>
    Task TerminateUserConnectionsAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Asks a person for a decision. Share <see cref="HibiraApproval.Url"/> with them.</summary>
    Task<HibiraApproval> RequestApprovalAsync(HibiraApprovalRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads an approval. <paramref name="wait"/> (up to 25 seconds) holds the
    /// request until the person answers or the time runs out.
    /// </summary>
    Task<HibiraApproval> GetApprovalAsync(string approvalId, TimeSpan? wait = null, CancellationToken cancellationToken = default);

    /// <summary>Signs a private channel subscription for the connection <paramref name="socketId"/>.</summary>
    HibiraChannelAuth AuthorizeChannel(string socketId, string channel);

    /// <summary>
    /// Signs a presence channel subscription. <paramref name="userId"/> and
    /// <paramref name="userInfo"/> are what other members see.
    /// </summary>
    HibiraChannelAuth AuthorizePresenceChannel(string socketId, string channel, string userId, object? userInfo = null);

    /// <summary>
    /// Signs a user in on the connection <paramref name="socketId"/>, so
    /// <see cref="SendToUserAsync"/> reaches it. <paramref name="userData"/>
    /// may add fields such as "user_info" or "watchlist" to the user data.
    /// </summary>
    HibiraUserAuth AuthenticateUser(string socketId, string userId, IReadOnlyDictionary<string, object?>? userData = null);

    /// <summary>
    /// Checks a webhook request's signature and parses it. Returns null when
    /// the signature is wrong. Pass the X-Pusher-Key, X-Pusher-Signature and
    /// X-Hibira-Previous-Signature headers and the raw body.
    /// </summary>
    HibiraWebhook? VerifyWebhook(string? key, string? signature, string? previousSignature, ReadOnlySpan<byte> body);
}
