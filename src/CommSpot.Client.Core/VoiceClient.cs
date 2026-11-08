using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using CommSpot.Protocol;
using Concentus;
using Concentus.Enums;
using Microsoft.AspNetCore.SignalR.Client;

namespace CommSpot.Client.Core;

/// <summary>
/// Everything except the actual sound card: TLS control channel, encrypted UDP voice, Opus, mixing.
/// The desktop app feeds it mic frames and pulls speaker frames; tests feed it synthetic audio.
/// </summary>
public sealed class VoiceClient : IAsyncDisposable, IAudioSink
{
    private readonly ConcurrentDictionary<int, Member> _members = new();
    private readonly ConcurrentDictionary<int, Peer> _peers = new();
    private readonly VoiceActivity _vad = new();
    private readonly object _sendGate = new();
    private HubConnection? _hub;
    private Socket? _udp;
    private VoiceCrypto? _crypto;
    private readonly ReplayWindow _replay = new();
    private readonly object _replayGate = new();
    private IOpusEncoder? _encoder;
    private uint _sessionId;
    private ulong _sendCounter;
    private CancellationTokenSource? _cts;
    private long _selfLastVoice;

    public int UserId { get; private set; }
    public bool Connected { get; private set; }
    public bool Muted { get; private set; }
    public bool Deafened { get; set; }
    public VoiceActivity Activity => _vad;

    /// <summary>Mic/speaker volume, meter and loopback. Can be shared with the settings window.</summary>
    public MicProcessor Mic { get; set; } = new();

    /// <summary>Keep sending this long after the push-to-talk key is released, so word endings aren't cut.</summary>
    public int PushToTalkReleaseMs { get; set; } = 200;
    private volatile bool _pttHeld;
    private long _pttReleasedAt;

    /// <summary>When true the mic only sends while <see cref="PushToTalkHeld"/> (plus a short release tail); otherwise voice activity decides.</summary>
    public bool PushToTalk { get; set; }

    /// <summary>Set from the push-to-talk key state (any thread).</summary>
    public bool PushToTalkHeld
    {
        get => _pttHeld;
        set
        {
            if (_pttHeld && !value)
            {
                Interlocked.Exchange(ref _pttReleasedAt, Environment.TickCount64);
            }
            _pttHeld = value;
        }
    }

    /// <summary>Raised (on a background thread) whenever the member list or a mute flag changes.</summary>
    public event Action? MembersChanged;

    /// <summary>Raised if the control connection drops.</summary>
    public event Action<string>? Disconnected;

    public IReadOnlyCollection<Member> Members => _members.Values.OrderBy(m => m.UserId).ToList();

    /// <summary>True if this member's audio arrived in the last 300 ms (or, for yourself, your mic is active).</summary>
    public bool IsSpeaking(int userId)
    {
        long last = userId == UserId ? Interlocked.Read(ref _selfLastVoice)
            : _peers.TryGetValue(userId, out var p) ? Interlocked.Read(ref p.LastHeard) : 0;
        return last != 0 && Environment.TickCount64 - last < 300;
    }

    /// <summary>
    /// <paramref name="server"/> is a host name (uses https://host/hub) or a full URL (tests use http://127.0.0.1:port).
    /// </summary>
    public async Task<string?> ConnectAsync(string server, string accessKey, string displayName, CancellationToken ct = default)
    {
        Uri hubUrl;
        try
        {
            hubUrl = server.Contains("://", StringComparison.Ordinal)
                ? new Uri(new Uri(server.Trim()), HubNames.Path)
                : new Uri($"https://{server.Trim().TrimEnd('/')}{HubNames.Path}");
        }
        catch (UriFormatException)
        {
            return "That server address isn't valid. Check Settings > Connection.";
        }
        bool loopback = IPAddress.TryParse(hubUrl.Host, out var ip) && IPAddress.IsLoopback(ip);
        if (hubUrl.Scheme != Uri.UriSchemeHttps && !loopback)
        {
            return "Refusing an unencrypted connection to a remote server.";
        }

        _hub = new HubConnectionBuilder().WithUrl(hubUrl).Build();
        _hub.On<Member>(HubNames.MemberJoined, m => { _members[m.UserId] = m; MembersChanged?.Invoke(); });
        _hub.On<int>(HubNames.MemberLeft, id =>
        {
            _members.TryRemove(id, out _);
            _peers.TryRemove(id, out _);
            MembersChanged?.Invoke();
        });
        _hub.On<int, bool>(HubNames.MemberMuted, (id, muted) =>
        {
            if (_members.TryGetValue(id, out var m))
            {
                _members[id] = m with { Muted = muted };
                MembersChanged?.Invoke();
            }
        });
        _hub.On<int, bool>(HubNames.MemberSharing, (id, sharing) =>
        {
            if (_members.TryGetValue(id, out var m))
            {
                _members[id] = m with { Sharing = sharing };
                if (!sharing && Watching == id)
                {
                    Watching = 0;
                    WatchEnded?.Invoke(id);
                }
                MembersChanged?.Invoke();
            }
        });
        _hub.Closed += ex =>
        {
            Connected = false;
            Disconnected?.Invoke(ex?.Message ?? "Disconnected.");
            return Task.CompletedTask;
        };

        await _hub.StartAsync(ct).ConfigureAwait(false);
        var result = await _hub.InvokeAsync<JoinResult>(HubNames.Join, new JoinRequest(accessKey, displayName), ct)
            .ConfigureAwait(false);
        if (!result.Ok || result.VoiceKey is null || result.VoiceHost is null)
        {
            await _hub.StopAsync(ct).ConfigureAwait(false);
            return result.Error ?? "Could not join.";
        }

        UserId = result.UserId;
        _sessionId = result.SessionId;
        _crypto = new VoiceCrypto(result.VoiceKey);
        Array.Clear(result.VoiceKey);
        foreach (var m in result.Members)
        {
            _members[m.UserId] = m;
        }
        _members[UserId] = new Member(UserId, displayName.Trim(), false);

        var addresses = await Dns.GetHostAddressesAsync(result.VoiceHost, ct).ConfigureAwait(false);
        var target = addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).First();
        _udp = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        await _udp.ConnectAsync(new IPEndPoint(target, result.VoicePort), ct).ConfigureAwait(false);

        _encoder = OpusCodecFactory.CreateEncoder(VoiceConstants.SampleRate, VoiceConstants.Channels, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = VoiceConstants.OpusBitrate;

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
        _ = Task.Run(() => KeepAliveLoopAsync(_cts.Token), CancellationToken.None);
        SendSealed(ReadOnlySpan<byte>.Empty); // register our UDP endpoint right away

        Connected = true;
        MembersChanged?.Invoke();
        return null;
    }

    public async Task SetMutedAsync(bool muted)
    {
        Muted = muted;
        if (_members.TryGetValue(UserId, out var me))
        {
            _members[UserId] = me with { Muted = muted };
        }
        MembersChanged?.Invoke();
        if (_hub is { State: HubConnectionState.Connected })
        {
            await _hub.InvokeAsync(HubNames.SetMuted, muted).ConfigureAwait(false);
        }
    }

    // ---- Screen sharing ----
    private VideoAssembler _video = new();
    private uint _videoFrameId;
    private volatile int _watching;

    /// <summary>The member whose screen you're watching, or 0.</summary>
    public int Watching
    {
        get => _watching;
        private set => _watching = value;
    }

    public bool Sharing { get; private set; }

    /// <summary>A complete encoded frame (JPEG) from the member you're watching. Background thread.</summary>
    public event Action<int, byte[]>? VideoFrameReceived;

    /// <summary>The member you were watching stopped sharing or left.</summary>
    public event Action<int>? WatchEnded;

    public async Task SetSharingAsync(bool sharing)
    {
        Sharing = sharing;
        if (_members.TryGetValue(UserId, out var me))
        {
            _members[UserId] = me with { Sharing = sharing };
        }
        MembersChanged?.Invoke();
        if (_hub is { State: HubConnectionState.Connected })
        {
            await _hub.InvokeAsync(HubNames.SetSharing, sharing).ConfigureAwait(false);
        }
    }

    /// <summary>Watch a member's screen (0 stops watching).</summary>
    public async Task WatchAsync(int userId)
    {
        Watching = userId;
        _video = new VideoAssembler();
        if (_hub is { State: HubConnectionState.Connected })
        {
            await _hub.InvokeAsync(HubNames.Watch, userId).ConfigureAwait(false);
        }
    }

    /// <summary>Sends one encoded frame to everyone watching you. Returns the number of packets sent.</summary>
    public int SendVideoFrame(ReadOnlyMemory<byte> encoded)
    {
        if (!Connected || !Sharing)
        {
            return 0;
        }
        int n = 0;
        foreach (var payload in VideoFragments.Split(++_videoFrameId, encoded))
        {
            SendSealed(payload, VoiceConstants.VideoVersion);
            n++;
        }
        return n;
    }

    /// <summary>Feed exactly one 20 ms mono frame (960 samples at 48 kHz) from the microphone.</summary>
    public void SubmitMicFrame(ReadOnlySpan<short> frame)
    {
        if (frame.Length != VoiceConstants.FrameSamples)
        {
            return;
        }
        Span<short> pcm = stackalloc short[frame.Length];
        Mic.Process(frame, pcm); // volume, meter, hear-yourself
        if (!Connected || Muted || _encoder is null)
        {
            return;
        }
        bool send = PushToTalk
            ? _pttHeld || Environment.TickCount64 - Interlocked.Read(ref _pttReleasedAt) < PushToTalkReleaseMs
            : _vad.IsVoice(pcm);
        if (!send)
        {
            return;
        }
        Interlocked.Exchange(ref _selfLastVoice, Environment.TickCount64);
        Span<byte> opus = stackalloc byte[1000];
        int len;
        lock (_sendGate)
        {
            len = _encoder.Encode(pcm, VoiceConstants.FrameSamples, opus, opus.Length);
        }
        if (len > 0)
        {
            SendSealed(opus[..len]);
        }
    }

    /// <summary>Fill one 20 ms speaker frame by mixing everyone's audio. Silence if deafened or nobody talks.</summary>
    public void ReadSpeakerFrame(Span<short> output)
    {
        output.Clear();
        if (Deafened)
        {
            return;
        }
        Span<int> acc = stackalloc int[output.Length];
        bool any = Mic.MixLoopback(acc);
        foreach (var peer in _peers.Values)
        {
            var f = peer.Buffer.Pop();
            if (f is null)
            {
                continue;
            }
            any = true;
            for (int i = 0; i < output.Length && i < f.Length; i++)
            {
                acc[i] += f[i];
            }
        }
        if (!any)
        {
            return;
        }
        Mic.WriteOut(acc, output);
    }

    private void SendSealed(ReadOnlySpan<byte> payload, byte version = VoiceConstants.Version)
    {
        if (_crypto is null || _udp is null)
        {
            return;
        }
        Span<byte> packet = stackalloc byte[VoiceConstants.MaxPacketSize];
        int n;
        lock (_sendGate)
        {
            n = _crypto.Seal(VoiceDirection.ClientToServer, _sessionId, ++_sendCounter, payload, packet, version);
        }
        try
        {
            _udp.Send(packet[..n]);
        }
        catch (SocketException)
        {
            // Transient (e.g. ICMP unreachable). Voice is best-effort.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(VoiceConstants.KeepAliveSeconds));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            SendSealed(ReadOnlySpan<byte>.Empty);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[VoiceConstants.MaxPacketSize + 1];
        var plain = new byte[VoiceConstants.MaxPayloadSize];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await _udp!.ReceiveAsync(buf, SocketFlags.None, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            HandlePacket(buf.AsSpan(0, n), plain);
        }
    }

    // A malicious or broken server is untrusted input too: same checks as the server applies.
    private void HandlePacket(ReadOnlySpan<byte> packet, Span<byte> plain)
    {
        if (!VoiceCrypto.TryReadHeader(packet, out byte version, out uint sid, out ulong counter) || sid != _sessionId)
        {
            return;
        }
        if (!_crypto!.TryOpen(VoiceDirection.ServerToClient, packet, plain, out int len)
            || len <= VoiceConstants.SenderIdSize)
        {
            return;
        }
        lock (_replayGate)
        {
            if (!_replay.TryAccept(counter))
            {
                return;
            }
        }
        int sender = BinaryPrimitives.ReadInt32BigEndian(plain);
        if (sender == UserId || !_members.ContainsKey(sender))
        {
            return;
        }
        if (version == VoiceConstants.VideoVersion)
        {
            if (sender == Watching)
            {
                var frame = _video.Add(plain[VoiceConstants.SenderIdSize..len]);
                if (frame is not null)
                {
                    VideoFrameReceived?.Invoke(sender, frame);
                }
            }
            return;
        }
        var peer = _peers.GetOrAdd(sender, _ => new Peer());
        var pcm = new short[VoiceConstants.FrameSamples];
        int samples;
        try
        {
            samples = peer.Decoder.Decode(plain[VoiceConstants.SenderIdSize..len], pcm, VoiceConstants.FrameSamples);
        }
        catch (ArgumentException)
        {
            return; // malformed Opus
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or InvalidOperationException or Concentus.OpusException)
        {
            return; // a bad packet from another member must not stop voice
        }
        if (samples != VoiceConstants.FrameSamples)
        {
            return;
        }
        Interlocked.Exchange(ref peer.LastHeard, Environment.TickCount64);
        peer.Buffer.Push(pcm);
    }

    public async ValueTask DisposeAsync()
    {
        Connected = false;
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
        _udp?.Dispose();
        if (_hub is not null)
        {
            await _hub.DisposeAsync().ConfigureAwait(false);
        }
        lock (_sendGate)
        {
            _crypto?.Dispose();
            _crypto = null;
        }
        _encoder?.Dispose();
    }

    private sealed class Peer
    {
        public readonly IOpusDecoder Decoder = OpusCodecFactory.CreateDecoder(VoiceConstants.SampleRate, VoiceConstants.Channels);
        public readonly JitterBuffer Buffer = new();
        public long LastHeard;
    }
}
