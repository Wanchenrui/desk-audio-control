using System;

internal static class MicAdvisor
{
    // Aim inside -24..-16 dBFS voice RMS. A clipped peak has unknown excess,
    // so request headroom in one bounded pass and require a new measurement.
    public static double Adjustment(MicReport r)
    {
        if (r.Clipping) return -Math.Min(6,Math.Max(3,r.PeakDb+6));
        if (r.NoSpeech || r.QuietContaminated || r.Silent || r.Noisy || r.HumHz!=0 || r.EchoRisk ||
            r.Rumble || r.Hiss || r.SpeechDb-r.NoiseDb<8) return 0;
        if (r.SpeechDb < -24) return Math.Min(6,Math.Min(-20-r.SpeechDb,-3-r.PeakDb));
        if (r.SpeechDb > -16) return Math.Max(-6,-20-r.SpeechDb);
        return 0;
    }
    internal static int Target(MicReport report,int level,GainPoint[] points)
    {
        double adjustment=Adjustment(report);
        if (adjustment==0) return level;
        var curve=LevelPlanner.Curve(points);
        int target=curve==null ? level+(int)Math.Sign(adjustment)*3 :
            Landing.FromDb(LevelPlanner.DbAt(curve,level)+adjustment,curve);
        return Math.Max(0,Math.Min(100,Math.Max(level-10,Math.Min(level+8,target))));
    }
}
