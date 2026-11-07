using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CommSpot.Protocol;

/// <summary>
/// Seals and opens UDP voice packets with AES-256-GCM.
/// Packet: [version 1][sessionId 4][counter 8][ciphertext][tag 16]. The 13-byte header is the AAD.
/// Nonce: [direction 4][counter 8]. Counters never repeat per key and direction, so nonces never repeat.
/// </summary>
public sealed class VoiceCrypto : IDisposable
{
    private readonly AesGcm _aes;

    public VoiceCrypto(ReadOnlySpan<byte> key)
    {
        if (key.Length != VoiceConstants.KeySize)
        {
            throw new ArgumentException("Voice key must be 32 bytes.", nameof(key));
        }
        _aes = new AesGcm(key, VoiceConstants.TagSize);
    }

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(VoiceConstants.KeySize);

    /// <summary>Writes a sealed packet into <paramref name="destination"/> and returns its length.</summary>
    public int Seal(VoiceDirection direction, uint sessionId, ulong counter, ReadOnlySpan<byte> plaintext, Span<byte> destination)
    {
        if (plaintext.Length > VoiceConstants.MaxPayloadSize)
        {
            throw new ArgumentException("Payload too large.", nameof(plaintext));
        }
        int total = VoiceConstants.HeaderSize + plaintext.Length + VoiceConstants.TagSize;
        if (destination.Length < total)
        {
            throw new ArgumentException("Destination too small.", nameof(destination));
        }

        WriteHeader(destination, sessionId, counter);
        Span<byte> nonce = stackalloc byte[12];
        WriteNonce(nonce, direction, counter);

        var header = destination[..VoiceConstants.HeaderSize];
        var cipher = destination.Slice(VoiceConstants.HeaderSize, plaintext.Length);
        var tag = destination.Slice(VoiceConstants.HeaderSize + plaintext.Length, VoiceConstants.TagSize);
        _aes.Encrypt(nonce, plaintext, cipher, tag, header);
        return total;
    }

    /// <summary>
    /// Authenticates and decrypts. Returns false (and writes nothing useful) for anything forged,
    /// corrupted, truncated, or the wrong version. Does not check replay; callers use <see cref="ReplayWindow"/>.
    /// </summary>
    public bool TryOpen(VoiceDirection direction, ReadOnlySpan<byte> packet, Span<byte> plaintext, out int plaintextLength)
    {
        plaintextLength = 0;
        if (!TryReadHeader(packet, out _, out ulong counter))
        {
            return false;
        }
        int cipherLen = packet.Length - VoiceConstants.HeaderSize - VoiceConstants.TagSize;
        if (plaintext.Length < cipherLen)
        {
            return false;
        }

        Span<byte> nonce = stackalloc byte[12];
        WriteNonce(nonce, direction, counter);
        try
        {
            _aes.Decrypt(
                nonce,
                packet.Slice(VoiceConstants.HeaderSize, cipherLen),
                packet.Slice(VoiceConstants.HeaderSize + cipherLen, VoiceConstants.TagSize),
                plaintext[..cipherLen],
                packet[..VoiceConstants.HeaderSize]);
        }
        catch (AuthenticationTagMismatchException)
        {
            plaintext[..cipherLen].Clear();
            return false;
        }
        plaintextLength = cipherLen;
        return true;
    }

    /// <summary>Cheap structural check before any crypto: size bounds and version.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> packet, out uint sessionId, out ulong counter)
    {
        sessionId = 0;
        counter = 0;
        if (packet.Length < VoiceConstants.HeaderSize + VoiceConstants.TagSize
            || packet.Length > VoiceConstants.MaxPacketSize
            || packet[0] != VoiceConstants.Version)
        {
            return false;
        }
        sessionId = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(1, 4));
        counter = BinaryPrimitives.ReadUInt64BigEndian(packet.Slice(5, 8));
        return true;
    }

    private static void WriteHeader(Span<byte> dest, uint sessionId, ulong counter)
    {
        dest[0] = VoiceConstants.Version;
        BinaryPrimitives.WriteUInt32BigEndian(dest.Slice(1, 4), sessionId);
        BinaryPrimitives.WriteUInt64BigEndian(dest.Slice(5, 8), counter);
    }

    private static void WriteNonce(Span<byte> nonce, VoiceDirection direction, ulong counter)
    {
        BinaryPrimitives.WriteUInt32BigEndian(nonce[..4], (uint)direction);
        BinaryPrimitives.WriteUInt64BigEndian(nonce.Slice(4, 8), counter);
    }

    public void Dispose() => _aes.Dispose();
}
