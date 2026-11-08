using System.Net;
using CommSpot.Protocol;

namespace CommSpot.Server;

/// <summary>One connected member: identity, voice key, replay state, UDP endpoint, rate limit.</summary>
internal sealed class VoiceSession : IDisposable
{
    private readonly object _gate = new();
    private readonly VoiceCrypto _crypto;
    private readonly ReplayWindow _replay = new();
    private ulong _sendCounter;
    private double _tokens = VoiceConstants.MaxPacketsPerSecond;
    private long _lastRefillTicks = Environment.TickCount64;
    private double _videoTokens = VoiceConstants.MaxVideoPacketsPerSecond;
    private long _videoRefillTicks = Environment.TickCount64;
    private bool _disposed;

    public VoiceSession(string connectionId, int userId, uint sessionId, string name, byte[] key)
    {
        ConnectionId = connectionId;
        UserId = userId;
        SessionId = sessionId;
        Name = name;
        _crypto = new VoiceCrypto(key);
        _exportKey = key;
    }

    private byte[]? _exportKey;

    /// <summary>Hands the key to the joining client exactly once; the server keeps only the AES context.</summary>
    public byte[] ExportKeyOnce()
    {
        lock (_gate)
        {
            var k = _exportKey ?? throw new InvalidOperationException("Key already exported.");
            _exportKey = null;
            return k;
        }
    }

    public string ConnectionId { get; }
    public int UserId { get; }
    public uint SessionId { get; }
    public string Name { get; }
    public volatile bool Muted;
    public volatile bool Sharing;

    /// <summary>UserId whose screen this member watches, or 0.</summary>
    public volatile int Watching;
    public IPEndPoint? Endpoint { get; private set; }

    public Member ToMember() => new(UserId, Name, Muted, Sharing);

    /// <summary>Token bucket: refuse floods before spending CPU on decryption.</summary>
    public bool TryTakeToken()
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;
            _tokens = Math.Min(VoiceConstants.MaxPacketsPerSecond,
                _tokens + (now - _lastRefillTicks) / 1000.0 * VoiceConstants.MaxPacketsPerSecond);
            _lastRefillTicks = now;
            if (_tokens < 1)
            {
                return false;
            }
            _tokens -= 1;
            return true;
        }
    }

    /// <summary>Separate, larger bucket for screen share packets so video can't starve voice or be used to flood.</summary>
    public bool TryTakeVideoToken()
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;
            _videoTokens = Math.Min(VoiceConstants.MaxVideoPacketsPerSecond,
                _videoTokens + (now - _videoRefillTicks) / 1000.0 * VoiceConstants.MaxVideoPacketsPerSecond);
            _videoRefillTicks = now;
            if (_videoTokens < 1)
            {
                return false;
            }
            _videoTokens -= 1;
            return true;
        }
    }

    /// <summary>Authenticate, then replay-check, then (only then) learn the sender's endpoint.</summary>
    public bool TryOpen(ReadOnlySpan<byte> packet, IPEndPoint from, Span<byte> plaintext, out int length)
    {
        length = 0;
        lock (_gate)
        {
            if (_disposed || !_crypto.TryOpen(VoiceDirection.ClientToServer, packet, plaintext, out length))
            {
                return false;
            }
            VoiceCrypto.TryReadHeader(packet, out _, out ulong counter);
            if (!_replay.TryAccept(counter))
            {
                length = 0;
                return false;
            }
            Endpoint = from;
            return true;
        }
    }

    public int Seal(ReadOnlySpan<byte> plaintext, Span<byte> destination, byte version = VoiceConstants.Version)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _crypto.Seal(VoiceDirection.ServerToClient, SessionId, ++_sendCounter, plaintext, destination, version);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _crypto.Dispose();
        }
    }
}
