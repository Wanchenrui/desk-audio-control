using System;
using System.Collections.Generic;

// Offline cases for EndpointCache. Handles are plain objects here, so no COM or audio device is involved.
public static class EndpointCacheTest
{
    sealed class Handle { public string Id; public int Serial; public bool Dead, Closed; }
    static int assertions;
    static void Check(bool result, string scenario)
    {
        if (!result) throw new Exception("Failed: " + scenario);
        Console.WriteLine("PASS " + scenario);
        assertions++;
    }
    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return true; }
        return false;
    }
    public static int Main()
    {
        var opened = new List<Handle>();
        var cache = new EndpointCache<Handle>(
            endpoint =>
            {
                if (endpoint == "bad") throw new InvalidOperationException("cannot open");
                var h = new Handle { Id = endpoint, Serial = opened.Count };
                opened.Add(h);
                return h;
            },
            h => h.Closed = true);
        Func<Handle, int> serial = h =>
        {
            if (h.Dead) throw new InvalidOperationException("stale");
            return h.Serial;
        };

        Check(cache.Use("a", serial) == 0 && opened.Count == 1, "first read opens one handle");
        Check(cache.Use("a", serial) == 0 && opened.Count == 1, "same endpoint reuses the open handle");
        Check(cache.Use("A", serial) == 0 && opened.Count == 1, "endpoint ids compare without case");
        Check(cache.Use("b", serial) == 1 && opened.Count == 2 && opened[0].Closed && !opened[1].Closed,
              "a new endpoint closes the old handle and opens another");
        opened[1].Dead = true;
        Check(cache.Use("b", serial) == 2 && opened.Count == 3 && opened[1].Closed,
              "a stale handle is replaced and the read retried once");

        int before = opened.Count;
        Check(Throws<InvalidOperationException>(() => cache.Use<int>("b", h => { throw new InvalidOperationException("gone"); }))
              && opened.Count == before + 1 && opened[before].Closed && opened[before - 1].Closed,
              "a read that fails on a fresh handle surfaces the error and drops the handle");
        Check(cache.Use("b", serial) == 4 && opened.Count == 5, "the next read after a failure opens a new handle");

        Check(Throws<InvalidOperationException>(() => cache.Use("bad", serial)) && opened[4].Closed,
              "a failed open surfaces the error and leaves no handle behind");
        Check(cache.Use("a", serial) == 5 && opened.Count == 6, "the cache is usable after a failed open");
        Check(Throws<ArgumentNullException>(() => cache.Use(null, serial)) && opened[5].Closed, "an empty endpoint closes the handle");

        cache.Use("a", serial);
        int count = opened.Count;
        cache.Reset();
        Check(opened[count - 1].Closed, "reset closes the handle");
        cache.Use("a", serial);
        Check(opened.Count == count + 1, "a read after reset opens again");
        var last = opened[opened.Count - 1];
        cache.Dispose();
        Check(last.Closed, "dispose closes the handle");

        var shaky = new EndpointCache<Handle>(endpoint => new Handle { Id = endpoint }, h => { throw new InvalidOperationException("release"); });
        shaky.Use("x", serial);
        Check(shaky.Use("y", serial) == 0, "a failing release does not stop the next read");

        Console.WriteLine(assertions + " endpoint cache checks passed.");
        return 0;
    }
}
