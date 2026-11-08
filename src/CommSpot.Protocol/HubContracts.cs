namespace CommSpot.Protocol;

/// <summary>Names shared by the SignalR hub and its clients, so a typo can't silently break a call.</summary>
public static class HubNames
{
    public const string Path = "/hub";
    public const string Join = "Join";
    public const string SetMuted = "SetMuted";
    public const string MemberJoined = "MemberJoined";
    public const string MemberLeft = "MemberLeft";
    public const string MemberMuted = "MemberMuted";
    public const string SetSharing = "SetSharing";
    public const string Watch = "Watch";
    public const string MemberSharing = "MemberSharing";
    public const string SendChat = "SendChat";
    public const string SendDirect = "SendDirect";
    public const string ChatMessage = "ChatMessage";
    public const string DirectMessage = "DirectMessage";
}

/// <summary>A room chat message. <see cref="SentAt"/> is the server's clock, in Unix milliseconds.</summary>
public sealed record ChatMessage(int FromUserId, string FromName, string Text, long SentAt);

/// <summary>A private message between two members. Sent to the recipient and echoed to the sender.</summary>
public sealed record DirectMessage(int FromUserId, string FromName, int ToUserId, string ToName, string Text, long SentAt);

public static class ChatText
{
    public const int MaxLength = 2000;

    /// <summary>Trims, drops control and text-direction characters (keeps new lines; tabs become spaces) and limits the length. False if nothing is left.</summary>
    public static bool TryNormalize(string? raw, out string text)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char ch in (raw ?? string.Empty).Trim())
        {
            // Bidi overrides/isolates could make a message display as something else ("spoofing"); drop them.
            if (ch is '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E' or '\u2066' or '\u2067' or '\u2068' or '\u2069' or '\u200E' or '\u200F')
            {
                continue;
            }
            if (ch is '\n' or '\t' || !char.IsControl(ch))
            {
                sb.Append(ch == '\t' ? ' ' : ch);
            }
        }
        text = sb.ToString().Replace("\r", "", StringComparison.Ordinal);
        if (text.Length > MaxLength)
        {
            text = text[..MaxLength];
        }
        return text.Length > 0;
    }
}

public sealed record Member(int UserId, string Name, bool Muted, bool Sharing = false);

public sealed record JoinRequest(string AccessKey, string DisplayName);

/// <summary>Sent only over TLS. <see cref="VoiceKey"/> is the per-session AES-256 key.</summary>
public sealed record JoinResult(
    bool Ok,
    string? Error,
    int UserId,
    uint SessionId,
    byte[]? VoiceKey,
    string? VoiceHost,
    int VoicePort,
    IReadOnlyList<Member> Members);

public static class DisplayNames
{
    public const int MaxLength = 32;

    /// <summary>Letters, digits, space, '-', '_', '.'; 1 to 32 characters after trimming.</summary>
    public static bool TryNormalize(string? raw, out string name)
    {
        name = (raw ?? string.Empty).Trim();
        if (name.Length is 0 or > MaxLength)
        {
            return false;
        }
        foreach (char ch in name)
        {
            if (!(char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' or '.'))
            {
                return false;
            }
        }
        return true;
    }
}
