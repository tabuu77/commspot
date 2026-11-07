namespace CommSpot.Protocol;

/// <summary>
/// Sliding-window replay filter (like IPsec/DTLS). Accepts each counter at most once and
/// rejects anything older than <see cref="Size"/> behind the highest counter seen.
/// Call <see cref="TryAccept"/> only AFTER the packet authenticated, so forged packets can't move the window.
/// Not thread-safe; callers lock per session.
/// </summary>
public sealed class ReplayWindow
{
    public const int Size = 1024;
    private const int Words = Size / 64;

    private readonly ulong[] _bits = new ulong[Words];
    private ulong _highest;
    private bool _any;

    public bool TryAccept(ulong counter)
    {
        if (!_any)
        {
            _any = true;
            _highest = counter;
            Set(counter);
            return true;
        }

        if (counter > _highest)
        {
            ulong shift = counter - _highest;
            if (shift >= Size)
            {
                Array.Clear(_bits);
            }
            else
            {
                for (ulong c = _highest + 1; c <= counter; c++)
                {
                    Clear(c);
                }
            }
            _highest = counter;
            Set(counter);
            return true;
        }

        if (_highest - counter >= Size)
        {
            return false; // too old
        }
        if (IsSet(counter))
        {
            return false; // duplicate
        }
        Set(counter);
        return true;
    }

    private static (int word, ulong mask) Slot(ulong counter)
    {
        int bit = (int)(counter % Size);
        return (bit / 64, 1UL << (bit % 64));
    }

    private void Set(ulong c) { var (w, m) = Slot(c); _bits[w] |= m; }
    private void Clear(ulong c) { var (w, m) = Slot(c); _bits[w] &= ~m; }
    private bool IsSet(ulong c) { var (w, m) = Slot(c); return (_bits[w] & m) != 0; }
}
