namespace Hibira;

/// <summary>
/// Credentials and host of a Hibira app. Copy the app ID, key and secret from
/// your app page on https://hibira.io/portal.
/// </summary>
public sealed class HibiraOptions
{
    /// <summary>The configuration section <c>AddHibira</c> binds by default.</summary>
    public const string SectionName = "Hibira";

    /// <summary>The app ID.</summary>
    public string AppId { get; set; } = "";

    /// <summary>The app key. It is public: browsers connect with it.</summary>
    public string Key { get; set; } = "";

    /// <summary>The app secret. Keep it on the server.</summary>
    public string Secret { get; set; } = "";

    /// <summary>
    /// The host shown on the app page: <c>ws.hibira.io</c> for the default
    /// cluster, or <c>ws-&lt;cluster&gt;.hibira.io</c> for apps in another region.
    /// The HTTP API and the WebSocket endpoint share it.
    /// </summary>
    public string Host { get; set; } = "ws.hibira.io";

    /// <summary>The port; null means 443 with TLS and 80 without.</summary>
    public int? Port { get; set; }

    /// <summary>Use https and wss. Turn off only for a local server.</summary>
    public bool UseTls { get; set; } = true;

    /// <summary>Timeout for HTTP API requests.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The port in use: <see cref="Port"/>, or 443 with TLS and 80 without.</summary>
    public int EffectivePort => Port ?? (UseTls ? 443 : 80);

    internal Uri BaseUri
    {
        get
        {
            var scheme = UseTls ? "https" : "http";
            var defaultPort = UseTls ? 443 : 80;
            return new Uri(EffectivePort == defaultPort ? $"{scheme}://{Host}" : $"{scheme}://{Host}:{EffectivePort}");
        }
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(AppId) || string.IsNullOrWhiteSpace(Key) || string.IsNullOrWhiteSpace(Secret))
            throw new InvalidOperationException(
                "Hibira: AppId, Key and Secret are required. Copy them from your app page on https://hibira.io/portal " +
                "into the \"Hibira\" configuration section (for example HIBIRA__APPID, HIBIRA__KEY and HIBIRA__SECRET).");
        if (string.IsNullOrWhiteSpace(Host))
            throw new InvalidOperationException("Hibira: Host is required (ws.hibira.io for the default cluster).");
    }
}
