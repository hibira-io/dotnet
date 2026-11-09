using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hibira;

/// <summary>The claims of a valid waiting room pass. Times are Unix seconds.</summary>
/// <param name="AppId">The app the pass was issued for.</param>
/// <param name="Room">The waiting room.</param>
/// <param name="Number">The visitor's queue number.</param>
/// <param name="TicketId">The ticket ID; record it at checkout to allow one checkout per number.</param>
/// <param name="IssuedAt">When the pass was issued.</param>
/// <param name="ExpiresAt">When the pass expires.</param>
public sealed record WaitingRoomPass(string AppId, string Room, long Number, string TicketId, long IssuedAt, long ExpiresAt);

/// <summary>
/// Checks the signed pass a visitor brings back from a Hibira waiting room:
/// <c>base64url(JSON) + "." + base64url(HMAC-SHA256(secret, "hibira-waitroom-pass." + base64url(JSON)))</c>.
/// </summary>
public static class WaitingRoom
{
    /// <summary>The query parameter the waiting page adds when it sends a visitor back.</summary>
    public const string PassParameter = "hibira_pass";

    /// <summary>The default hosted waiting page origin.</summary>
    public const string DefaultHost = "https://ws.hibira.io";

    private const string Purpose = "hibira-waitroom-pass.";

    /// <summary>
    /// Returns the pass's claims, or null when it is missing, forged, expired,
    /// or for another room or app. Pass several secrets during a rotation.
    /// </summary>
    /// <param name="token">The pass.</param>
    /// <param name="secrets">The app secret, then any previous one.</param>
    /// <param name="room">The room it must be for, or null for any.</param>
    /// <param name="appId">The app it must be for, or null for any.</param>
    /// <param name="now">The current time; null means now.</param>
    public static WaitingRoomPass? VerifyPass(string? token, IEnumerable<string> secrets, string? room = null, string? appId = null, DateTimeOffset? now = null)
    {
        if (token is null || token.Length > 1024) return null;
        var parts = token.Split('.');
        if (parts.Length != 2 || parts[0].Length == 0) return null;
        var sig = Decode(parts[1]);
        if (sig is null || sig.Length != 32) return null;
        var message = Encoding.ASCII.GetBytes(Purpose + parts[0]);
        var ok = false;
        foreach (var secret in secrets)
        {
            if (string.IsNullOrEmpty(secret)) continue;
            if (CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message), sig))
            {
                ok = true;
                break;
            }
        }
        if (!ok) return null;
        var raw = Decode(parts[0]);
        if (raw is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var c = doc.RootElement;
            if (c.ValueKind != JsonValueKind.Object) return null;
            if (!c.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetDouble() != 1) return null;
            if (!c.TryGetProperty("exp", out var expEl) || expEl.ValueKind != JsonValueKind.Number) return null;
            var exp = (long)expEl.GetDouble();
            if ((now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() >= exp) return null;
            var passRoom = Str(c, "room");
            var passApp = Str(c, "app");
            if (room != null && room != passRoom) return null;
            if (appId != null && appId != passApp) return null;
            return new WaitingRoomPass(passApp, passRoom, Num(c, "n"), Str(c, "tid"), Num(c, "iat"), exp);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Verifies a pass with one secret, at the current time.</summary>
    /// <param name="token">The pass.</param>
    /// <param name="secret">The app secret.</param>
    /// <param name="room">The room it must be for, or null for any.</param>
    public static WaitingRoomPass? VerifyPass(string? token, string secret, string? room = null) =>
        VerifyPass(token, new[] { secret }, room);

    /// <summary>The hosted waiting page address for a room.</summary>
    /// <param name="appKey">The app key.</param>
    /// <param name="room">The room.</param>
    /// <param name="returnUrl">Where to send the visitor when it's their turn.</param>
    /// <param name="host">The waiting page origin; null means https://ws.hibira.io.</param>
    /// <param name="lang">The page language (en, ja, ko, zh-TW, ...); null follows the browser.</param>
    public static string Url(string appKey, string room, string? returnUrl = null, string? host = null, string? lang = null)
    {
        var url = new StringBuilder((host ?? DefaultHost).TrimEnd('/'))
            .Append("/wait/").Append(Uri.EscapeDataString(appKey)).Append('/').Append(Uri.EscapeDataString(room));
        var sep = '?';
        if (!string.IsNullOrEmpty(returnUrl))
        {
            url.Append(sep).Append("return=").Append(Uri.EscapeDataString(returnUrl));
            sep = '&';
        }
        if (!string.IsNullOrEmpty(lang)) url.Append(sep).Append("lang=").Append(Uri.EscapeDataString(lang));
        return url.ToString();
    }

    private static string Str(JsonElement c, string name) =>
        c.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";

    private static long Num(JsonElement c, string name) =>
        c.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? (long)e.GetDouble() : 0;

    private static byte[]? Decode(string s)
    {
        if (s.Length == 0) return null;
        foreach (var ch in s)
            if (!(char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_')) return null;
        var b64 = s.Replace('-', '+').Replace('_', '/');
        b64 += (b64.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => "!" };
        try
        {
            return Convert.FromBase64String(b64);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
