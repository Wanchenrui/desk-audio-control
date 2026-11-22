using System;

// Decides when a device rescan may start. Requests that arrive close together merge into one scan, a request
// that arrives while a scan runs schedules exactly one more, and a steady stream of requests cannot postpone the
// scan forever. Times are milliseconds on a monotonic clock. Use it from a single thread.
internal sealed class ScanGate
{
    readonly long quiet, longest;
    bool pending, running;
    long firstRequest, lastRequest;

    public ScanGate(long quietMs, long longestWaitMs)
    {
        if (quietMs < 0) throw new ArgumentOutOfRangeException("quietMs");
        if (longestWaitMs < quietMs) throw new ArgumentOutOfRangeException("longestWaitMs");
        quiet = quietMs; longest = longestWaitMs;
    }
    public bool Pending { get { return pending; } }
    public bool Running { get { return running; } }

    public void Request(long now)
    {
        if (!pending) { pending = true; firstRequest = now; }
        lastRequest = now;
    }
    // True when a scan should start now. The caller must call End when that scan finishes.
    public bool TryBegin(long now)
    {
        if (running || !pending) return false;
        if (now - lastRequest < quiet && now - firstRequest < longest) return false;
        pending = false; running = true;
        return true;
    }
    // A scheduled refresh is immediately due unless an endpoint burst is already settling.
    public void Refresh(long now)
    {
        if (pending) return;
        Request(now);
        lastRequest = now - quiet;
    }
    public int Delay(long now)
    {
        long wait=pending ? Math.Min(quiet-(now-lastRequest),longest-(now-firstRequest)) : quiet;
        return (int)Math.Max(1,Math.Min(Int32.MaxValue,wait));
    }
    public void End() { running = false; }
}
