using Microsoft.AspNetCore.SignalR.Client;
using System.Net;
using System.Net.Sockets;
using CommSpot.Client.Core;
using CommSpot.Protocol;
using CommSpot.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CommSpot.Tests;

/// <summary>Real server on loopback, two real clients, synthetic audio: proves voice goes A -> server -> B.</summary>
public sealed class EndToEndTests : IAsyncLifetime
{
    private const string Key = "end-to-end-test-access-key-123"; // gitleaks:allow (test-only value)
    private WebApplication _app = null!;
    private string _url = null!;

    public async Task InitializeAsync()
    {
        _app = CommSpotServer.Build([], b =>
        {
            b.WebHost.UseUrls("http://127.0.0.1:0");
            b.Configuration["AccessKey"] = Key;
            b.Configuration["VoiceHost"] = "127.0.0.1";
            b.Configuration["VoicePort"] = "0";
        });
        await _app.StartAsync();
        _url = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static short[] Tone(int frame)
    {
        var f = new short[VoiceConstants.FrameSamples];
        for (int i = 0; i < f.Length; i++)
        {
            double t = (frame * f.Length + i) / (double)VoiceConstants.SampleRate;
            f[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * t));
        }
        return f;
    }

    private static async Task<double> TalkAndListen(VoiceClient talker, VoiceClient listener, int frames = 50)
    {
        double peak = 0;
        var outFrame = new short[VoiceConstants.FrameSamples];
        for (int i = 0; i < frames; i++)
        {
            talker.SubmitMicFrame(Tone(i));
            await Task.Delay(20);
            listener.ReadSpeakerFrame(outFrame);
            foreach (short s in outFrame)
            {
                peak = Math.Max(peak, Math.Abs((int)s));
            }
        }
        return peak;
    }

    [Fact]
    public async Task VoiceTravelsBetweenTwoClients_AndMuteStopsIt()
    {
        await using var a = new VoiceClient();
        await using var b = new VoiceClient();
        Assert.Null(await a.ConnectAsync(_url, Key, "Alice"));
        Assert.Null(await b.ConnectAsync(_url, Key, "Bob"));
        await Task.Delay(300); // presence + UDP endpoint registration

        Assert.Equal(2, a.Members.Count);
        Assert.Equal(2, b.Members.Count);

        double peak = await TalkAndListen(a, b);
        Assert.True(peak > 2000, $"Bob should hear Alice (peak {peak}).");
        Assert.True(b.IsSpeaking(a.UserId));

        await a.SetMutedAsync(true);
        await Task.Delay(300);
        var drain = new short[VoiceConstants.FrameSamples];
        for (int i = 0; i < 20; i++)
        {
            b.ReadSpeakerFrame(drain); // play out what was already buffered before the mute
        }
        Assert.True(b.Members.Single(m => m.UserId == a.UserId).Muted);
        double mutedPeak = await TalkAndListen(a, b);
        Assert.Equal(0, mutedPeak);

        await a.SetMutedAsync(false);
        b.Deafened = true;
        Assert.Equal(0, await TalkAndListen(a, b));
    }

    [Fact]
    public async Task PushToTalk_SendsOnlyWhileHeld()
    {
        await using var a = new VoiceClient { PushToTalk = true };
        await using var b = new VoiceClient();
        Assert.Null(await a.ConnectAsync(_url, Key, "Alice"));
        Assert.Null(await b.ConnectAsync(_url, Key, "Bob"));
        await Task.Delay(300);

        Assert.Equal(0, await TalkAndListen(a, b)); // key not held: loud tone must not go out

        a.PushToTalkHeld = true;
        Assert.True(await TalkAndListen(a, b) > 2000, "Bob should hear Alice while the key is held.");

        a.PushToTalkHeld = false;
        await Task.Delay(a.PushToTalkReleaseMs + 300);
        var drain = new short[VoiceConstants.FrameSamples];
        for (int i = 0; i < 20; i++)
        {
            b.ReadSpeakerFrame(drain);
        }
        Assert.Equal(0, await TalkAndListen(a, b));
    }

    [Fact]
    public async Task ScreenShare_ReachesOnlyWatchers()
    {
        await using var a = new VoiceClient();
        await using var b = new VoiceClient();
        await using var c = new VoiceClient();
        Assert.Null(await a.ConnectAsync(_url, Key, "Alice"));
        Assert.Null(await b.ConnectAsync(_url, Key, "Bob"));
        Assert.Null(await c.ConnectAsync(_url, Key, "Carol"));
        var bFrames = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
        var cFrames = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
        b.VideoFrameReceived += (_, f) => bFrames.Enqueue(f);
        c.VideoFrameReceived += (_, f) => cFrames.Enqueue(f);
        await Task.Delay(300);

        var frame = new byte[60_000];
        new Random(7).NextBytes(frame);

        // Not sharing yet: the server drops video, and Bob can't watch someone who isn't live.
        await b.WatchAsync(a.UserId);
        Assert.Equal(0, a.SendVideoFrame(frame));

        await a.SetSharingAsync(true);
        await Task.Delay(200);
        Assert.True(b.Members.Single(m => m.UserId == a.UserId).Sharing);
        await b.WatchAsync(a.UserId);
        await Task.Delay(100);
        Assert.True(a.SendVideoFrame(frame) > 40);
        await Task.Delay(500);

        Assert.True(bFrames.TryDequeue(out var got), "Bob is watching and should get the frame.");
        Assert.Equal(frame, got);
        Assert.Empty(cFrames); // Carol isn't watching

        await a.SetSharingAsync(false);
        await Task.Delay(200);
        Assert.Equal(0, b.Watching);
    }

    [Theory]
    [InlineData("not a host")]
    [InlineData("http://")]
    [InlineData("http://example.com")]
    public async Task BadOrPlainHttpServerAddress_IsRefusedWithoutCrashing(string server)
    {
        await using var c = new VoiceClient();
        var err = await c.ConnectAsync(server, Key, "Mallory");
        Assert.NotNull(err);
        Assert.False(c.Connected);
    }

    [Fact]
    public async Task ConnectionWithoutKey_HearsNoRoomEvents()
    {
        await using var spy = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(_url), HubNames.Path)).Build();
        int events = 0;
        spy.On<Member>(HubNames.MemberJoined, _ => Interlocked.Increment(ref events));
        spy.On<int>(HubNames.MemberLeft, _ => Interlocked.Increment(ref events));
        spy.On<int, bool>(HubNames.MemberMuted, (_, _) => Interlocked.Increment(ref events));
        await spy.StartAsync();

        await using (var a = new VoiceClient())
        {
            Assert.Null(await a.ConnectAsync(_url, Key, "Alice"));
            await a.SetMutedAsync(true);
            await Task.Delay(300);
        }
        await Task.Delay(300);
        Assert.Equal(0, Volatile.Read(ref events));
    }

    [Fact]
    public async Task DuplicateName_IsRefused()
    {
        await using var a = new VoiceClient();
        await using var b = new VoiceClient();
        Assert.Null(await a.ConnectAsync(_url, Key, "Alice"));
        Assert.NotNull(await b.ConnectAsync(_url, Key, "alice"));
    }

    [Fact]
    public async Task WrongAccessKey_IsRejected()
    {
        await using var c = new VoiceClient();
        var err = await c.ConnectAsync(_url, "definitely-not-the-right-key-xx", "Mallory");
        Assert.NotNull(err);
        Assert.False(c.Connected);
    }

    [Fact]
    public async Task GarbageAndForgedUdp_IsIgnoredAndServerKeepsWorking()
    {
        await using var a = new VoiceClient();
        await using var b = new VoiceClient();
        Assert.Null(await a.ConnectAsync(_url, Key, "Alice"));
        Assert.Null(await b.ConnectAsync(_url, Key, "Bob"));

        int port = await _app.Services.GetRequiredService<UdpRelay>().BoundPort;
        using var attacker = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, port);
        var rnd = new Random(1);
        for (int i = 0; i < 2000; i++)
        {
            var junk = new byte[rnd.Next(0, 1500)];
            rnd.NextBytes(junk);
            if (junk.Length > 0 && i % 2 == 0)
            {
                junk[0] = VoiceConstants.Version; // looks valid structurally, fails auth
            }
            await attacker.SendAsync(junk, target);
        }

        await Task.Delay(200);
        double peak = await TalkAndListen(a, b);
        Assert.True(peak > 2000, "Voice must still work after a flood of forged packets.");
    }

    [Fact]
    public async Task InvalidName_IsRejected()
    {
        await using var c = new VoiceClient();
        Assert.NotNull(await c.ConnectAsync(_url, Key, "<b>bad</b>"));
    }
}
