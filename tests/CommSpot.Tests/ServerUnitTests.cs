using CommSpot.Server;
using Xunit;

namespace CommSpot.Tests;

public class ServerUnitTests
{
    private const string Key = "correct-horse-battery-staple-42";

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

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
