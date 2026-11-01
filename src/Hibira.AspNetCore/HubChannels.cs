using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Hibira.AspNetCore;

/// <summary>The channel names of one hub.</summary>
internal sealed class HubChannels
{
    public const string ChannelRoot = "private-hibira.";

    public HubChannels(string hubName)
    {
        Hub = hubName;
        All = ChannelRoot + hubName;
        ConnectionPrefix = All + ".c.";
        GroupPrefix = All + ".g.";
        HashedGroupPrefix = All + ".h.";
    }

    public string Hub { get; }
    public string All { get; }
    public string ConnectionPrefix { get; }
    public string GroupPrefix { get; }
    public string HashedGroupPrefix { get; }

    public string Connection(string connectionId)
    {
        if (!IsSocketId(connectionId)) throw new ArgumentException($"not a connection ID: {connectionId}", nameof(connectionId));
        return ConnectionPrefix + connectionId;
    }

    public string Group(string groupName)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupName);
        var name = GroupPrefix + HibiraChannelName.Escape(groupName);
        if (name.Length <= HibiraChannelName.MaxLength) return name;
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(groupName)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return HashedGroupPrefix + hash;
    }

    public bool IsGroupChannel(string channel) =>
        channel.StartsWith(GroupPrefix, StringComparison.Ordinal) || channel.StartsWith(HashedGroupPrefix, StringComparison.Ordinal);

    /// <summary>The connection ID of a connection channel of this hub, or null.</summary>
    public string? ConnectionOf(string channel) =>
        channel.StartsWith(ConnectionPrefix, StringComparison.Ordinal) && IsSocketId(channel[ConnectionPrefix.Length..])
            ? channel[ConnectionPrefix.Length..]
            : null;

    public static bool IsSocketId(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 3 || s.Length > 64) return false;
        var dot = s.IndexOf('.');
        if (dot <= 0 || dot == s.Length - 1) return false;
        for (var i = 0; i < s.Length; i++)
            if (i != dot && !char.IsAsciiDigit(s[i])) return false;
        return true;
    }

    public static string NameOf(Type hubType, string? overrideName)
    {
        var name = overrideName ?? hubType.GetCustomAttribute<HibiraHubNameAttribute>()?.Name;
        if (name == null)
        {
            name = hubType.Name;
            var tick = name.IndexOf('`');
            if (tick > 0) name = name[..tick];
            if (name.Length > 3 && name.EndsWith("Hub", StringComparison.Ordinal)) name = name[..^3];
            name = new string(name.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').ToArray());
        }
        if (name.Length == 0 || name.Length > 40 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw new InvalidOperationException($"Hibira: hub name \"{name}\" must be 1 to 40 letters, digits, \"_\" or \"-\".");
        return name;
    }
}
