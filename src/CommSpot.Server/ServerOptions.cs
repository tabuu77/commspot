namespace CommSpot.Server;

/// <summary>Bound from environment variables with the COMMSPOT_ prefix (see /etc/commspot/secrets.env).</summary>
public sealed class ServerOptions
{
    public const int MinAccessKeyLength = 24;

    /// <summary>Shared secret both testers type into the app. Step 2 replaces this with accounts.</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>Hostname clients send UDP voice to (DNS-only record, not proxied).</summary>
    public string VoiceHost { get; set; } = "voice.commspot.net";

    public int VoicePort { get; set; } = 50000;

    public int MaxMembers { get; set; } = 10;
}
