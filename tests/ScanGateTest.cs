using System;

// Offline cases for ScanGate. Time is passed in by hand, so nothing sleeps.
public static class ScanGateTest
{
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
        var idle = new ScanGate(400, 2000);
        Check(!idle.TryBegin(0) && !idle.TryBegin(10000), "nothing starts without a request");

        Check(idle.Delay(10000)==400 && new ScanGate(0,0).Delay(0)==1 &&
            new ScanGate(Int64.MaxValue,Int64.MaxValue).Delay(0)==Int32.MaxValue,"idle delay fits the timer interval");

        var g = new ScanGate(400, 2000);
        g.Request(0);
        Check(!g.TryBegin(399), "a scan waits for the quiet period");
        Check(g.TryBegin(400) && g.Running && !g.Pending, "a scan starts once the quiet period has passed");
        Check(!g.TryBegin(1000), "no second scan starts while one runs");
        g.End();
        Check(!g.TryBegin(2000) && !g.Running, "finishing a scan leaves nothing to do");

        g = new ScanGate(400, 2000);
        g.Request(0); g.Request(100); g.Request(200);
        Check(!g.TryBegin(500) && g.TryBegin(600), "a burst of requests becomes one scan");
        g.End();
        Check(!g.TryBegin(5000), "the burst leaves no extra scan behind");

        g = new ScanGate(400, 2000);
        g.Request(0);
        g.TryBegin(400);
        g.Request(450);
        Check(g.Pending && !g.TryBegin(2000), "a request during a scan waits for it to finish");
        g.End();
        Check(g.TryBegin(2000) && !g.Pending, "a request during a scan schedules exactly one more");
        g.End();
        Check(!g.TryBegin(9000), "the follow-up scan leaves nothing pending");

        g = new ScanGate(400, 2000);
        long began = -1;
        for (long t = 0; t <= 4000 && began < 0; t += 100)
        {
            g.Request(t);
            if (g.TryBegin(t)) began = t;
        }
        Check(began == 2000, "a steady stream of requests cannot postpone the scan forever");

        g = new ScanGate(400, 2000);
        g.End();
        g.Request(0);
        Check(g.TryBegin(400), "ending without a scan is harmless");
        g.End();
        g.Request(5000);
        Check(!g.TryBegin(5100) && g.TryBegin(5400), "each request opens a fresh quiet window");

        g = new ScanGate(400,2000);
        g.Refresh(10000);
        Check(g.TryBegin(10000), "a due fallback poll starts immediately");
        g.Request(10100);
        g.Refresh(10150);
        g.End();
        Check(!g.TryBegin(10499) && g.TryBegin(10500), "a poll or open panel never steals or extends a hardware burst");
        g.End();
        Check(!g.Pending && !g.TryBegin(20000), "a consumed poll creates no extra scan");

        var eager = new ScanGate(0, 0);
        eager.Request(10);
        Check(eager.TryBegin(10), "a zero quiet period starts at once");

        Check(Throws<ArgumentOutOfRangeException>(() => new ScanGate(-1, 10)), "a negative quiet period is rejected");
        Check(Throws<ArgumentOutOfRangeException>(() => new ScanGate(500, 100)), "a longest wait below the quiet period is rejected");

        Console.WriteLine(assertions + " scan gate checks passed.");
        return 0;
    }
}
