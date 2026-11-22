using System;
using System.Linq;
using System.Windows.Forms;

internal sealed partial class AudioPanel
{
    readonly Timer landingTimer=new Timer { Interval=140 };
    VolumeTuning levels;
    VolumeChange ramp;
    int rampStart,rampTarget,rampTick;
    bool rampHeadphones;
    bool tuneEnabled=LocalSettings.Get("EnableTuning")=="1";
    string observedPort,learnKey,learnPort;
    int observedLevel,learnLevel;
    long learnAt;

    void InitLanding()
    {
        levels=new VolumeTuning(AppConfig.Store,audio.Level,audio.SetLevel);
        landingTimer.Tick+=delegate { AdvanceLanding(); };
    }
    void StopLanding()
    {
        landingTimer.Stop(); ramp=null; observedPort=null;
    }
    void QueueComfort(string key,string port,int level,long delay)
    {
        if (!tuneEnabled) return;
        learnKey=key; learnPort=port; learnLevel=level; learnAt=clock.ElapsedMilliseconds+delay;
    }
    void ObserveComfort()
    {
        if (!tuneEnabled || ramp!=null || defaultOut==null) return;
        var g=groups.FirstOrDefault(x=>!x.Virtual && ContainsId(x.Outputs,defaultOut));
        if (g==null) return;
        int n=audio.Level(defaultOut);
        if (observedPort==defaultOut && n!=observedLevel)
            QueueComfort(g.Key,defaultOut,n,3000);
        observedPort=defaultOut; observedLevel=n;
    }
    void LearnIfDue()
    {
        if (learnPort==null || clock.ElapsedMilliseconds<learnAt) return;
        string id=learnPort; learnPort=null;
        if (tuneEnabled && ramp==null && audio.Level(id)==learnLevel && !audio.Muted(id) && audio.TuningGuard()==null)
            RememberLevel(learnKey,id,learnLevel);
    }

    GainPoint[] DeviceGain(string id)
    {
        try { return audio.GainPoints(id); }
        catch (System.Runtime.InteropServices.COMException) { return new GainPoint[0]; }
        catch (NotSupportedException) { return new GainPoint[0]; }
    }
    void RememberLevel(string key,string id,int level)
    {
        var curve=DeviceGain(id);
        levels.Learn(key,level,curve.Length==3 && curve[1].Level==level ? curve[1].Db : Double.NaN);
    }
    int MatchedLevel(SmartGroup old,SmartGroup target,string source,string endpoint,int original,bool phones)
    {
        int[] history=levels.History(target.Key);
        int sourceLevel=audio.Level(source);
        if (sourceLevel==0 || audio.Muted(source)) return 0;
        var from=old==null ? new GainPoint[0] : levels.Anchors(old.Key);
        var to=levels.Anchors(target.Key);
        if (from.Length>0 && to.Length>0) {
            var sourceCurve=DeviceGain(source); var targetCurve=DeviceGain(endpoint);
            if (sourceCurve.Length==3 && targetCurve.Length==3) {
                var a=from.OrderBy(p=>p.Db).ElementAt((from.Length-1)/2);
                var b=to.OrderBy(p=>p.Db).ElementAt((to.Length-1)/2);
                double desired=b.Db+sourceCurve[1].Db-a.Db;
                int matched=Landing.FromDb(desired,targetCurve.Concat(to).ToArray());
                return phones ? Math.Min(Landing.Ceiling(history),matched) : matched;
            }
        }
        return Landing.Plan(sourceLevel,original,old==null ? new int[0] : levels.History(old.Key),history,phones);
    }

    // Return the durable volume intent to the caller before changing the default roles.
    VolumeChange PrepareLanding(string target,string source,string owner)
    {
        StopLanding(); learnPort=null;
        if (!tuneEnabled || Same(source,target) || target==null || source==null) return null;
        var g=groups.FirstOrDefault(x=>!x.Virtual && ContainsId(x.Outputs,target));
        if (g==null || audio.Muted(target)) return null;
        var old=groups.FirstOrDefault(x=>ContainsId(x.Outputs,source));
        int form; g.Forms.TryGetValue(target,out form);
        string mode=SmartPolicy.Mode(g);
        bool phones=mode=="headphones" || mode=="duplex" || form==3 || form==5 ||
            g.Outputs.Any(p=>Same(p.Id,target) && IsHeadphoneName(p.Name+" "+g.Name));
        int original=audio.Level(target);
        if (original==0) return null;
        int[] history=levels.History(g.Key);
        if (!phones && history.Length==0 && audio.Level(source)!=0 && !audio.Muted(source)) return null;
        rampTarget=MatchedLevel(old,g,source,target,original,phones);
        rampStart=phones ? Math.Min(original,Math.Min(12,rampTarget/3)) : Math.Min(original,rampTarget);
        rampHeadphones=phones;
        if (original==rampTarget && !phones) return null;
        try { return levels.Begin(target,owner,rampStart); }
        catch { RollbackLevel(target,owner); throw; }
    }
    static bool IsHeadphoneName(string text)
    {
        return new[]{"耳机","耳麦","headphone","headset","earbuds","airpods"}.Any(s=>text.IndexOf(s,StringComparison.OrdinalIgnoreCase)>=0);
    }
    void BeginFade(VolumeChange change)
    {
        if (change==null) return;
        ramp=change; rampTick=0;
        landingTimer.Start();
    }
    void AdvanceLanding()
    {
        var tx=ramp;
        if (tx==null) return;
        try {
            if (!tuneEnabled || !Same(audio.Default(0,1),tx.Endpoint) || audio.Muted(tx.Endpoint) ||
                audio.Level(tx.Endpoint)!=tx.Next || audio.TuningGuard()!=null)
            { StopLanding(); return; }
            rampTick++;
            if (rampHeadphones && audio.Peak(tx.Endpoint)>=.94f) {
                levels.Step(tx,Math.Max(0,tx.Next-Math.Max(4,tx.Next/4)));
                StopLanding(); Notify("检测到接近满幅的峰值，已降低耳机音量");
                Activity("柔着陆",NameFor(tx.Endpoint,0),"切换后检测到高峰值，已降低音量","tuned"); return;
            }
            if (rampTick<=6)
                levels.Step(tx,rampStart+(int)Math.Round((rampTarget-rampStart)*rampTick/6.0));
            if (rampTick>=(rampHeadphones ? 18 : 6)) StopLanding();
        } catch (Exception e) { StopLanding(); Fail("淡入已停止 · "+e.Message); }
    }
    void AbortLanding(VolumeChange tx,string[] before)
    {
        if (tx==null) return;
        try {
            if (!SameRoles(audio.Outputs(),before)) { Fail("输出角色已变化，可在声音调优中还原音量"); return; }
            RollbackLevel(tx.Endpoint,tx.Owner);
        } catch (Exception error) { Fail("音量还原 · "+error.Message); }
    }
    void RollbackLevel(string endpoint,string owner)
    {
        if (ramp!=null && ramp.Owner==owner) StopLanding();
        if (!levels.Owns(endpoint,owner)) return;
        try {
            if (audio.TuningGuard()!=null) throw new InvalidOperationException("设备正在使用，可稍后从声音调优还原");
            levels.Restore(endpoint,owner); observedPort=null;
        } catch (Exception error) { Fail("音量还原 · "+error.Message); }
    }
}
