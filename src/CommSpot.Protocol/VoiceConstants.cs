namespace CommSpot.Protocol;

/// <summary>Fixed parameters shared by client and server. See docs/SECURITY_DESIGN.md section 4.</summary>
public static class VoiceConstants
{
    public const byte Version = 1;               // voice packets
    public const byte VideoVersion = 2;          // screen share packets (same crypto, own rate limit)
    public const int HeaderSize = 1 + 4 + 8;      // version, sessionId, counter
    public const int TagSize = 16;                // AES-GCM tag
    public const int KeySize = 32;                // AES-256
    public const int MaxPacketSize = 1400;        // stays under typical MTU
    public const int MaxPayloadSize = MaxPacketSize - HeaderSize - TagSize;
    public const int SenderIdSize = 4;            // server -> client payload prefix

    public const int SampleRate = 48000;
    public const int Channels = 1;
    public const int FrameMs = 20;
    public const int FrameSamples = SampleRate / 1000 * FrameMs; // 960
    public const int OpusBitrate = 32000;

    public const int KeepAliveSeconds = 5;
    public const int MaxPacketsPerSecond = 60;    // 50 frames/s + keepalives + slack

    // Screen share: a JPEG frame split into fragments [frameId 4][index 2][count 2][data].
    public const int VideoFragmentHeader = 8;
    public const int MaxVideoFragmentData = MaxPayloadSize - SenderIdSize - VideoFragmentHeader;
    public const int MaxVideoFragments = 1024;    // ~1.3 MB per frame at most
    public const int MaxVideoPacketsPerSecond = 1500; // ~2 MB/s per sharer
    public const int MaxVideoDimension = 4096;
}

/// <summary>Who sent a packet. Part of the nonce, so the two directions never share a nonce.</summary>
public enum VoiceDirection : uint
{
    ClientToServer = 1,
    ServerToClient = 2,
}
