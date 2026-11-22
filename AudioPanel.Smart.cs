using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

internal sealed partial class AudioPanel
{
    readonly SceneTracker scenes = new SceneTracker(
        ReadNumber("SceneConfirmMs",5000,0,60000),ReadNumber("SceneDwellMs",15000,0,300000));
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
            foreach (var p in g.Outputs.Concat(g.Inputs).Where(DevicePresence.Available)) result[p.Id] = g.Name + (p.Flow == 0 ? " · 播放" : " · 录音");
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
        selectedPort=id; selectedUntil=clock.ElapsedMilliseconds+30000;
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
    RouteCandidate[] Candidates(Snapshot snap,List<DecisionRecord> history,string[] added)
    {
        var candidates = new List<RouteCandidate>();
        var now = DateTime.UtcNow;
        string current=snap.Outputs[1];
        foreach (var g in snap.Groups.Where(g => !g.Virtual && SmartPolicy.PrimaryForFlow(g,snap.Groups,0,current)))
        {
            var bt = g.Kind == "Bluetooth";
            var pick = bt ? SmartPolicy.StereoOutput(g,current) : SmartPolicy.Pick(g,0,current);
            if (pick == null) continue;
            string mode = SmartPolicy.Mode(g);
            int form;
            if (!g.Forms.TryGetValue(pick.Id,out form)) form = -1;
            bool infer = mode == "auto" || mode == "show";
            var trust = RouteTrust.From(history,g.Key,scenes.Current,now);
            var feedback=RouteFeedback.From(history,g.Key,scenes.Current,now);
            candidates.Add(new RouteCandidate { Id = pick.Id, Key = g.Key, Name = g.Name,
                Headphones = mode == "headphones" || mode == "duplex" || (infer && (form == 3 || form == 5)),
                Speaker = mode == "speaker" || (infer && form == 1), Stereo = pick.Channels >= 2,
                RestoreStereo = bt && g.Outputs.Any(p => Same(p.Id,current) && (SmartPolicy.HandsFree(p) || p.Channels == 1)),
                Kept=feedback.Kept,Corrected=feedback.Corrected,FeedbackOffset=feedback.Offset,
                Preference = trust.Preference, Recent = added.Contains(g.Key,StringComparer.OrdinalIgnoreCase),
                Presence = pick.Presence==null ? null : pick.Presence.State+" · "+pick.Presence.Reason,
                Level = Math.Min(pick.Presence==null ? 1 : pick.Presence.MaxLevel,autonomy < 0 ? trust.Level : autonomy) });
        }
        return candidates.ToArray();
    }

    string IndependentMic(Snapshot snap)
    {
        var suitable = new List<AudioItem>();
        foreach (var g in snap.Groups.Where(g => !g.Virtual && g.Kind != "Bluetooth" && SmartPolicy.PrimaryForFlow(g,snap.Groups,1,snap.Inputs[1])))
        {
            var p = SmartPolicy.Pick(g,1,snap.Inputs[1]);
            if (p == null) continue;
            int form;
            if (SmartPolicy.Mode(g) == "microphone" || (g.Forms.TryGetValue(p.Id,out form) && form == 4))
                suitable.Add(p);
        }
        var existing = suitable.FirstOrDefault(p => snap.Inputs.Contains(p.Id,StringComparer.OrdinalIgnoreCase));
        return existing != null ? existing.Id : suitable.Count == 1 ? suitable[0].Id : null;
    }
    DecisionRecord PlanRoute(Snapshot snap,string source,string[] added,long since)
    {
        var guard = SwitchGuard.Evaluate(snap.Signals);
        var evidence = new SwitchEvidence { ModelVersion=1,
            PresenceEvidence=snap.Groups.Where(g=>!g.Virtual).SelectMany(g=>g.Outputs.Where(p=>p.Presence!=null)
                .Select(p=>p.Name+" · "+p.Presence.State+" · "+p.Presence.Reason)).ToArray(), DefaultOutput = snap.Outputs[1], Scene = scenes.Current, Sessions = snap.Signals.Sessions,
            Ranked = Candidates(snap,journal.Records(),added), Sensitivity = sensitivity, Guard = tuningJob!=null ? "正在进行声音调优" : guard.Blocked ? guard.Reason : null,
            GuardCode = tuningJob!=null ? "tuning" : guard.Code, ConfirmationReason = guard.MaxLevel==1 ? guard.Reason : null,
            Quiet = clock.ElapsedMilliseconds < quietUntil, CanRestore = snap.Outputs.All(v => !String.IsNullOrEmpty(v)) };
        if (guard.MaxLevel==1) foreach (var candidate in evidence.Ranked) candidate.Level=Math.Min(1,candidate.Level);
        var best = evidence.Winner();
        string[] inputs = null;
        if (best != null && evidence.Guard == null)
        {
            var g = snap.Groups.First(x => Same(x.Key,best.Key));
            if (g.Kind == "Bluetooth" && snap.Inputs.Any(id => ContainsId(g.Inputs,id)))
            {
                try
                {
                    inputs = BluetoothPlan.InputTargets(g,snap.Inputs,IndependentMic(snap));
                    evidence.PairCurrentInput = inputs != null;
                    if (inputs!=null) {
                        var mic=snap.Groups.SelectMany(x=>x.Inputs).FirstOrDefault(p=>inputs.Where((v,i)=>!Same(v,snap.Inputs[i])).Any(v=>Same(v,p.Id)));
                        if (mic!=null && mic.Presence!=null && mic.Presence.MaxLevel<2 && !snap.Inputs.Any(v=>Same(v,mic.Id))) {
                            best.Level=Math.Min(best.Level,mic.Presence.MaxLevel);
                            evidence.ConfirmationReason="独立话筒 · "+mic.Presence.Reason;
                        }
                    }
                }
                catch (InvalidOperationException error) { evidence.Guard = error.Message; evidence.GuardCode = "bluetooth_pair"; }
            }
        }
        string skip = evidence.SkipReason();
        string reason = evidence.Explain();
        if (inputs != null) reason += " · 配对独立话筒 " + NameFor(snap.Groups,inputs.Where((v,i) => !Same(snap.Inputs[i],v)).First(),1);
        string result = skip != null ? "skipped" : best.Level == 0 ? "hint" : best.Level == 1 ? "offer" : "prepared";
        var row = DecisionRecord.Create(source,String.Join("、",evidence.Ranked.Select(p => p.Name)),reason,result);
        row.Scene = scenes.Current; row.Evidence = evidence; row.ReasonCode = evidence.SkipCode() ?? guard.Code;
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
            ExecuteRoute(row,snap.Inputs,since);
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
        AssessPresence(snap,false);
        var connected = new HashSet<string>(Connections(groups).Keys,StringComparer.OrdinalIgnoreCase);
        string mismatch = null;
        if (!connected.SetEquals(Connections(snap.Groups).Keys) || !SameRoles(offer.Before,snap.Outputs) ||
            !SameRoles(expectedInputs,snap.Inputs) || snap.Signals.ObservedScene(scenes.Current) != offer.Scene)
            mismatch = "设备或场景已经变化";
        var recent = offer.Evidence.Ranked.Where(p => p.Recent).Select(p => p.Key).ToArray();
        var row=PlanRoute(snap,"执行建议",recent,since);
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
        ExecuteRoute(row,snap.Inputs,since);
    }
    bool CheckIntent(DecisionRecord intent,string[] inputRoles,bool confirmed,VolumeChange landing)
    {
        var roles=audio.Outputs();
        var guard=audio.RouteGuard(roles);
        string code=null,reason=null;
        if (guard.Blocked || (guard.MaxLevel==1 && !confirmed)) { code=guard.Code; reason=guard.Reason; }
        else if (!SameRoles(intent.Before,roles) || !SameRoles(inputRoles,audio.Inputs())) {
            code="route_changed"; reason="默认角色已经改变";
        } else if (clock.ElapsedMilliseconds<quietUntil) { code="quiet_period"; reason="处于静默期"; }
        if (reason==null) return true;
        AbortLanding(landing,intent.Before);
        intent.Evidence.Guard=reason; intent.Evidence.GuardCode=code;
        var skipped=DecisionRecord.Create(intent.Source,intent.Candidates,intent.Evidence.Explain(),"skipped");
        skipped.ReasonCode=code; skipped.Scene=intent.Scene; skipped.Related=intent.Id;
        skipped.Evidence=intent.Evidence; skipped.Timing=intent.Timing; journal.Add(skipped);
        return false;
    }
    void ExecuteRoute(DecisionRecord intent,string[] inputRoles,long since)
    {
        journal.Add(intent);
        var targets = Three(intent.Target);
        bool writing = false;
        VolumeChange volumeChange=null;
        try
        {
            bool confirmed = intent.Source == "执行建议";
            if (!CheckIntent(intent,inputRoles,confirmed,null)) return;
            volumeChange=PrepareLanding(intent.Target,intent.Before[1],intent.Id,confirmed);
            if (!CheckIntent(intent,inputRoles,confirmed,volumeChange)) return;
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
        if (volumeChange!=null) done.Reason+=" · "+rampPlan.Reason+"，目标 "+rampPlan.Target+"%";
        done.Timing = intent.Timing; done.LatencyMs = intent.Timing.ReadbackMs;
        done.Related = intent.Id; done.Target = intent.Target; done.DeviceKey = intent.DeviceKey; done.Scene = intent.Scene;
        done.Before = intent.Before; done.InputBefore = intent.InputBefore; done.InputAfter = intent.InputAfter; done.Evidence = intent.Evidence;
        BeginFade(volumeChange);
        journal.Add(done);
        var best = done.Evidence.Winner();
        if (best.Level != 3) ShowRoute(best.Name,done.Target,true);
    }
}
