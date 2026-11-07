using System.Runtime.InteropServices;
using System.Threading.Channels;
using CommSpot.Client.Core;
using CommSpot.Protocol;
using PortAudioSharp;

namespace CommSpot.Client;

/// <summary>Sound card glue: mic -> VoiceClient (on a worker thread), VoiceClient -> speakers.</summary>
internal sealed class AudioEngine : IDisposable
{
    private static bool _initialized;
    private readonly VoiceClient _client;
    private readonly Channel<short[]> _micFrames = Channel.CreateBounded<short[]>(
        new BoundedChannelOptions(10) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly short[] _micAccum = new short[VoiceConstants.FrameSamples];
    private int _micFill;
    private readonly short[] _spkFrame = new short[VoiceConstants.FrameSamples];
    private int _spkPos = VoiceConstants.FrameSamples;
    private PortAudioSharp.Stream? _input;
    private PortAudioSharp.Stream? _output;
    private readonly CancellationTokenSource _cts = new();
    private readonly PortAudioSharp.Stream.Callback _inCb;
    private readonly PortAudioSharp.Stream.Callback _outCb;

    public AudioEngine(VoiceClient client)
    {
        _client = client;
        _inCb = OnInput;
        _outCb = OnOutput;
    }

    public static void EnsureInitialized()
    {
        if (!_initialized)
        {
            PortAudio.Initialize();
            _initialized = true;
        }
    }

    public static IReadOnlyList<(int Index, string Name)> Devices(bool input)
    {
        EnsureInitialized();
        var list = new List<(int, string)>();
        for (int i = 0; i < PortAudio.DeviceCount; i++)
        {
            var d = PortAudio.GetDeviceInfo(i);
            if ((input ? d.maxInputChannels : d.maxOutputChannels) > 0)
            {
                list.Add((i, d.name));
            }
        }
        return list;
    }

    public static int DefaultDevice(bool input)
    {
        EnsureInitialized();
        return input ? PortAudio.DefaultInputDevice : PortAudio.DefaultOutputDevice;
    }

    public void Start(int inputDevice, int outputDevice)
    {
        EnsureInitialized();
        _ = Task.Run(() => EncodeLoopAsync(_cts.Token));

        if (inputDevice != PortAudio.NoDevice)
        {
            var p = new StreamParameters
            {
                device = inputDevice,
                channelCount = 1,
                sampleFormat = SampleFormat.Int16,
                suggestedLatency = PortAudio.GetDeviceInfo(inputDevice).defaultLowInputLatency,
                hostApiSpecificStreamInfo = IntPtr.Zero,
            };
            _input = new PortAudioSharp.Stream(p, null, VoiceConstants.SampleRate, VoiceConstants.FrameSamples,
                StreamFlags.ClipOff, _inCb, null);
            _input.Start();
        }
        if (outputDevice != PortAudio.NoDevice)
        {
            var p = new StreamParameters
            {
                device = outputDevice,
                channelCount = 1,
                sampleFormat = SampleFormat.Int16,
                suggestedLatency = PortAudio.GetDeviceInfo(outputDevice).defaultLowOutputLatency,
                hostApiSpecificStreamInfo = IntPtr.Zero,
            };
            _output = new PortAudioSharp.Stream(null, p, VoiceConstants.SampleRate, VoiceConstants.FrameSamples,
                StreamFlags.ClipOff, _outCb, null);
            _output.Start();
        }
    }

    // Runs on PortAudio's real-time thread: copy only, never block.
    private StreamCallbackResult OnInput(IntPtr input, IntPtr output, uint frames, ref StreamCallbackTimeInfo t,
        StreamCallbackFlags flags, IntPtr user)
    {
        if (input == IntPtr.Zero)
        {
            return StreamCallbackResult.Continue;
        }
        int remaining = (int)frames;
        int offset = 0;
        while (remaining > 0)
        {
            int take = Math.Min(remaining, VoiceConstants.FrameSamples - _micFill);
            Marshal.Copy(input + offset * sizeof(short), _micAccum, _micFill, take);
            _micFill += take;
            offset += take;
            remaining -= take;
            if (_micFill == VoiceConstants.FrameSamples)
            {
                _micFrames.Writer.TryWrite((short[])_micAccum.Clone());
                _micFill = 0;
            }
        }
        return StreamCallbackResult.Continue;
    }

    private StreamCallbackResult OnOutput(IntPtr input, IntPtr output, uint frames, ref StreamCallbackTimeInfo t,
        StreamCallbackFlags flags, IntPtr user)
    {
        int remaining = (int)frames;
        int offset = 0;
        while (remaining > 0)
        {
            if (_spkPos == VoiceConstants.FrameSamples)
            {
                _client.ReadSpeakerFrame(_spkFrame);
                _spkPos = 0;
            }
            int take = Math.Min(remaining, VoiceConstants.FrameSamples - _spkPos);
            Marshal.Copy(_spkFrame, _spkPos, output + offset * sizeof(short), take);
            _spkPos += take;
            offset += take;
            remaining -= take;
        }
        return StreamCallbackResult.Continue;
    }

    private async Task EncodeLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _micFrames.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                _client.SubmitMicFrame(frame);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var s in new[] { _input, _output })
        {
            if (s is null)
            {
                continue;
            }
            try
            {
                s.Stop();
            }
            catch (PortAudioException)
            {
            }
            s.Dispose();
        }
        _cts.Dispose();
    }
}
