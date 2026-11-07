using System.Collections.Concurrent;

namespace CommSpot.Server;

/// <summary>Limits failed join attempts per client IP: 5 per 15 minutes.</summary>
internal sealed class JoinLimiter(TimeProvider time)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

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
        var q = _failures.GetOrAdd(ip, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            Prune(q);
            q.Enqueue(time.GetUtcNow());
        }
    }

    private void Prune(Queue<DateTimeOffset> q)
    {
        var cutoff = time.GetUtcNow() - Window;
        while (q.Count > 0 && q.Peek() < cutoff)
        {
            q.Dequeue();
        }
    }
}
