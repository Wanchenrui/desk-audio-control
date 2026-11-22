using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

[Flags]
internal enum ScanReason { None = 0, Startup = 1, Added = 2, Removed = 4, State = 8, Default = 16, Property = 32, Poll = 64, Refresh = 128 }

internal sealed class AudioEventBus
{
    readonly object sync = new object();
    ScanReason pending;
    long firstTick;
    readonly HashSet<string> dirty=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    bool full;
    public event Action Wake;

    public void Publish(ScanReason reason,string endpoint=null)
    {
        bool notify;
        lock (sync)
        {
            notify = pending == ScanReason.None;
            if (notify) firstTick = Stopwatch.GetTimestamp();
            pending |= reason;
            if (endpoint==null || dirty.Count>=256) full=true;
            else if (!full) dirty.Add(endpoint);
        }
        var wake = Wake;
        if (notify && wake != null) wake();
    }

    public ScanReason Drain(out long since)
    {
        string[] ids; return Drain(out since,out ids);
    }
    public ScanReason Drain(out long since,out string[] ids)
    {
        lock (sync) {
            since=firstTick; var result=pending;
            ids=full ? null : dirty.ToArray();
            pending=ScanReason.None; dirty.Clear(); full=false;
            return result;
        }
    }
}

internal sealed class OutputArrivals
{
    HashSet<string> previous;
    public string[] Observe(IEnumerable<string> connected)
    {
        var current = new HashSet<string>(connected,StringComparer.OrdinalIgnoreCase);
        var added = previous == null ? new string[0] : current.Where(k => !previous.Contains(k)).ToArray();
        previous = current;
        return added;
    }
}
