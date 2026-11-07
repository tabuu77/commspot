using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using CommSpot.Protocol;
using Microsoft.Extensions.Options;

namespace CommSpot.Server;

/// <summary>
/// Receives encrypted voice on UDP, drops anything that fails size, rate, auth or replay checks,
/// and re-encrypts each frame for every other member (hop-by-hop, see SECURITY_DESIGN.md section 4).
/// </summary>
internal sealed partial class UdpRelay(Room room, IOptions<ServerOptions> options, ILogger<UdpRelay> log) : BackgroundService
{
    private Socket? _socket;
    private readonly TaskCompletionSource<int> _bound = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Actual bound port (useful when configured as 0 in tests).</summary>
    public Task<int> BoundPort => _bound.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _socket = OpenSocket(options.Value.VoicePort);
        _bound.TrySetResult(((IPEndPoint)_socket.LocalEndPoint!).Port);
        LogListening(log, ((IPEndPoint)_socket.LocalEndPoint!).Port);

        var recv = new byte[VoiceConstants.MaxPacketSize + 1];
        var plain = new byte[VoiceConstants.MaxPayloadSize];
        var forward = new byte[VoiceConstants.MaxPayloadSize];
        var outPacket = new byte[VoiceConstants.MaxPacketSize];
        EndPoint any = new IPEndPoint(_socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

        while (!stoppingToken.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await _socket.ReceiveFromAsync(recv, SocketFlags.None, any, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue; // e.g. ICMP port unreachable surfaced on Windows; never fatal
            }

            try
            {
                await HandleAsync(recv.AsMemory(0, r.ReceivedBytes), (IPEndPoint)r.RemoteEndPoint, plain, forward, outPacket)
                    .ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Session left mid-packet; drop it.
            }
        }
    }

    private async Task HandleAsync(ReadOnlyMemory<byte> packet, IPEndPoint from, byte[] plain, byte[] forward, byte[] outPacket)
    {
        // 1. Structure (cheap). 2. Known session. 3. Rate limit. 4. AES-GCM auth. 5. Replay. Only then use it.
        if (!VoiceCrypto.TryReadHeader(packet.Span, out uint sessionId, out _))
        {
            return;
        }
        var sender = room.BySession(sessionId);
        if (sender is null || !sender.TryTakeToken())
        {
            return;
        }
        if (!sender.TryOpen(packet.Span, from, plain, out int len))
        {
            return;
        }
        if (len == 0 || sender.Muted)
        {
            return; // keepalive, or muted member
        }

        BinaryPrimitives.WriteInt32BigEndian(forward, sender.UserId);
        plain.AsSpan(0, len).CopyTo(forward.AsSpan(VoiceConstants.SenderIdSize));
        int fwdLen = VoiceConstants.SenderIdSize + len;
        if (fwdLen > VoiceConstants.MaxPayloadSize)
        {
            return;
        }

        foreach (var target in room.Sessions)
        {
            if (target.UserId == sender.UserId || target.Endpoint is not { } ep)
            {
                continue;
            }
            int n = target.Seal(forward.AsSpan(0, fwdLen), outPacket);
            await _socket!.SendToAsync(outPacket.AsMemory(0, n), SocketFlags.None, ep).ConfigureAwait(false);
        }
    }

    /// <summary>Dual-stack when the host has IPv6; plain IPv4 otherwise.</summary>
    private static Socket OpenSocket(int port)
    {
        try
        {
            var v6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
            v6.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            return v6;
        }
        catch (SocketException)
        {
            var v4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            v4.Bind(new IPEndPoint(IPAddress.Any, port));
            return v4;
        }
    }

    public override void Dispose()
    {
        _socket?.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Voice relay listening on UDP {Port}")]
    private static partial void LogListening(ILogger logger, int port);
}
