using System.Globalization;
using System.Text.Json.Nodes;

namespace Hibira;

/// <summary>
/// The Chat API (https://hibira.io/chat-api): conversations with stored
/// history, read receipts and offline push. Clients receive chat.* events on
/// #server-to-user-&lt;user_id&gt; after pusher.signin().
/// </summary>
public static class HibiraChatExtensions
{
    private static string Seg(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return Uri.EscapeDataString(id);
    }

    /// <summary>Creates or replaces a conversation: name, members, metadata, roles, watchers (leave members or watchers out to keep them).</summary>
    public static Task<JsonNode> PutConversationAsync(this IHibiraClient hibira, string conversationId, object fields, CancellationToken cancellationToken = default) =>
        hibira.ChatAsync(HttpMethod.Put, "/conversations/" + Seg(conversationId), null, fields, cancellationToken);

    /// <summary>Reads a conversation.</summary>
    public static Task<JsonNode> GetConversationAsync(this IHibiraClient hibira, string conversationId, CancellationToken cancellationToken = default) =>
        hibira.ChatAsync(HttpMethod.Get, "/conversations/" + Seg(conversationId), null, null, cancellationToken);

    /// <summary>Deletes a conversation with its members, messages and files.</summary>
    public static Task<JsonNode> DeleteConversationAsync(this IHibiraClient hibira, string conversationId, CancellationToken cancellationToken = default) =>
        hibira.ChatAsync(HttpMethod.Delete, "/conversations/" + Seg(conversationId), null, null, cancellationToken);

    /// <summary>Adds members.</summary>
    public static Task<JsonNode> AddMembersAsync(this IHibiraClient hibira, string conversationId, IEnumerable<string> userIds, CancellationToken cancellationToken = default) =>
        hibira.ChatAsync(HttpMethod.Post, "/conversations/" + Seg(conversationId) + "/members", null, new { user_ids = userIds.ToArray() }, cancellationToken);

    /// <summary>Removes a member.</summary>
    public static Task<JsonNode> RemoveMemberAsync(this IHibiraClient hibira, string conversationId, string userId, CancellationToken cancellationToken = default) =>
        hibira.ChatAsync(HttpMethod.Delete, "/conversations/" + Seg(conversationId) + "/members/" + Seg(userId), null, null, cancellationToken);

    /// <summary>
    /// Posts a message as a member. <paramref name="extra"/> may add name,
    /// metadata, push ("offline", "always", "never"), push_title, push_url,
    /// visible_to and idempotency_key.
    /// </summary>
    public static Task<JsonNode> SendMessageAsync(this IHibiraClient hibira, string conversationId, string userId, string? text,
        IReadOnlyDictionary<string, object?>? extra = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        var body = new Dictionary<string, object?> { ["user_id"] = userId };
        if (text != null) body["text"] = text;
        if (extra != null) foreach (var (k, v) in extra) body[k] = v;
        return hibira.ChatAsync(HttpMethod.Post, "/conversations/" + Seg(conversationId) + "/messages", null, body, cancellationToken);
    }

    /// <summary>Messages oldest first after a seq (0 for the start), at most <paramref name="limit"/> (0 for the default).</summary>
    public static Task<JsonNode> ListMessagesAsync(this IHibiraClient hibira, string conversationId, long after = 0, int limit = 0, CancellationToken cancellationToken = default)
    {
        var q = new List<KeyValuePair<string, string>>();
        if (after > 0) q.Add(new("after", after.ToString(CultureInfo.InvariantCulture)));
        if (limit > 0) q.Add(new("limit", limit.ToString(CultureInfo.InvariantCulture)));
        return hibira.ChatAsync(HttpMethod.Get, "/conversations/" + Seg(conversationId) + "/messages", q, null, cancellationToken);
    }

    /// <summary>Marks a conversation read up to <paramref name="seq"/> for a member.</summary>
    public static Task<JsonNode> MarkReadAsync(this IHibiraClient hibira, string conversationId, string userId, long seq, CancellationToken cancellationToken = default) =>
        hibira.ChatAsync(HttpMethod.Post, "/conversations/" + Seg(conversationId) + "/read", null, new { user_id = userId, seq }, cancellationToken);

    /// <summary>A message with no sender, such as "Alice joined".</summary>
    public static Task<JsonNode> SystemMessageAsync(this IHibiraClient hibira, string conversationId, string text, object? metadata = null, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?> { ["text"] = text };
        if (metadata != null) body["metadata"] = metadata;
        return hibira.ChatAsync(HttpMethod.Post, "/conversations/" + Seg(conversationId) + "/system", null, body, cancellationToken);
    }

    /// <summary>A user's conversations, latest activity first, each with its unread count.</summary>
    public static Task<JsonNode> UserConversationsAsync(this IHibiraClient hibira, string userId, int limit = 0, string? before = null, CancellationToken cancellationToken = default)
    {
        var q = new List<KeyValuePair<string, string>>();
        if (limit > 0) q.Add(new("limit", limit.ToString(CultureInfo.InvariantCulture)));
        if (!string.IsNullOrEmpty(before)) q.Add(new("before", before));
        return hibira.ChatAsync(HttpMethod.Get, "/users/" + Seg(userId) + "/conversations", q, null, cancellationToken);
    }
}
