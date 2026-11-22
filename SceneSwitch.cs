using System;
using System.Collections.Generic;
using System.Linq;

// Only coarse categories, device observations and counts enter the decision history.
internal sealed class SceneSignals
{
    public string Foreground,ReadError;
    public bool Capturing, Camera, Communicating, SharedBusy, Away, Unknown, ControlForeground;
    public int Sessions, Hour;
    public readonly HashSet<string> Audible=new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public string Classify()
    {
        if (Communicating || Camera || Foreground == "会议") return "会议";
        if (Capturing || Foreground == "录制") return "录制";
        if (Away) return "离开";
        if (Foreground != null) return Foreground;
        if (Hour >= 22 || Hour < 6) return "夜间";
        return "日常";
    }
    public string Guard()
    {
        return SwitchGuard.Evaluate(this).Reason;
    }
    public string ObservedScene(string previous)
    {
        if (ControlForeground && previous != null && Foreground == null && !Capturing && !Camera &&
            !Communicating && !Away && previous != "会议" && previous != "录制" && previous != "离开")
            return previous;
        return Classify();
    }
}

internal sealed class SceneTracker
{
    public string Current { get; private set; }
    string pending;
    long pendingSince, entered;
    readonly int confirmMs,dwellMs;
    internal SceneTracker(int confirmMs=5000,int dwellMs=15000)
    {
        if (confirmMs<0 || dwellMs<0) throw new ArgumentOutOfRangeException();
        this.confirmMs=confirmMs; this.dwellMs=dwellMs;
    }
    public bool Observe(string scene,long now)
    {
        if (Current == null) { Current = scene; entered = now; return false; }
        if (scene == Current) { pending = null; return false; }
        if (pending != scene) { pending = scene; pendingSince = now; }
        if (now - pendingSince < confirmMs || now - entered < dwellMs) return false;
        Current = scene; entered = now; pending = null;
        return true;
    }
}

internal sealed class RouteCandidate
{
    public string Key { get; set; }
    public string Id { get; set; }
    public string Name { get; set; }
    public bool Recent { get; set; }
    public bool Headphones { get; set; }
    public bool Speaker { get; set; }
    public bool Stereo { get; set; }
    public bool RestoreStereo { get; set; }
    public int FeedbackOffset { get; set; }
    public int Kept { get; set; }
    public int Corrected { get; set; }
    public string Presence { get; set; }
    public int Preference { get; set; }
    public int Level { get; set; }

    public int SceneFit(string scene)
    {
        if (scene == "夜间" || scene == "专注" || scene == "游戏") return Headphones ? 22 : 0;
        if (scene == "音乐" || scene == "影音") return Speaker ? 22 : Stereo ? 12 : 0;
        return 0;
    }
    public int Score(SwitchEvidence e)
    {
        int cost = RouteTransaction.Equal(Id,e.DefaultOutput) ? 0 : 2 * Math.Min(10,e.Sessions);
        return Preference + FeedbackOffset + (Recent ? 36 : 0) + (Stereo ? 5 : 0) + (RestoreStereo ? 30 : 0) + SceneFit(e.Scene) - cost;
    }
}

// Derive trust from retained local feedback. A linked correction supersedes positive credit.
// Read once per decision, never during the idle UI timer.
internal sealed class RouteTrust
{
    public int Level = 2, Streak, Preference;

    public static RouteTrust From(IEnumerable<DecisionRecord> rows,string device,string scene,DateTime now)
    {
        now=now.ToUniversalTime();
        var history=rows.Where(r=>!r.Imported && r.DeviceKey==device && r.Scene==scene && r.Utc.ToUniversalTime()<=now)
            .OrderBy(r=>r.Utc.ToUniversalTime()).ToList();
        var corrections = history.Where(r => (r.Result == "undone" || r.Result == "corrected") && r.Related != null)
            .ToLookup(r => r.Related);
        var trust = new RouteTrust();
        double preference=0;
        DateTime previous=history.Count==0 ? now : history[0].Utc.ToUniversalTime();
        foreach (var r in history)
        {
            DateTime time=r.Utc.ToUniversalTime();
            preference=Decay(preference,previous,time);
            previous=time;
            if (r.Result == "undone" || r.Result == "corrected")
            {
                trust.Level = Math.Max(0,trust.Level - 1); trust.Streak = 0;
                preference=Math.Max(-24,preference-12);
            }
            else if ((r.Result == "applied" || r.Result == "manual") &&
                !corrections[r.Id].Any(c => c.Utc.ToUniversalTime()>=time && (c.Utc.ToUniversalTime()-time).TotalSeconds<30) &&
                now-time >= TimeSpan.FromSeconds(30))
            {
                preference=Math.Min(24,preference+(r.Result=="manual" ? 8 : 3));
                if (++trust.Streak >= 3) { trust.Level = Math.Min(3,trust.Level + 1); trust.Streak = 0; }
            }
        }
        trust.Preference=(int)Math.Round(Decay(preference,previous,now));
        return trust;
    }
    static double Decay(double score,DateTime from,DateTime until)
    {
        const double halfLifeDays=14;
        return score*Math.Pow(.5,(until-from).TotalDays/halfLifeDays);
    }
}

internal static class BluetoothPlan
{
    public static string[] InputTargets(SmartGroup playback,string[] before,string microphone)
    {
        if (playback.Kind != "Bluetooth" || !before.Any(id => playback.Inputs.Any(p => RouteTransaction.Equal(p.Id,id)))) return null;
        if (String.IsNullOrEmpty(microphone) || before.Any(String.IsNullOrEmpty))
            throw new InvalidOperationException("蓝牙话筒是默认输入，缺少可安全配对的独立话筒或可恢复角色");
        return before.Select(id => playback.Inputs.Any(p => RouteTransaction.Equal(p.Id,id)) ? microphone : id).ToArray();
    }
}
