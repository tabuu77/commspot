using System.Collections.Concurrent;

namespace CommSpot.Server;

/// <summary>Limits failed join attempts per client IP: 5 per 15 minutes.</summary>
internal sealed class JoinLimiter(TimeProvider time)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>Above this many tracked IPs, expired entries are dropped so the table can't grow forever.</summary>
    public const int MaxTrackedIps = 10_000;

    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _failures = new();

    public bool IsBlocked(string ip)
    {
        if (!_failures.TryGetValue(ip, out var q))
        {
            return false;
        }
        lock (q)
        {
            Prune(q);
            return q.Count >= MaxFailures;
        }
    }

    public void RecordFailure(string ip)
    {
        if (_failures.Count > MaxTrackedIps)
        {
            Sweep();
        }
        var q = _failures.GetOrAdd(ip, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            Prune(q);
            q.Enqueue(time.GetUtcNow());
        }
    }

    private void Sweep()
    {
        foreach (var (ip, q) in _failures)
        {
            bool empty;
            lock (q)
            {
                Prune(q);
                empty = q.Count == 0;
            }
            if (empty)
            {
                _failures.TryRemove(new KeyValuePair<string, Queue<DateTimeOffset>>(ip, q));
            }
        }
    }

    internal int TrackedIps => _failures.Count;

    private void Prune(Queue<DateTimeOffset> q)
    {
        var cutoff = time.GetUtcNow() - Window;
        while (q.Count > 0 && q.Peek() < cutoff)
        {
            q.Dequeue();
        }
    }
}
