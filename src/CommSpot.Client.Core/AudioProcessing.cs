namespace CommSpot.Client.Core;

/// <summary>Something the sound card feeds 20 ms mic frames to and pulls 20 ms speaker frames from.</summary>
public interface IAudioSink
{
    void SubmitMicFrame(ReadOnlySpan<short> frame);

    void ReadSpeakerFrame(Span<short> output);
}

/// <summary>Local mic settings shared by a call and the mic test: gain, level meter and "hear yourself".</summary>
public sealed class MicProcessor
{
    private readonly JitterBuffer _loopback = new();
    private double _level;

    /// <summary>Mic volume, 0 to 2 (100% = 1).</summary>
    public double Gain { get; set; } = 1;

    /// <summary>Speaker volume, 0 to 2.</summary>
    public double SpeakerGain { get; set; } = 1;

    /// <summary>When true, your own mic is played back to your speakers (mic test).</summary>
    public bool Loopback { get; set; }

    /// <summary>Last mic level after gain, 0 to 1 (for the meter).</summary>
    public double Level => Volatile.Read(ref _level);

    /// <summary>RMS of the last frame on the same scale as the sensitivity threshold.</summary>
    public double LastRms { get; private set; }

    /// <summary>Applies gain into <paramref name="dest"/>, updates the meter and the loopback.</summary>
    public void Process(ReadOnlySpan<short> frame, Span<short> dest)
    {
        double g = Math.Clamp(Gain, 0, 2);
        double sum = 0;
        for (int i = 0; i < frame.Length; i++)
        {
            int v = (int)Math.Round(frame[i] * g);
            short s = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
            dest[i] = s;
            sum += (double)s * s;
        }
        LastRms = Math.Sqrt(sum / Math.Max(1, frame.Length));
        // Meter: map RMS logarithmically, 20 (silence) .. 8000 (loud) -> 0..1.
        double lvl = LastRms <= 20 ? 0 : Math.Clamp(Math.Log(LastRms / 20) / Math.Log(400), 0, 1);
        Volatile.Write(ref _level, lvl);
        if (Loopback)
        {
            _loopback.Push(dest.ToArray());
        }
    }

    /// <summary>Adds the loopback (if on) into an int mix buffer. Returns true if anything was added.</summary>
    public bool MixLoopback(Span<int> acc)
    {
        if (!Loopback)
        {
            return false;
        }
        var f = _loopback.Pop();
        if (f is null)
        {
            return false;
        }
        for (int i = 0; i < acc.Length && i < f.Length; i++)
        {
            acc[i] += f[i];
        }
        return true;
    }

    /// <summary>Writes the mix to the output with speaker volume and clipping.</summary>
    public void WriteOut(ReadOnlySpan<int> acc, Span<short> output)
    {
        double g = Math.Clamp(SpeakerGain, 0, 2);
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = (short)Math.Clamp((int)Math.Round(acc[i] * g), short.MinValue, short.MaxValue);
        }
    }
}

/// <summary>Mic test outside a call: just gain, meter and hear-yourself.</summary>
public sealed class MicTest : IAudioSink
{
    public MicTest(MicProcessor mic) => Mic = mic;

    public MicProcessor Mic { get; }

    public void SubmitMicFrame(ReadOnlySpan<short> frame)
    {
        Span<short> buf = stackalloc short[frame.Length];
        Mic.Process(frame, buf);
    }

    public void ReadSpeakerFrame(Span<short> output)
    {
        output.Clear();
        Span<int> acc = stackalloc int[output.Length];
        if (Mic.MixLoopback(acc))
        {
            Mic.WriteOut(acc, output);
        }
    }
}
