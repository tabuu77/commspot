using System.Buffers.Binary;
using CommSpot.Protocol;

namespace CommSpot.Client.Core;

/// <summary>Splits encoded screen frames into packets and puts them back together on the other side.</summary>
public static class VideoFragments
{
    /// <summary>Payloads for one frame: [frameId 4][index 2][count 2][data].</summary>
    public static IEnumerable<byte[]> Split(uint frameId, ReadOnlyMemory<byte> frame)
    {
        int max = VoiceConstants.MaxVideoFragmentData;
        int count = (frame.Length + max - 1) / max;
        if (count is 0 or > VoiceConstants.MaxVideoFragments)
        {
            throw new ArgumentException("Frame is empty or too large.", nameof(frame));
        }
        for (int i = 0; i < count; i++)
        {
            var part = frame.Slice(i * max, Math.Min(max, frame.Length - (i * max)));
            var p = new byte[VoiceConstants.VideoFragmentHeader + part.Length];
            BinaryPrimitives.WriteUInt32BigEndian(p, frameId);
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), (ushort)i);
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(6), (ushort)count);
            part.Span.CopyTo(p.AsSpan(VoiceConstants.VideoFragmentHeader));
            yield return p;
        }
    }
}

/// <summary>
/// Reassembles one sharer's frames. Keeps only the newest frame in progress: a lost packet costs one frame,
/// never a stall. Input comes from the network, so every field is bounds-checked.
/// </summary>
public sealed class VideoAssembler
{
    private uint _frameId;
    private bool _active;
    private byte[]?[] _parts = [];
    private int _received;
    private uint _lastCompleted;
    private bool _anyCompleted;

    /// <summary>Feeds one fragment. Returns a complete frame when this fragment finished one.</summary>
    public byte[]? Add(ReadOnlySpan<byte> fragment)
    {
        if (fragment.Length <= VoiceConstants.VideoFragmentHeader)
        {
            return null;
        }
        uint id = BinaryPrimitives.ReadUInt32BigEndian(fragment);
        int index = BinaryPrimitives.ReadUInt16BigEndian(fragment[4..]);
        int count = BinaryPrimitives.ReadUInt16BigEndian(fragment[6..]);
        var data = fragment[VoiceConstants.VideoFragmentHeader..];
        if (count is 0 or > VoiceConstants.MaxVideoFragments || index >= count || data.Length > VoiceConstants.MaxVideoFragmentData)
        {
            return null;
        }
        if (_anyCompleted && (int)(id - _lastCompleted) <= 0)
        {
            return null; // old or duplicate frame
        }
        if (!_active || id != _frameId)
        {
            if (_active && (int)(id - _frameId) < 0)
            {
                return null; // late fragment of an older frame
            }
            _frameId = id;
            _active = true;
            _parts = new byte[]?[count];
            _received = 0;
        }
        if (count != _parts.Length || _parts[index] is not null)
        {
            return null;
        }
        _parts[index] = data.ToArray();
        if (++_received < count)
        {
            return null;
        }
        var frame = new byte[_parts.Sum(p => p!.Length)];
        int at = 0;
        foreach (var p in _parts)
        {
            p!.CopyTo(frame, at);
            at += p.Length;
        }
        _active = false;
        _parts = [];
        _lastCompleted = id;
        _anyCompleted = true;
        return frame;
    }
}
