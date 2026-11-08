using CommSpot.Client.Core;
using CommSpot.Protocol;

using SkiaSharp;

// Test helper: joins a room and plays a soft beep every 2 seconds, so one person can check audio alone.
// With --share it also shares a moving test pattern, so one person can check watching a screen.
// Usage: CommSpot.Bot <server> <name> [--share]   (access key is read from the COMMSPOT_ACCESS_KEY environment variable)
if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: CommSpot.Bot <server> <name>  (set COMMSPOT_ACCESS_KEY)");
    return 2;
}
string key = Environment.GetEnvironmentVariable("COMMSPOT_ACCESS_KEY") ?? string.Empty;

await using var client = new VoiceClient();
string? err = await client.ConnectAsync(args[0], key, args[1]).ConfigureAwait(false);
if (err is not null)
{
    Console.Error.WriteLine("Join failed: " + err);
    return 1;
}
Console.WriteLine($"Joined as {args[1]} (user {client.UserId}). Ctrl+C to quit.");
bool share = args.Contains("--share");
if (share)
{
    await client.SetSharingAsync(true).ConfigureAwait(false);
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(VoiceConstants.FrameMs));
var frame = new short[VoiceConstants.FrameSamples];
long n = 0;
try
{
    while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
    {
        bool beep = n % 100 < 15; // 300 ms beep every 2 s
        for (int i = 0; i < frame.Length; i++)
        {
            double t = (n * frame.Length + i) / (double)VoiceConstants.SampleRate;
            frame[i] = beep ? (short)(4000 * Math.Sin(2 * Math.PI * 660 * t)) : (short)0;
        }
        client.SubmitMicFrame(frame);
        if (share && n % 5 == 0)
        {
            client.SendVideoFrame(TestPattern(n / 5));
        }
        n++;
    }
}
catch (OperationCanceledException)
{
}
return 0;

static byte[] TestPattern(long i)
{
    using var bmp = new SKBitmap(1280, 720);
    using var canvas = new SKCanvas(bmp);
    canvas.Clear(new SKColor(0x1B, 0x17, 0x14));
    using var bar = new SKPaint { Color = new SKColor(0xF2, 0xB5, 0x44) };
    canvas.DrawRect((i * 12) % 1280, 300, 120, 120, bar);
    using var font = new SKFont { Size = 64 };
    using var text = new SKPaint { Color = SKColors.White };
    canvas.DrawText($"CommSpot test pattern  #{i}", 60, 140, font, text);
    using var img = SKImage.FromBitmap(bmp);
    using var data = img.Encode(SKEncodedImageFormat.Jpeg, 70);
    return data.ToArray();
}
