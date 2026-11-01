using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Hibira;

/// <summary>The signatures of the Pusher Channels protocol.</summary>
public static class HibiraSignature
{
    /// <summary>Lowercase hex HMAC-SHA256 of <paramref name="message"/>.</summary>
    public static string Hmac(string secret, string message)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    /// <summary>Lowercase hex HMAC-SHA256 of raw bytes.</summary>
    public static string Hmac(string secret, ReadOnlySpan<byte> message)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message);
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    /// <summary>Compares two hex signatures in constant time, ignoring case.</summary>
    public static bool Equal(string? a, string? b)
    {
        if (a is null || b is null) return false;
        var x = Encoding.ASCII.GetBytes(a.ToLowerInvariant());
        var y = Encoding.ASCII.GetBytes(b.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(x, y);
    }

    /// <summary>
    /// The query string of a signed HTTP API request: the given parameters
    /// plus auth_key, auth_timestamp, auth_version, body_md5 (when there is a
    /// body) and auth_signature.
    /// </summary>
    public static string SignedQuery(string key, string secret, string method, string path,
        IEnumerable<KeyValuePair<string, string>>? parameters, byte[]? body, DateTimeOffset now)
    {
        var q = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (parameters != null)
            foreach (var (k, v) in parameters)
                q[k.ToLowerInvariant()] = v;
        q["auth_key"] = key;
        q["auth_timestamp"] = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        q["auth_version"] = "1.0";
        if (body is { Length: > 0 })
            q["body_md5"] = Convert.ToHexString(MD5.HashData(body)).ToLowerInvariant();
        var canonical = string.Join("&", q.Select(p => p.Key + "=" + p.Value));
        var signature = Hmac(secret, method.ToUpperInvariant() + "\n" + path + "\n" + canonical);
        return string.Join("&", q.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value))) + "&auth_signature=" + signature;
    }
}
