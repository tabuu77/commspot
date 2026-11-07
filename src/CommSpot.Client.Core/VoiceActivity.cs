namespace CommSpot.Client.Core;

/// <summary>Simple energy-based voice detection with a hangover so word endings aren't clipped.</summary>
public sealed class VoiceActivity
{
    private const int HangoverFrames = 15; // 300 ms
    private int _hang;

    /// <summary>RMS threshold on 16-bit samples (about -45 dBFS by default).</summary>
    public double Threshold { get; set; } = 180;

    public bool IsVoice(ReadOnlySpan<short> frame)
    {
        double sum = 0;
        foreach (short s in frame)
        {
            sum += (double)s * s;
        }
        double rms = Math.Sqrt(sum / Math.Max(1, frame.Length));
        if (rms >= Threshold)
        {
            _hang = HangoverFrames;
            return true;
        }
        if (_hang > 0)
        {
            _hang--;
            return true;
        }
        return false;
    }
}
