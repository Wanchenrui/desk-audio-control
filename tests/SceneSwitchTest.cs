using System;
using System.Collections.Generic;
using System.Linq;

internal static class SceneSwitchTest
{
    static int checks;
    static void Check(bool condition,string title)
    {
        if (!condition) throw new Exception(title);
        checks++;
    }
    static DecisionRecord Action(string result, string device, string scene, DateTime time)
    {
        var row = DecisionRecord.Create("test",device,scene,result);
        row.DeviceKey = device; row.Scene = scene; row.Utc = time;
        return row;
    }
    sealed class PairAudio : PanelAudio
    {
        public string[] Out = { "s","s","s" }, In = { "desk","hf","hf" };
        public bool FailOutput;
        public override string[] Outputs() { return (string[])Out.Clone(); }
        public override string[] Inputs() { return (string[])In.Clone(); }
        public override void RouteInputs(string[] targets,string[] expected)
        {
            RouteTransaction.Apply(targets,i => In[i],(i,v) => In[i]=v,expected);
        }
        public override void RouteOutputs(string[] targets,string[] expected)
        {
            if (FailOutput) throw new InvalidOperationException("endpoint removed");
            RouteTransaction.Apply(targets,i => Out[i],(i,v) => Out[i]=v,expected);
        }
    }
    public static void Main()
    {
        var signals = new SceneSignals { Hour = 12 };
        Check(signals.Classify() == "日常","daytime baseline");
        foreach (var scene in new[] { "会议","音乐","影音","游戏","专注","录制" }) {
            signals.Foreground = scene;
            Check(signals.Classify() == scene,"foreground category " + scene);
        }
        signals.Foreground = null; signals.Capturing = true;
        Check(signals.Classify() == "录制" && signals.Guard() != null,"background recording");
        signals.Communicating = true;
        Check(signals.Classify() == "会议","call priority");
        signals = new SceneSignals { Hour = 23 };
        Check(signals.Classify() == "夜间","night");
        signals.Away = true;
        Check(signals.Classify() == "离开" && signals.Guard() != null,"idle or locked");
        signals = new SceneSignals { Unknown = true };
        Check(signals.Guard() != null,"unknown activity fails closed");
        signals = new SceneSignals { Camera = true };
        Check(signals.Classify()=="会议" && signals.Guard()!=null,"camera use protects meetings");
        signals = new SceneSignals { Hour=12, SharedBusy=true };
        Check(signals.Classify()=="日常" && signals.Guard()!=null,"unclassified shared stream stays protected");
        var shared = SwitchGuard.Evaluate(signals);
        Check(shared.MaxLevel == 1 && shared.Code == "shared_playback","unclassified playback permits confirmation");
        signals.Capturing = true;
        Check(SwitchGuard.Evaluate(signals).Blocked,"simultaneous capture still blocks a confirmed offer");
        signals = new SceneSignals { Hour=12, ControlForeground=true };
        Check(signals.ObservedScene("专注")=="专注","opening DeskAudio to accept an offer preserves foreground context");
        signals.Capturing=true;
        Check(signals.ObservedScene("专注")=="录制" && signals.Guard()!=null,"recording takes precedence over panel interaction");

        var scenes = new SceneTracker();
        Check(!scenes.Observe("日常",0),"first snapshot only");
        Check(!scenes.Observe("音乐",1000) && !scenes.Observe("日常",9000),"transient scene discarded");
        Check(!scenes.Observe("音乐",10000) && scenes.Observe("音乐",20000),"stable change");
        Check(!scenes.Observe("游戏",21000) && !scenes.Observe("游戏",27000),"minimum residence");
        Check(scenes.Observe("游戏",36000),"stable after dwell");

        var current = new RouteCandidate { Id="old", Name="音箱" };
        var usb = new RouteCandidate { Id="usb", Name="USB", Recent=true };
        var input = new SwitchEvidence { Scene="日常", Ranked=new[] { current,usb }, DefaultOutput="old", CanRestore=true, Sensitivity=50 };
        Check(input.Winner().Id=="usb" && input.SkipReason()==null,"rank arrival against incumbent");
        input.Ranked = new[] { usb,current,new RouteCandidate { Id="twin", Recent=true } };
        Check(input.SkipReason()!=null,"ambiguous challengers abstain");
        input.Ranked = new[] { current,usb }; usb.Recent=false;
        Check(input.SkipReason()!=null,"stable incumbent");
        usb.Headphones=true; input.Scene="夜间"; input.Sensitivity=100;
        Check(input.SkipReason()==null,"night fits headphones");
        input.Sessions=8; input.Sensitivity=0;
        Check(input.SkipReason()!=null,"session cost and conservative margin");
        usb.Recent=true; input.Sessions=0; input.Sensitivity=100;
        input.Guard="会议保护"; Check(input.SkipReason()=="会议保护","hard guard dominates score");
        input.Guard=null; input.Quiet=true;
        Check(input.SkipReason()!=null,"manual quiet");
        input.Quiet=false; input.CanRestore=false;
        Check(input.SkipReason()!=null,"missing previous role");
        var now = DateTime.UtcNow;
        var observations=Enumerable.Range(0,8).Select(i=>Action("applied","usb","音乐",now.AddMinutes(-i-1))).ToList();
        Check(RouteFeedback.From(observations,"usb","音乐",now).Offset>0,"settled actions update posterior");
        observations[0].Imported=true;
        Check(RouteFeedback.From(observations,"usb","音乐",now).Offset==0,"insufficient local outcomes retain prior");
        var records = Enumerable.Range(1,3).Select(i => Action("applied","a","音乐",now.AddMinutes(-5+i))).ToList();
        Check(RouteTrust.From(records,"a","音乐",now).Level==3,"three settled actions earn silent level");
        Check(RouteTrust.From(records,"b","音乐",now).Level==2 && RouteTrust.From(records,"a","游戏",now).Level==2,"trust isolation");
        var undo = Action("undone","a","音乐",now); undo.Related=records.Last().Id; records.Add(undo);
        Check(RouteTrust.From(records,"a","音乐",now).Level==2,"undo after promotion lowers trust one level");
        undo.Imported=true;
        Check(RouteTrust.From(records,"a","音乐",now).Level==3,"imports never train");
        records.Clear(); records.Add(Action("applied","a","音乐",now));
        Check(RouteTrust.From(records,"a","音乐",now).Streak==0,"unsettled action gives no credit");
        var immediate = Action("undone","a","音乐",now.AddSeconds(3)); immediate.Related=records[0].Id;
        records.Add(immediate);
        Check(RouteTrust.From(records,"a","音乐",now.AddMinutes(1)).Level==1,"immediate undo demotes without positive credit");

        var bt = new SmartGroup { Kind="Bluetooth",Key="bt" };
        bt.Inputs.Add(new AudioItem { Id="hf-in" });
        bt.Outputs.Add(new AudioItem { Id="hf", Name="Hands-Free AG Audio" });
        Check(SmartPolicy.SelectStereo(bt,null,"hf")==null,"HFP alone blocked");
        bt.Outputs.Add(new AudioItem { Id="st", Name="Stereo" });
        Check(SmartPolicy.SelectStereo(bt,"hf","hf").Id=="st","automatic profile never chooses HFP over stereo");
        bt.Outputs[0].Name="Bluetooth"; bt.Outputs[0].Channels=1; bt.Outputs[1].Channels=2;
        Check(SmartPolicy.SelectStereo(bt,"hf","hf").Id=="st","localized mono profile rejected from observed format");
        var before = new[] { "desk","hf-in","hf-in" };
        Check(BluetoothPlan.InputTargets(bt,before,"desk").SequenceEqual(new[] { "desk","desk","desk" }),"move only headset input roles");
        Check(BluetoothPlan.InputTargets(bt,new[] { "desk","desk","desk" },null)==null,"independent input preserved");
        bool refused=false;
        try { BluetoothPlan.InputTargets(bt,before,null); } catch (InvalidOperationException) { refused=true; }
        Check(refused,"no unique independent microphone preserves both directions");
        var pair = new PairAudio();
        var outputs = new[] { "st","st","st" }; var inputs = new[] { "desk","desk","desk" };
        var previous = pair.Outputs(); var original = (string[])pair.In.Clone();
        pair.FailOutput=true; refused=false;
        try { pair.RoutePair(outputs,previous,inputs,original); } catch (InvalidOperationException) { refused=true; }
        Check(refused && pair.In.SequenceEqual(original) && pair.Out.SequenceEqual(previous),"second flow failure restores first");
        pair.FailOutput=false; pair.RoutePair(outputs,previous,inputs,original);
        pair.In[2]="outside"; refused=false;
        try { pair.RoutePair(previous,outputs,original,inputs); } catch (InvalidOperationException) { refused=true; }
        Check(refused && pair.Out.SequenceEqual(outputs) && pair.In[2]=="outside","conflicting capture role prevents paired undo");
        pair.In[2]="desk"; pair.RoutePair(previous,outputs,original,inputs);
        Check(pair.In.SequenceEqual(original) && pair.Out.SequenceEqual(previous),"paired undo restores both flows");
        var intent=Action("prepared","bt","音乐",now); intent.Before=previous; intent.Target="st";
        intent.InputBefore=original; intent.InputAfter=inputs;
        pair.In=(string[])inputs.Clone(); pair.Out[0]="st";
        pair.Restore(intent);
        Check(pair.In.SequenceEqual(original) && pair.Out.SequenceEqual(previous),"restart after partial pair can recover");
        pair.In[0]="outside"; refused=false;
        try { pair.Restore(intent); } catch (InvalidOperationException) { refused=true; }
        Check(refused && pair.In[0]=="outside","interrupted undo respects external changes");
        Console.WriteLine(checks + " scene, scoring, trust and Bluetooth checks passed.");
    }
}
