using System.Security.Cryptography;
using System.Text;
using CommSpot.Protocol;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace CommSpot.Server;

/// <summary>Control channel over TLS: join with the access key, receive the voice key, mute state, presence.</summary>
internal sealed partial class VoiceHub(Room room, JoinLimiter limiter, UdpRelay relay, IOptions<ServerOptions> options, ILogger<VoiceHub> log) : Hub
{
    private const string Generic = "Could not join.";

    /// <summary>Only members who joined with the key get room events; a bare connection hears nothing.</summary>
    internal const string RoomGroup = "room";

    public async Task<JoinResult> Join(JoinRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string ip = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var opts = options.Value;

        if (limiter.IsBlocked(ip))
        {
            LogBlocked(log, ip);
            return Fail("Too many attempts. Try again later.");
        }
        if (!KeyMatches(request.AccessKey, opts.AccessKey))
        {
            limiter.RecordFailure(ip);
            LogBadKey(log, ip);
            return Fail(Generic);
        }
        if (!DisplayNames.TryNormalize(request.DisplayName, out string name))
        {
            return Fail("Name must be 1-32 letters, numbers, spaces, - _ or .");
        }

        if (room.Sessions.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return Fail("Someone here already has that name.");
        }
        var session = room.Add(Context.ConnectionId, name, opts.MaxMembers);
        if (session is null)
        {
            return Fail("Room is full or you already joined.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup).ConfigureAwait(false);
        var others = room.Sessions.Where(s => s.UserId != session.UserId).Select(s => s.ToMember()).ToList();
        await Clients.OthersInGroup(RoomGroup).SendAsync(HubNames.MemberJoined, session.ToMember()).ConfigureAwait(false);
        LogJoined(log, session.UserId, ip);

        int port = await relay.BoundPort.ConfigureAwait(false);
        return new JoinResult(true, null, session.UserId, session.SessionId, session.ExportKeyOnce(),
            opts.VoiceHost, port, others);
    }

    public async Task SetMuted(bool muted)
    {
        var session = room.ByConnection(Context.ConnectionId);
        if (session is null)
        {
            return;
        }
        session.Muted = muted;
        await Clients.OthersInGroup(RoomGroup).SendAsync(HubNames.MemberMuted, session.UserId, muted).ConfigureAwait(false);
    }

    public async Task SetSharing(bool sharing)
    {
        var session = room.ByConnection(Context.ConnectionId);
        if (session is null || session.Sharing == sharing)
        {
            return;
        }
        session.Sharing = sharing;
        if (!sharing)
        {
            foreach (var s in room.Sessions.Where(s => s.Watching == session.UserId))
            {
                s.Watching = 0;
            }
        }
        await Clients.OthersInGroup(RoomGroup).SendAsync(HubNames.MemberSharing, session.UserId, sharing).ConfigureAwait(false);
    }

    /// <summary>Start watching a member's screen (0 = stop). Only a member who is sharing can be watched.</summary>
    public void Watch(int userId)
    {
        var session = room.ByConnection(Context.ConnectionId);
        if (session is null)
        {
            return;
        }
        session.Watching = userId != session.UserId && room.Sessions.Any(s => s.UserId == userId && s.Sharing) ? userId : 0;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var removed = room.Remove(Context.ConnectionId);
        if (removed is not null)
        {
            foreach (var s in room.Sessions.Where(s => s.Watching == removed.UserId))
            {
                s.Watching = 0;
            }
            await Clients.OthersInGroup(RoomGroup).SendAsync(HubNames.MemberLeft, removed.UserId).ConfigureAwait(false);
        }
        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    private static JoinResult Fail(string error) => new(false, error, 0, 0, null, null, 0, []);

    /// <summary>Constant-time compare of SHA-256 digests, so length and content don't leak through timing.</summary>
    internal static bool KeyMatches(string? supplied, string expected)
    {
        if (string.IsNullOrEmpty(supplied) || expected.Length < ServerOptions.MinAccessKeyLength)
        {
            return false;
        }
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Join blocked by rate limit from {Ip}")]
    private static partial void LogBlocked(ILogger logger, string ip);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Join with wrong access key from {Ip}")]
    private static partial void LogBadKey(ILogger logger, string ip);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} joined from {Ip}")]
    private static partial void LogJoined(ILogger logger, int userId, string ip);
}
