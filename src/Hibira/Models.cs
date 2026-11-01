using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hibira;

/// <summary>One event of <see cref="IHibiraClient.TriggerBatchAsync"/>.</summary>
/// <param name="Channel">The channel.</param>
/// <param name="Name">The event name.</param>
/// <param name="Data">The payload: a string is sent as is, anything else as JSON.</param>
/// <param name="SocketId">A socket to leave out, usually the sender's.</param>
public sealed record HibiraBatchEvent(string Channel, string Name, object? Data, string? SocketId = null);

/// <summary>An occupied channel, from <see cref="IHibiraClient.GetChannelsAsync"/>.</summary>
public sealed class HibiraChannelInfo
{
    /// <summary>Whether anyone is subscribed.</summary>
    [JsonPropertyName("occupied")] public bool Occupied { get; set; }
    /// <summary>Distinct users on a presence channel (when asked for with info=user_count).</summary>
    [JsonPropertyName("user_count")] public int? UserCount { get; set; }
    /// <summary>Subscriptions (when asked for with info=subscription_count).</summary>
    [JsonPropertyName("subscription_count")] public int? SubscriptionCount { get; set; }
}

/// <summary>The answer a channel authorization endpoint returns to the client.</summary>
public sealed class HibiraChannelAuth
{
    /// <summary>"key:signature".</summary>
    [JsonPropertyName("auth")] public string Auth { get; set; } = "";
    /// <summary>The presence member data, as a JSON string (presence channels only).</summary>
    [JsonPropertyName("channel_data"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChannelData { get; set; }
}

/// <summary>The answer a user authentication (sign-in) endpoint returns to the client.</summary>
public sealed class HibiraUserAuth
{
    /// <summary>"key:signature".</summary>
    [JsonPropertyName("auth")] public string Auth { get; set; } = "";
    /// <summary>The user data, as a JSON string.</summary>
    [JsonPropertyName("user_data")] public string UserData { get; set; } = "";
}

/// <summary>A verified webhook request.</summary>
public sealed class HibiraWebhook
{
    /// <summary>When Hibira sent it, in Unix milliseconds.</summary>
    [JsonPropertyName("time_ms")] public long TimeMs { get; set; }
    /// <summary>The events.</summary>
    [JsonPropertyName("events")] public List<HibiraWebhookEvent> Events { get; set; } = new();
}

/// <summary>One webhook event.</summary>
public sealed class HibiraWebhookEvent
{
    /// <summary>channel_occupied, channel_vacated, member_added, member_removed, client_event, subscription_count, cache_miss or chat_message.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>The channel.</summary>
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";
    /// <summary>The user (member and client events on presence channels).</summary>
    [JsonPropertyName("user_id")] public string? UserId { get; set; }
    /// <summary>The client event name.</summary>
    [JsonPropertyName("event")] public string? Event { get; set; }
    /// <summary>The client event data.</summary>
    [JsonPropertyName("data")] public string? Data { get; set; }
    /// <summary>The socket that sent the client event.</summary>
    [JsonPropertyName("socket_id")] public string? SocketId { get; set; }
    /// <summary>Subscriptions (subscription_count events).</summary>
    [JsonPropertyName("subscription_count")] public int? SubscriptionCount { get; set; }
}

/// <summary>A request for a decision from a person (Hibira approvals).</summary>
public sealed class HibiraApprovalRequest
{
    /// <summary>The question, at most 200 characters.</summary>
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    /// <summary>More context, at most 4000 characters.</summary>
    [JsonPropertyName("details"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Details { get; set; }
    /// <summary>2 to 4 choices; the first one approves. Default Approve / Reject.</summary>
    [JsonPropertyName("options"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IList<string>? Options { get; set; }
    /// <summary>1 to 10080 minutes, default 60.</summary>
    [JsonPropertyName("expires_in_minutes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? ExpiresInMinutes { get; set; }
    /// <summary>A channel that receives an <c>approval</c> event on request and decision.</summary>
    [JsonPropertyName("channel"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Channel { get; set; }
    /// <summary>Push interests to notify (needs Push on the app).</summary>
    [JsonPropertyName("push_interests"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IList<string>? PushInterests { get; set; }
    /// <summary>Push users to notify (needs Push on the app).</summary>
    [JsonPropertyName("push_users"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IList<string>? PushUsers { get; set; }
    /// <summary>Who asks, at most 60 characters.</summary>
    [JsonPropertyName("agent"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Agent { get; set; }
}

/// <summary>An approval and its answer.</summary>
public sealed class HibiraApproval
{
    /// <summary>The approval ID.</summary>
    [JsonPropertyName("approval_id")] public string ApprovalId { get; set; } = "";
    /// <summary>The question.</summary>
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    /// <summary>The choices.</summary>
    [JsonPropertyName("options")] public List<string> Options { get; set; } = new();
    /// <summary>pending, approved, rejected or expired.</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    /// <summary>The answer page to share with whoever decides.</summary>
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    /// <summary>The chosen option, once answered.</summary>
    [JsonPropertyName("choice")] public string? Choice { get; set; }
    /// <summary>The person's comment, if any.</summary>
    [JsonPropertyName("comment")] public string? Comment { get; set; }
    /// <summary>When it was created.</summary>
    [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; set; }
    /// <summary>When it expires.</summary>
    [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>When it was answered.</summary>
    [JsonPropertyName("answered_at")] public DateTimeOffset? AnsweredAt { get; set; }
    /// <summary>Set when the push notification could not be sent.</summary>
    [JsonPropertyName("push_error")] public string? PushError { get; set; }
    /// <summary>Whether the person has answered (or it expired).</summary>
    [JsonIgnore] public bool IsPending => Status == "pending";
    /// <summary>Whether the first option was chosen.</summary>
    [JsonIgnore] public bool IsApproved => Status == "approved";
}

internal static class HibiraJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
