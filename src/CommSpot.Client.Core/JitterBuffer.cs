namespace CommSpot.Client.Core;

/// <summary>Small per-speaker queue of decoded 20 ms frames. Starts playing after a short prefill.</summary>
internal sealed class JitterBuffer
{
    private const int Prefill = 3;   // 60 ms
    private const int MaxFrames = 10; // 200 ms; older frames are dropped to keep latency low
    private readonly Queue<short[]> _frames = new();
    private bool _playing;

    public void Push(short[] frame)
    {
        lock (_frames)
        {
            _frames.Enqueue(frame);
            while (_frames.Count > MaxFrames)
            {
                _frames.Dequeue();
            }
        }
    }

    public short[]? Pop()
    {
        lock (_frames)
        {
            if (!_playing)
            {
                if (_frames.Count < Prefill)
                {
                    return null;
                }
                _playing = true;
            }
            if (_frames.Count == 0)
            {
                _playing = false; // underrun: prefill again
                return null;
            }
            return _frames.Dequeue();
        }
    }
}
