using CommSpot.Protocol;
using Xunit;

namespace CommSpot.Tests;

public class ProtocolTests
{
    [Fact]
    public void SealThenOpen_RoundTrips()
    {
        using var c = new VoiceCrypto(VoiceCrypto.NewKey());
        var buf = new byte[VoiceConstants.MaxPacketSize];
        byte[] msg = [1, 2, 3, 4, 5];
        int n = c.Seal(VoiceDirection.ClientToServer, 42, 7, msg, buf);
        var outBuf = new byte[VoiceConstants.MaxPayloadSize];
        Assert.True(c.TryOpen(VoiceDirection.ClientToServer, buf.AsSpan(0, n), outBuf, out int len));
        Assert.Equal(msg, outBuf[..len]);
        Assert.True(VoiceCrypto.TryReadHeader(buf.AsSpan(0, n), out uint sid, out ulong ctr));
        Assert.Equal(42u, sid);
        Assert.Equal(7ul, ctr);
    }

    [Fact]
    public void Open_RejectsWrongKeyTamperingAndWrongDirection()
    {
        using var a = new VoiceCrypto(VoiceCrypto.NewKey());
        using var b = new VoiceCrypto(VoiceCrypto.NewKey());
        var buf = new byte[VoiceConstants.MaxPacketSize];
        int n = a.Seal(VoiceDirection.ClientToServer, 1, 1, new byte[20], buf);
        var o = new byte[VoiceConstants.MaxPayloadSize];

        Assert.False(b.TryOpen(VoiceDirection.ClientToServer, buf.AsSpan(0, n), o, out _));     // wrong key
        Assert.False(a.TryOpen(VoiceDirection.ServerToClient, buf.AsSpan(0, n), o, out _));     // reflected
        for (int i = 0; i < n; i++)
        {
            var copy = buf[..n];
            copy[i] ^= 0x01;
            Assert.False(a.TryOpen(VoiceDirection.ClientToServer, copy, o, out _));               // any bit flip, header included
        }
        Assert.False(a.TryOpen(VoiceDirection.ClientToServer, buf.AsSpan(0, n - 1), o, out _)); // truncated
    }

    [Fact]
    public void Header_RejectsBadSizesAndVersion()
    {
        Assert.False(VoiceCrypto.TryReadHeader(new byte[10], out _, out _));
        Assert.False(VoiceCrypto.TryReadHeader(new byte[VoiceConstants.MaxPacketSize + 1], out _, out _));
        var p = new byte[40];
        p[0] = 99;
        Assert.False(VoiceCrypto.TryReadHeader(p, out _, out _));
    }

    [Fact]
    public void Seal_RejectsOversizePayload()
    {
        using var c = new VoiceCrypto(VoiceCrypto.NewKey());
        Assert.Throws<ArgumentException>(() =>
            c.Seal(VoiceDirection.ClientToServer, 1, 1, new byte[VoiceConstants.MaxPayloadSize + 1], new byte[2000]));
    }

    [Fact]
    public void ReplayWindow_AcceptsOnceAndRejectsOld()
    {
        var w = new ReplayWindow();
        Assert.True(w.TryAccept(100));
        Assert.False(w.TryAccept(100));
        Assert.True(w.TryAccept(99));          // late but inside window
        Assert.False(w.TryAccept(99));
        Assert.True(w.TryAccept(5000));        // big jump
        Assert.False(w.TryAccept(100));        // now too old
        Assert.True(w.TryAccept(5000 - 1023)); // edge of window
        Assert.False(w.TryAccept(5000 - 1024));
    }

    [Theory]
    [InlineData("Dude", true)]
    [InlineData("  Crow_77 ", true)]
    [InlineData("", false)]
    [InlineData("<script>", false)]
    [InlineData("a\u0000b", false)]
    [InlineData("123456789012345678901234567890123", false)]
    public void DisplayNames_Validate(string raw, bool ok) => Assert.Equal(ok, DisplayNames.TryNormalize(raw, out _));
}

public sealed class MicProcessorTests
{
    [Fact]
    public void Gain_ScalesAndClips_AndMeterMoves()
    {
        var mic = new CommSpot.Client.Core.MicProcessor { Gain = 2 };
        short[] input = [1000, -1000, 30000, -30000];
        var output = new short[4];
        mic.Process(input, output);
        Assert.Equal(new short[] { 2000, -2000, short.MaxValue, short.MinValue }, output);
        Assert.True(mic.Level > 0.5);
        mic.Gain = 0;
        mic.Process(input, output);
        Assert.All(output, s => Assert.Equal(0, s));
        Assert.Equal(0, mic.Level);
    }

    [Fact]
    public void MicTest_LoopsBackOnlyWhenEnabled()
    {
        var test = new CommSpot.Client.Core.MicTest(new CommSpot.Client.Core.MicProcessor { SpeakerGain = 0.5 });
        var frame = Enumerable.Repeat((short)4000, 960).ToArray();
        var outFrame = new short[960];
        for (int i = 0; i < 5; i++) test.SubmitMicFrame(frame);
        test.ReadSpeakerFrame(outFrame);
        Assert.All(outFrame, s => Assert.Equal(0, s));
        test.Mic.Loopback = true;
        for (int i = 0; i < 5; i++) test.SubmitMicFrame(frame);
        test.ReadSpeakerFrame(outFrame);
        Assert.All(outFrame, s => Assert.Equal(2000, s));
    }

    [Fact]
    public void AutoSensitivity_FollowsNoiseFloor()
    {
        var vad = new CommSpot.Client.Core.VoiceActivity { Auto = true };
        var quiet = Enumerable.Repeat((short)100, 960).ToArray();
        for (int i = 0; i < 300; i++) vad.IsVoice(quiet);
        Assert.InRange(vad.Threshold, 250, 450); // ~3x a noise floor of 100
        var speech = Enumerable.Range(0, 960).Select(i => (short)(i % 2 == 0 ? 3000 : -3000)).ToArray();
        Assert.True(vad.IsVoice(speech));
    }
}
