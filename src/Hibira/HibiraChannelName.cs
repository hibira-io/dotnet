using System.Text;

namespace Hibira;

/// <summary>Helpers for channel names and the user IDs inside them.</summary>
public static class HibiraChannelName
{
    /// <summary>Channel names are at most this many characters.</summary>
    public const int MaxLength = 164;

    /// <summary>The prefix of a user's own channel (Pusher user authentication).</summary>
    public const string ServerToUserPrefix = "#server-to-user-";

    /// <summary>Whether <paramref name="c"/> may appear in a channel name: a-z A-Z 0-9 _ - = @ , . ;</summary>
    public static bool IsValidChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' or '=' or '@' or ',' or '.' or ';';

    /// <summary>Whether <paramref name="name"/> is a valid channel name.</summary>
    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var rest = name.StartsWith(ServerToUserPrefix, StringComparison.Ordinal) ? name[ServerToUserPrefix.Length..] : name;
        return rest.Length > 0 && name.Length <= MaxLength && rest.All(IsValidChar);
    }

    /// <summary>
    /// Makes any string safe inside a channel name: characters outside the
    /// allowed set (and "=") become "=XX" per UTF-8 byte. The result is
    /// unchanged for names that are already safe and contain no "=".
    /// </summary>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.All(c => c != '=' && IsValidChar(c))) return value;
        var sb = new StringBuilder(value.Length + 8);
        Span<byte> buf = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.IsAscii && rune.Value != '=' && IsValidChar((char)rune.Value))
            {
                sb.Append((char)rune.Value);
                continue;
            }
            var n = rune.EncodeToUtf8(buf);
            for (var i = 0; i < n; i++) sb.Append('=').Append(buf[i].ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>Reverses <see cref="Escape"/>.</summary>
    public static string Unescape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.Contains('=')) return value;
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '=' && i + 2 < value.Length &&
                byte.TryParse(value.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                bytes.Add(b);
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(char.IsSurrogate(value[i]) && i + 1 < value.Length ? value.Substring(i++, 2) : value[i].ToString()));
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>The channel a signed-in user receives <c>SendToUserAsync</c> events on.</summary>
    public static string ForUser(string userId) => ServerToUserPrefix + Escape(userId);
}
