using CommSpot.Server;
using Xunit;

namespace CommSpot.Tests;

public class ServerUnitTests
{
    private const string Key = "correct-horse-battery-staple-42"; // gitleaks:allow (test-only value)

    [Fact]
    public void KeyMatches_OnlyExactKey()
    {
        Assert.True(VoiceHub.KeyMatches(Key, Key));
        Assert.False(VoiceHub.KeyMatches(Key + "x", Key));
        Assert.False(VoiceHub.KeyMatches("", Key));
        Assert.False(VoiceHub.KeyMatches(null, Key));
        Assert.False(VoiceHub.KeyMatches("short", "short")); // server key below minimum never matches
    }

    [Fact]
    public void JoinLimiter_BlocksAfterFiveFailuresThenExpires()
    {
        var time = new FakeTime();
        var l = new JoinLimiter(time);
        for (int i = 0; i < JoinLimiter.MaxFailures; i++)
        {
            Assert.False(l.IsBlocked("1.2.3.4"));
            l.RecordFailure("1.2.3.4");
        }
        Assert.True(l.IsBlocked("1.2.3.4"));
        Assert.False(l.IsBlocked("5.6.7.8"));
        time.Advance(JoinLimiter.Window + TimeSpan.FromSeconds(1));
        Assert.False(l.IsBlocked("1.2.3.4"));
    }

    [Fact]
    public void JoinLimiter_ForgetsExpiredIps_SoTheTableCantGrowForever()
    {
        var time = new FakeTime();
        var l = new JoinLimiter(time);
        for (int i = 0; i <= JoinLimiter.MaxTrackedIps; i++)
        {
            l.RecordFailure("10.0." + i);
        }
        time.Advance(JoinLimiter.Window + TimeSpan.FromSeconds(1));
        l.RecordFailure("9.9.9.9");
        Assert.True(l.TrackedIps <= 2, $"tracked {l.TrackedIps}");
    }

    [Fact]
    public void JpegInfo_ReadsSizeFromHeader_AndRejectsGarbage()
    {
        byte[] Jpeg(int w, int h) =>
        [
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, // APP0 with 2 bytes of data
            0xFF, 0xC0, 0x00, 0x0B, 0x08, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 0x01, 0x01, 0x11, 0x00,
            0xFF, 0xD9,
        ];
        Assert.True(CommSpot.Client.Core.JpegInfo.TryReadSize(Jpeg(1920, 1080), out int w, out int h));
        Assert.Equal((1920, 1080), (w, h));
        Assert.True(CommSpot.Client.Core.JpegInfo.TryReadSize(Jpeg(60000, 60000), out w, out h)); // read, then refused by the viewer
        Assert.Equal(60000, w);
        Assert.False(CommSpot.Client.Core.JpegInfo.TryReadSize([0xFF, 0xD8, 0xFF, 0xD9], out _, out _));
        Assert.False(CommSpot.Client.Core.JpegInfo.TryReadSize([1, 2, 3, 4, 5, 6], out _, out _));
        Assert.False(CommSpot.Client.Core.JpegInfo.TryReadSize([0xFF, 0xD8, 0xFF, 0xC0, 0xFF, 0xFF], out _, out _));
        Assert.False(CommSpot.Client.Core.JpegInfo.TryReadSize([], out _, out _));
    }

    [Fact]
    public void ChatText_CleansControlCharacters_AndLimitsLength()
    {
        Assert.True(CommSpot.Protocol.ChatText.TryNormalize(" a\u0000b\u202Ec\r\nd\te ", out string t));
        Assert.Equal("abc\nd e", t); // control and text-direction characters are gone
        Assert.False(CommSpot.Protocol.ChatText.TryNormalize("  \u0001 ", out _));
        Assert.False(CommSpot.Protocol.ChatText.TryNormalize(null, out _));
        Assert.True(CommSpot.Protocol.ChatText.TryNormalize(new string('y', 3000), out t));
        Assert.Equal(CommSpot.Protocol.ChatText.MaxLength, t.Length);
    }

    [Fact]
    public void ChatStore_SavesKeepsNewestAndSurvivesDamage()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cs-chat-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CommSpot.Client.Core.ChatStore(dir);
            string key = CommSpot.Client.Core.ChatStore.Direct("Bob");
            Assert.Equal(key, CommSpot.Client.Core.ChatStore.Direct(" bob "));
            for (int i = 0; i < CommSpot.Client.Core.ChatStore.MaxEntries + 5; i++)
            {
                store.Append(key, new("Bob", "m" + i, i, false));
            }
            var list = store.Load(key);
            Assert.Equal(CommSpot.Client.Core.ChatStore.MaxEntries, list.Count);
            Assert.Equal("m5", list[0].Text);
            File.WriteAllText(Path.Combine(dir, key + ".json"), "{not json");
            Assert.Empty(store.Load(key));
            Assert.Throws<ArgumentException>(() => store.Load("../evil"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
