using System;

// Keeps one live handle for an endpoint that is read over and over, such as a peak meter polled every few tens of
// milliseconds. The handle is reopened when the endpoint changes or a read fails. Use it from a single thread.
internal sealed class EndpointCache<T> : IDisposable where T : class
{
    readonly Func<string, T> open;
    readonly Action<T> close;
    string current;
    T handle;

    public EndpointCache(Func<string, T> open, Action<T> close)
    {
        if (open == null) throw new ArgumentNullException("open");
        if (close == null) throw new ArgumentNullException("close");
        this.open = open;
        this.close = close;
    }

    public TResult Use<TResult>(string endpointId, Func<T, TResult> read)
    {
        if (String.IsNullOrEmpty(endpointId)) { Reset(); throw new ArgumentNullException("endpointId"); }
        bool reused = handle != null && String.Equals(current, endpointId, StringComparison.OrdinalIgnoreCase);
        if (!reused) { Reset(); Open(endpointId); }
        try { return read(handle); }
        catch (Exception)
        {
            Reset();
            // A handle that stayed open for a while may belong to a device that went away, so retry once with a fresh one.
            if (!reused) throw;
        }
        Open(endpointId);
        try { return read(handle); }
        catch (Exception) { Reset(); throw; }
    }

    public void Reset()
    {
        T old = handle;
        handle = null; current = null;
        if (old == null) return;
        try { close(old); }
        catch (Exception) { } // Releasing is best effort. A stale handle must not block the next read.
    }
    void Open(string endpointId) { handle = open(endpointId); current = endpointId; }
    public void Dispose() { Reset(); }
}
