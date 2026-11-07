using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace CommSpot.Server;

/// <summary>The single voice room for Step 1. Thread-safe.</summary>
internal sealed class Room
{
    private readonly ConcurrentDictionary<string, VoiceSession> _byConnection = new();
    private readonly ConcurrentDictionary<uint, VoiceSession> _bySession = new();
    private readonly object _joinGate = new();
    private int _nextUserId;

    public IEnumerable<VoiceSession> Sessions => _bySession.Values;

    public VoiceSession? Add(string connectionId, string name, int maxMembers)
    {
        lock (_joinGate)
        {
            if (_byConnection.ContainsKey(connectionId) || _byConnection.Count >= maxMembers)
            {
                return null;
            }
            uint sessionId;
            do
            {
                sessionId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
            }
            while (_bySession.ContainsKey(sessionId));

            var session = new VoiceSession(connectionId, Interlocked.Increment(ref _nextUserId), sessionId, name,
                Protocol.VoiceCrypto.NewKey());
            _byConnection[connectionId] = session;
            _bySession[sessionId] = session;
            return session;
        }
    }

    public VoiceSession? ByConnection(string connectionId) =>
        _byConnection.TryGetValue(connectionId, out var s) ? s : null;

    public VoiceSession? BySession(uint sessionId) =>
        _bySession.TryGetValue(sessionId, out var s) ? s : null;

    public VoiceSession? Remove(string connectionId)
    {
        lock (_joinGate)
        {
            if (!_byConnection.TryRemove(connectionId, out var s))
            {
                return null;
            }
            _bySession.TryRemove(s.SessionId, out _);
            s.Dispose();
            return s;
        }
    }
}
