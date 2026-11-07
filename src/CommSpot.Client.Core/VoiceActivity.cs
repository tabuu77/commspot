namespace CommSpot.Client.Core;

/// <summary>Simple energy-based voice detection with a hangover so word endings aren't clipped.</summary>
public sealed class VoiceActivity
{
    private const int HangoverFrames = 15; // 300 ms
    private int _hang;

    /// <summary>RMS threshold on 16-bit samples (about -45 dBFS by default).</summary>
    public double Threshold { get; set; } = 180;

    /// <summary>When true the threshold follows the background noise level automatically.</summary>
    public bool Auto { get; set; }

    private double _noiseFloor = 60;

    public bool IsVoice(ReadOnlySpan<short> frame)
    {
        double sum = 0;
        foreach (short s in frame)
        {
            sum += (double)s * s;
        }
        double rms = Math.Sqrt(sum / Math.Max(1, frame.Length));
        if (Auto)
        {
            // Track the quiet level: fall fast, rise slowly, ignore speech-level frames.
            _noiseFloor = rms < _noiseFloor ? (_noiseFloor * 0.9) + (rms * 0.1)
                : rms < _noiseFloor * 3 ? (_noiseFloor * 0.995) + (rms * 0.005) : _noiseFloor;
            Threshold = Math.Clamp((_noiseFloor * 3) + 40, 80, 1500);
        }
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
