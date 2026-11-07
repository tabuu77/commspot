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
}

public sealed record Member(int UserId, string Name, bool Muted);

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
