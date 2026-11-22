using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

internal sealed partial class AudioPanel
{
    readonly SceneTracker scenes = new SceneTracker();
    string[] expectedOutputs, expectedInputs;
    DecisionRecord pendingRoute;
    long offerUntil;
    int sensitivity = ReadNumber("RouteSensitivity",50,0,100);
    int autonomy = ReadNumber("RouteAutonomy",-1,-1,3);

    static int ReadNumber(string key,int fallback,int min,int max)
    {
        int n;
        return Int32.TryParse(LocalSettings.Get(key),out n) && n >= min && n <= max ? n : fallback;
    }
    static bool SameRoles(string[] a,string[] b)
    {
        return a != null && b != null && a.SequenceEqual(b,StringComparer.OrdinalIgnoreCase);
    }
    static string[] Three(string id) { return new[] { id,id,id }; }

    void Activity(string source,string devices,string reason,string result)
    {
        var entry = DecisionRecord.Create(source,devices,reason,result);
        entry.Scene = scenes.Current;
        try { journal.Add(entry); } catch (Exception error) { Fail("活动时间线 · " + error.Message); }
    }

    static Dictionary<string,string> Connections(List<SmartGroup> devices)
    {
        var result = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in devices)
        {
            foreach (var p in g.Outputs.Concat(g.Inputs)) result[p.Id] = g.Name + (p.Flow == 0 ? " · 播放" : " · 录音");
            foreach (var id in g.SensedOutputs.Concat(g.SensedInputs)) result["jack:" + id] = g.Name + " · 插孔";
        }
        return result;
    }
    bool RecordConnections(List<SmartGroup> previous,List<SmartGroup> current,ScanReason reasons)
    {
        var old = Connections(previous);
        var next = Connections(current);
        var added = next.Where(p => !old.ContainsKey(p.Key)).Select(p => p.Value).Distinct().ToArray();
        var removed = old.Where(p => !next.ContainsKey(p.Key)).Select(p => p.Value).Distinct().ToArray();
        if (removed.Length > 0) Activity(reasons.ToString(),String.Join("、",removed),"端口或插头已断开","removed");
        if (added.Length > 0) Activity(reasons.ToString(),String.Join("、",added),"检测到端口或插头","connected");
        return added.Length + removed.Length != 0;
    }

    void RestoreQuietPeriod()
    {
        try
        {
            var last = journal.Records().LastOrDefault(r => !r.Imported &&
                (r.Result == "applied" || r.Result == "prepared" || r.Result == "undone" || r.Result == "manual" || r.Result == "input"));
            if (last == null) return;
            long remaining = 30000 - (long)(DateTime.UtcNow - last.Utc.ToUniversalTime()).TotalMilliseconds;
            quietUntil = Math.Max(quietUntil,clock.ElapsedMilliseconds + Math.Max(0,Math.Min(30000,remaining)));
        }
        catch (Exception error) { Fail(error.Message); }
    }

    void RecordChoice(string source,string id,string previous)
    {
        var g = groups.FirstOrDefault(x => ContainsId(x.Outputs,id));
        var last = journal.LatestUndo();
        if (last != null && last.DeviceKey != null && Same(last.Target,previous) && !Same(last.Target,id))
        {
            var correction = DecisionRecord.Create(source,last.Candidates,"已改选其他输出","corrected");
            correction.Scene = last.Scene; correction.DeviceKey = last.DeviceKey; correction.Related = last.Id;
            journal.Add(correction);
        }
        var row = DecisionRecord.Create(source,g == null ? "默认输出" : g.Name,"默认输出角色变化","manual");
        row.DeviceKey = g == null ? null : g.Key; row.Scene = scenes.Current;
        journal.Add(row);
    }
    RouteCandidate[] Candidates(List<DecisionRecord> history,string[] added)
    {
        var candidates = new List<RouteCandidate>();
        var now = DateTime.UtcNow;
        foreach (var g in groups.Where(g => !g.Virtual && Eligible(g,0)))
        {
            var bt = g.Kind == "Bluetooth";
            var pick = bt ? SmartPolicy.StereoOutput(g,defaultOut) : SmartPolicy.Pick(g,0,defaultOut);
            if (pick == null) continue;
            string mode = SmartPolicy.Mode(g);
            int form;
            if (!g.Forms.TryGetValue(pick.Id,out form)) form = -1;
            bool infer = mode == "auto" || mode == "show";
            var trust = RouteTrust.From(history,g.Key,scenes.Current,now);
            candidates.Add(new RouteCandidate { Id = pick.Id, Key = g.Key, Name = g.Name,
                Headphones = mode == "headphones" || mode == "duplex" || (infer && (form == 3 || form == 5)),
                Speaker = mode == "speaker" || (infer && form == 1), Stereo = pick.Channels >= 2,
                RestoreStereo = bt && g.Outputs.Any(p => Same(p.Id,defaultOut) && (SmartPolicy.HandsFree(p) || p.Channels == 1)),
                Preference = trust.Preference, Recent = added.Contains(g.Key,StringComparer.OrdinalIgnoreCase),
                Level = autonomy < 0 ? trust.Level : autonomy });
        }
        return candidates.ToArray();
    }

    string IndependentMic(string[] roles)
    {
        var suitable = new List<AudioItem>();
        foreach (var g in groups.Where(g => !g.Virtual && g.Kind != "Bluetooth" && Eligible(g,1)))
        {
            var p = SmartPolicy.Pick(g,1,defaultIn);
            if (p == null) continue;
            int form;
            if (SmartPolicy.Mode(g) == "microphone" || (g.Forms.TryGetValue(p.Id,out form) && form == 4))
                suitable.Add(p);
        }
        var existing = suitable.FirstOrDefault(p => roles.Contains(p.Id,StringComparer.OrdinalIgnoreCase));
        return existing != null ? existing.Id : suitable.Count == 1 ? suitable[0].Id : null;
    }
    DecisionRecord PlanRoute(Snapshot snap,string source,string[] added,long since)
    {
        var evidence = new SwitchEvidence { DefaultOutput = defaultOut, Scene = scenes.Current, Sessions = snap.Signals.Sessions,
            Ranked = Candidates(journal.Records(),added), Sensitivity = sensitivity, Guard = tuningJob==null ? snap.Signals.Guard() : "正在进行声音调优",
            Quiet = clock.ElapsedMilliseconds < quietUntil, CanRestore = snap.Outputs.All(v => !String.IsNullOrEmpty(v)) };
        var best = evidence.Winner();
        string[] inputs = null;
        if (best != null && evidence.Guard == null)
        {
            var g = groups.First(x => Same(x.Key,best.Key));
            if (g.Kind == "Bluetooth" && snap.Inputs.Any(id => ContainsId(g.Inputs,id)))
            {
                try
                {
                    inputs = BluetoothPlan.InputTargets(g,snap.Inputs,IndependentMic(snap.Inputs));
                    evidence.PairCurrentInput = inputs != null;
                }
                catch (InvalidOperationException error) { evidence.Guard = error.Message; }
            }
        }
        string skip = evidence.SkipReason();
        string reason = evidence.Explain();
        if (inputs != null) reason += " · 配对独立话筒 " + NameFor(inputs.Where((v,i) => !Same(snap.Inputs[i],v)).First(),1);
        string result = skip != null ? "skipped" : best.Level == 0 ? "hint" : best.Level == 1 ? "offer" : "prepared";
        var row = DecisionRecord.Create(source,String.Join("、",evidence.Ranked.Select(p => p.Name)),reason,result);
        row.Scene = scenes.Current; row.Evidence = evidence; row.ReasonCode = evidence.SkipCode();
        row.Timing = new RouteTiming { ScanStartMs = RouteTiming.Elapsed(since,snap.ScanStart),
            ScanEndMs = RouteTiming.Elapsed(since,snap.ScanEnd), DecisionMs = RouteTiming.Elapsed(since,Stopwatch.GetTimestamp()) };
        if (best != null) { row.Target = best.Id; row.DeviceKey = best.Key; }
        if (skip != null) return row;
        row.Before = snap.Outputs;
        row.InputBefore = inputs == null ? null : snap.Inputs;
        row.InputAfter = inputs;
        return row;
    }
    void ConsiderRoute(Snapshot snap,ScanReason reasons,long since,string[] added)
    {
        try
        {
            var row = PlanRoute(snap,reasons.ToString(),added,since);
            if (row.Result == "skipped") { journal.Add(row); return; }
            var best = row.Evidence.Winner();
            if (best.Level < 2)
            {
                journal.Add(row);
                if (best.Level == 1)
                {
                    pendingRoute = row; offerUntil = clock.ElapsedMilliseconds + 60000;
                    if (!RouteOverlay.FullScreen())
                    {
                        if (overlay != null) overlay.Dispose();
                        overlay = new RouteOverlay("建议 · " + best.Name,-1,() => Act(delegate {
                            if (pendingRoute != row) throw new InvalidOperationException("建议已过期");
                            AcceptOffer();
                        }),"执行");
                        overlay.Display(); return;
                    }
                }
                string text = "建议切换至 " + best.Name + (best.Level == 1 ? " · 可在设置或托盘执行" : "");
                Notify(text);
                if (Visible && rail.Index != 0) tray.ShowBalloonTip(3500,"桌面音频",text,System.Windows.Forms.ToolTipIcon.Info);
                return;
            }
            ExecuteRoute(row,since);
        }
        catch (Exception e) { Fail("智能切换 · " + e.Message); }
    }

    void AcceptOffer()
    {
        var offer = pendingRoute;
        pendingRoute = null;
        RefreshSmartSettings();
        if (!preferNew || offer == null || clock.ElapsedMilliseconds >= offerUntil || offer.Scene != scenes.Current)
            throw new InvalidOperationException("建议已过期");
        // Re-enumerate for a delayed click, including endpoint roles and preference edits.
        long since = Stopwatch.GetTimestamp();
        var snap = TakeSnapshot();
        var connected = new HashSet<string>(Connections(groups).Keys,StringComparer.OrdinalIgnoreCase);
        string mismatch = null;
        if (!connected.SetEquals(Connections(snap.Groups).Keys) || !SameRoles(offer.Before,snap.Outputs) ||
            !SameRoles(expectedInputs,snap.Inputs) || snap.Signals.ObservedScene(scenes.Current) != offer.Scene)
            mismatch = "设备或场景已经变化";
        var recent = offer.Evidence.Ranked.Where(p => p.Recent).Select(p => p.Key).ToArray();
        var row = PlanRoute(snap,"执行建议",recent,since);
        if (mismatch == null && (row.Result != "offer" || !Same(row.Target,offer.Target) ||
            !(row.InputAfter == null && offer.InputAfter == null || SameRoles(row.InputAfter,offer.InputAfter))))
            mismatch = row.Evidence.SkipReason() ?? "当前建议已变化";
        row.Related = offer.Id;
        if (mismatch != null)
        {
            row.Result = "skipped"; row.ReasonCode = row.Evidence.GuardCode = "stale_offer"; row.Evidence.Guard = mismatch; row.Reason = row.Evidence.Explain();
            journal.Add(row); throw new InvalidOperationException(mismatch);
        }
        row.Result = "prepared";
        ExecuteRoute(row,since);
    }
    void ExecuteRoute(DecisionRecord intent,long since)
    {
        journal.Add(intent);
        var targets = Three(intent.Target);
        bool writing = false;
        VolumeChange volumeChange=null;
        try
        {
            var roles = audio.Outputs();
            string guard = audio.Guard(roles);
            bool stale = !SameRoles(intent.Before,roles) || !SameRoles(expectedInputs,audio.Inputs());
            if (guard != null || stale || clock.ElapsedMilliseconds < quietUntil)
            {
                intent.Evidence.Guard = guard ?? "角色已改变或处于静默期";
                var skipped = DecisionRecord.Create(intent.Source,intent.Candidates,intent.Evidence.Explain(),"skipped");
                skipped.ReasonCode = intent.Evidence.GuardCode ?? "route_changed"; skipped.Timing = intent.Timing;
                skipped.Related = intent.Id; skipped.Scene = intent.Scene; skipped.Evidence = intent.Evidence;
                journal.Add(skipped); return;
            }
            volumeChange=PrepareLanding(intent.Target,roles[1],intent.Id);
            if (volumeChange!=null) {
                string protection=audio.TuningGuard();
                if (protection!=null) throw new InvalidOperationException(protection);
            }
            writing = true;
            audio.OutputWritesComplete = () => intent.Timing.WriteMs = RouteTiming.Elapsed(since,Stopwatch.GetTimestamp());
            try {
                audio.RoutePair(targets,intent.Before,intent.InputAfter,intent.InputBefore);
                intent.Timing.ReadbackMs = RouteTiming.Elapsed(since,Stopwatch.GetTimestamp());
            } finally { audio.OutputWritesComplete = null; }
        }
        catch (Exception e)
        {
            AbortLanding(volumeChange,intent.Before);
            var failure = DecisionRecord.Create(intent.Source,intent.Candidates,e.Message,"failed");
            bool restored = !writing;
            if (writing)
            {
                try { restored = SameRoles(intent.Before,audio.Outputs()) &&
                    (intent.InputBefore == null || SameRoles(intent.InputBefore,audio.Inputs())); }
                catch (Exception) { }
            }
            if (restored) failure.Related = intent.Id; // Keep interrupted intents available for guarded recovery.
            failure.Timing = intent.Timing; failure.Scene = intent.Scene; journal.Add(failure); throw;
        }
        actionRevision++; pendingRoute = null;
        defaultOut = intent.Target; expectedOutputs = targets;
        if (intent.InputAfter != null) { expectedInputs = intent.InputAfter; defaultIn = expectedInputs[1]; }
        quietUntil = clock.ElapsedMilliseconds + 30000;
        var done = DecisionRecord.Create(intent.Source,intent.Candidates,intent.Reason,"applied");
        done.Timing = intent.Timing; done.LatencyMs = intent.Timing.ReadbackMs;
        done.Related = intent.Id; done.Target = intent.Target; done.DeviceKey = intent.DeviceKey; done.Scene = intent.Scene;
        done.Before = intent.Before; done.InputBefore = intent.InputBefore; done.InputAfter = intent.InputAfter; done.Evidence = intent.Evidence;
        BeginFade(volumeChange);
        journal.Add(done);
        var best = done.Evidence.Winner();
        if (best.Level != 3) ShowRoute(best.Name,done.Target,true);
    }
}
