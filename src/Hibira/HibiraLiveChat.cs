using System.Net;
using System.Text;

namespace Hibira;

/// <summary>
/// Hibira Live Chat (https://chat.hibira.io) on your pages. With an identity
/// secret (Live Chat › Settings › Widget › Verified visitors), signed-in users
/// chat as themselves: their ID is signed with HMAC-SHA256, so the inbox shows
/// their name and email and nobody can pretend to be them.
/// </summary>
public sealed class HibiraLiveChat
{
    /// <summary>The widget script.</summary>
    public const string ScriptUrl = "https://chat.hibira.io/w/v1.js";

    /// <summary>Creates the helper for a workspace, with an optional identity secret.</summary>
    public HibiraLiveChat(string? workspace, string? identitySecret = null)
    {
        Workspace = string.IsNullOrWhiteSpace(workspace) ? null : workspace;
        IdentitySecret = string.IsNullOrWhiteSpace(identitySecret) ? null : identitySecret;
    }

    /// <summary>The workspace ID, or null.</summary>
    public string? Workspace { get; }

    private string? IdentitySecret { get; }

    /// <summary>hex(HMAC-SHA256(identity secret, user ID)): the widget's data-user-hash.</summary>
    public static string UserHash(string identitySecret, string userId)
    {
        ArgumentException.ThrowIfNullOrEmpty(identitySecret);
        ArgumentException.ThrowIfNullOrEmpty(userId);
        return HibiraSignature.Hmac(identitySecret, userId);
    }

    /// <summary>
    /// The verified-visitor fields (id, hash, name, email) for
    /// HibiraLiveChat.identify() in an SPA, or null without a user ID or an
    /// identity secret.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Identity(string? userId, string? name = null, string? email = null)
    {
        if (IdentitySecret == null || string.IsNullOrEmpty(userId)) return null;
        var output = new Dictionary<string, string> { ["id"] = userId, ["hash"] = UserHash(IdentitySecret, userId) };
        if (!string.IsNullOrEmpty(name)) output["name"] = name;
        if (!string.IsNullOrEmpty(email)) output["email"] = email;
        return output;
    }

    /// <summary>
    /// The widget's script tag, with the signed-in user when <paramref name="userId"/>
    /// is set; empty without a workspace. In Razor: @Html.Raw(liveChat.ScriptTag(...)).
    /// </summary>
    public string ScriptTag(string? userId = null, string? name = null, string? email = null, string? nonce = null)
    {
        if (Workspace == null) return "";
        var html = new StringBuilder("<script src=\"").Append(ScriptUrl).Append('"');
        Attribute(html, "data-workspace", Workspace);
        if (Identity(userId, name, email) is { } who)
        {
            Attribute(html, "data-user-id", who["id"]);
            Attribute(html, "data-user-hash", who["hash"]);
            Attribute(html, "data-user-name", who.GetValueOrDefault("name"));
            Attribute(html, "data-user-email", who.GetValueOrDefault("email"));
        }
        Attribute(html, "nonce", nonce);
        return html.Append(" async></script>").ToString();
    }

    private static void Attribute(StringBuilder html, string name, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        html.Append(' ').Append(name).Append("=\"").Append(WebUtility.HtmlEncode(value)).Append('"');
    }
}
