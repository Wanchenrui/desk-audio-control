using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal sealed partial class AudioPanel
{
    internal sealed class ReplayAudio : PanelAudio
    {
        public volatile bool Connected, ActiveCall, Bluetooth, HfpOnly;
        public string Foreground;
        public readonly Dictionary<string,int> Levels=new Dictionary<string,int>{{"old-music",55},{"usb",88},{"desk",50}};
        public int LevelAtRoute;
        public float Signal;
        public bool RejectRoute;
        public Action AfterLevelWrite;
        public override int Level(string id) { return Levels[id]; }
        public override void SetLevel(string id,int value)
        {
            Levels[id]=value;
            var changed=AfterLevelWrite; AfterLevelWrite=null;
            if (changed!=null) changed();
        }
        public override GainPoint[] GainPoints(string id) { return new[]{new GainPoint(0,-100),new GainPoint(Level(id),Level(id)-100),new GainPoint(100,0)}; }
        public override bool Muted(string id) { return false; }
        public override float Peak(string id) { return Signal; }
        public override string TuningGuard() { return ActiveCall ? "会议保护" : null; }
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public int Scans, Writes;
        public long AppliedAt;
        public string[] Roles = { "old-console", "old-music", "old-call" };
        public string[] MicRoles = { null,null,null };
        public override List<SmartGroup> ScanChanged(string[] changed) { return Scan(); }
        public override List<SmartGroup> Scan()
        {
            Interlocked.Increment(ref Scans);
            var result = new List<SmartGroup> { Group("old-music","音箱") };
            if (Bluetooth)
            {
                result[0].Inputs.Add(new AudioItem { Id="desk",Name="桌面话筒", Flow=1 });
                result[0].Forms["desk"] = 4;
            }
            if (Connected) result.Add(Group("usb","USB 耳机"));
            if (Bluetooth && Connected)
            {
                var bt = result[1];
                bt.Kind="Bluetooth"; bt.Outputs[0].Name="Stereo"; bt.Forms["usb"]=3; bt.Outputs[0].Channels=2;
                bt.Outputs.Add(new AudioItem { Id="hf",Name="Hands-Free AG Audio" });
                if (HfpOnly) bt.Outputs.RemoveAt(0);
                bt.Inputs.Add(new AudioItem { Id="hfin",Name="Headset",Flow=1 });
            }
            return result;
        }
        static SmartGroup Group(string id,string name)
        {
            var g = new SmartGroup { Key = id, Name = name, Kind = "USB", Details = "1 路播放" };
            g.Outputs.Add(new AudioItem { Id = id, Name = name, Flow = 0, State = 1, JackConnected=true });
            return g;
        }
        public override string Default(int flow,int role) { return flow == 0 ? Roles[role] : null; }
        public override string[] Outputs() { return (string[])Roles.Clone(); }
        public override string[] Inputs() { return (string[])MicRoles.Clone(); }
        public override void RouteInputs(string[] targets,string[] expected)
        {
            RouteTransaction.Apply(targets,i => MicRoles[i],(i,v) => MicRoles[i]=v,expected);
        }
        public override SceneSignals Context(List<SmartGroup> groups,string[] roles) { return new SceneSignals { Hour = 12, Communicating = ActiveCall, Foreground = Foreground }; }
        public override SwitchGuard RouteGuard(string[] roles) { return SwitchGuard.Evaluate(new SceneSignals { Communicating=ActiveCall }); }
        public override void RouteOutputs(string[] targets,string[] expected)
        {
            if (RejectRoute) throw new InvalidOperationException("simulated route failure");
            int level; if (Levels.TryGetValue(targets[1],out level)) LevelAtRoute=level;
            RouteTransaction.Apply(targets,i => Roles[i],(i,v) => Roles[i]=v,expected);
            Writes++; AppliedAt = Clock.ElapsedMilliseconds;
        }
    }

    internal void SmokeReplay(ReplayAudio device)
    {
        WaitFor(() => hasSnapshot);
        var elapsed = new List<long>();
        preferNew = true; autonomy = 2; tuneEnabled=true;
        for (int i = 0; i < 7; i++)
        {
            // Advance past the quiet period between independent replay runs.
            journal.Clear(); quietUntil = 0;
            int oldScans = device.Scans;
            device.Connected = true;
            long started = device.Clock.ElapsedMilliseconds;
            bus.Publish(ScanReason.Added); bus.Publish(ScanReason.State);
            Open(); // A concurrent view refresh used to consume the new output.
            WaitFor(() => device.Roles[1] == "usb");
            elapsed.Add(device.AppliedAt - started);
            if (device.Scans != oldScans + 1) throw new Exception("burst performed extra scans");
            var record = journal.LatestUndo();
            if (record == null || !record.LatencyMs.HasValue || Math.Abs(record.LatencyMs.Value - elapsed.Last()) > 100 || record.Evidence.SkipReason() != null) throw new Exception("decision replay mismatch");
            Message key = Message.Create(Handle,0x0312,new IntPtr(3),IntPtr.Zero);
            WndProc(ref key);
            if (!device.Roles.SequenceEqual(new[] { "old-console","old-music","old-call" })) throw new Exception("F9 undo failed");
            device.Connected = false;
            bus.Publish(ScanReason.Removed);
            WaitFor(() => groups.Count == 1);
        }
        device.ActiveCall = true; device.Connected = true; quietUntil = 0;
        bus.Publish(ScanReason.Added);
        WaitFor(() => groups.Count == 2);
        if (device.Writes != 14 || journal.Records().Last().Result != "skipped") throw new Exception("call protection failed");
        elapsed.Sort();
        Console.WriteLine("7/7 insertion bursts merged; 7/7 role restores; call guard passed; simulated endpoint -> verified route median {0} ms (range {1}-{2})",
            elapsed[3],elapsed[0],elapsed[6]);
        tuneEnabled=false;
        SmokeAutonomy(device);
    }

    void SmokeAutonomy(ReplayAudio device)
    {
        device.ActiveCall = false;
        Action<int> insert = level => {
            device.Connected = false; bus.Publish(ScanReason.Removed);
            WaitFor(() => groups.Count == 1);
            journal.Clear(); quietUntil = 0; autonomy = level;
            device.Connected = true; bus.Publish(ScanReason.Added);
            WaitFor(() => groups.Count == 2);
        };
        int writes = device.Writes;
        insert(0);
        if (device.Writes != writes || pendingRoute != null || journal.Records().Last().Result != "hint") throw new Exception("L0 wrote or offered");
        insert(1);
        if (pendingRoute == null || !offerButton.Enabled || device.Writes != writes) throw new Exception("L1 action missing");
        device.ActiveCall = true;
        try { AcceptOffer(); } catch (InvalidOperationException) { }
        if (device.Writes != writes || journal.Records().Last().Result != "skipped") throw new Exception("stale L1 bypassed call protection");
        device.ActiveCall = false; insert(1);
        AcceptOffer();
        if (device.Roles[1] != "usb" || journal.LatestUndo().InputBefore != null) throw new Exception("L1 acceptance");
        UndoRoute("test");
        var previousOverlay = overlay; insert(3);
        if (device.Roles[1] != "usb" || previousOverlay != overlay)
            throw new Exception("L3 notification or missing write");
        var active = journal.LatestUndo();
        device.Roles = new[] { "outside","old-music","old-call" };
        bus.Publish(ScanReason.Default);
        WaitFor(() => defaultOut == "old-music");
        if (journal.LatestUndo() != null || !journal.Records().Any(r => r.Result == "corrected" && r.Related == active.Id))
            throw new Exception("external role correction missing");
        Console.WriteLine("L0 hint, L1 fresh acceptance and stale call veto, L3 silent routing and Windows correction passed.");
        device.Bluetooth = true; device.MicRoles = new[] { "desk","hfin","hfin" }; insert(2);
        var paired = journal.LatestUndo();
        if (paired == null || paired.Target != "usb" || device.MicRoles.Any(p => p != "desk")) throw new Exception("Bluetooth paired route");
        UndoRoute("test");
        if (!device.MicRoles.SequenceEqual(new[] { "desk","hfin","hfin" }) || device.Roles[1] != "old-music")
            throw new Exception("Bluetooth paired undo");
        Console.WriteLine("Bluetooth stereo + independent microphone and mixed-role undo passed.");
        journal.Clear(); quietUntil = 0; device.Foreground="游戏";
        bus.Publish(ScanReason.Poll);
        WaitFor(() => !gate.Running && !gate.Pending && device.Clock.ElapsedMilliseconds > 16500);
        long observed = clock.ElapsedMilliseconds;
        bus.Publish(ScanReason.Poll);
        WaitFor(() => clock.ElapsedMilliseconds - observed >= 5200);
        bus.Publish(ScanReason.Poll);
        WaitFor(() => device.Roles[1] == "usb");
        if (scenes.Current!="游戏" || journal.LatestUndo().Evidence.Ranked.Any(p => p.Recent))
            throw new Exception("stable scene should switch already-connected devices");
        Console.WriteLine("Stable scene transition reranked connected endpoints without a plug event.");
        UndoRoute("test");
        device.HfpOnly = true; insert(2);
        if (device.Roles[1] == "hf" || journal.LatestUndo() != null) throw new Exception("HFP-only output selected");
        device.HfpOnly = false; bus.Publish(ScanReason.State);
        WaitFor(() => device.Roles[1] == "usb");
        if (!journal.LatestUndo().Evidence.Winner().Recent) throw new Exception("late stereo arrival lost");
        Console.WriteLine("HFP-only connection waited for a later stereo endpoint on the same headset.");
        UndoRoute("test"); tuneEnabled=true; insert(2);
        if (device.LevelAtRoute>12 || ramp==null) throw new Exception("target must be attenuated before automatic route");
        WaitFor(()=>ramp==null);
        if (device.Level("usb")!=35) throw new Exception("cold headphone ceiling and fade");
        UndoRoute("test"); if (device.Level("usb")!=88) throw new Exception("route undo lost original gain");
        insert(2);
        device.Levels["usb"]=31; WaitFor(()=>ramp==null);
        UndoRoute("test"); if (device.Level("usb")!=31) throw new Exception("ramp overwrote manual edit");
        device.Levels["usb"]=88; insert(2); device.ActiveCall=true;
        int held=device.Level("usb"); WaitFor(()=>ramp==null);
        if (device.Level("usb")!=held) throw new Exception("call did not stop fade");
        device.ActiveCall=false; UndoRoute("test");
        insert(2); device.Signal=1; int start=device.Level("usb"); WaitFor(()=>ramp==null);
        if (device.Level("usb")>=start) throw new Exception("peak suppression");
        device.Signal=0; UndoRoute("test");
        device.RejectRoute=true; insert(2); device.RejectRoute=false;
        if (device.Level("usb")!=88 || device.Roles[1]=="usb") throw new Exception("pre-route volume rollback");
        SetDefault(groups.Single(g=>g.Key=="usb").Outputs.First(p=>p.Id=="usb"));
        WaitFor(()=>ramp==null);
        if (device.Level("usb")!=35 || device.LevelAtRoute>12) throw new Exception("manual route bypassed soft landing");
        rail.Index=TunePageIndex; RefreshTuningPage();
        outputBox.SelectedIndex=outputBox.Items.Cast<TuneEndpoint>().ToList().FindIndex(p=>p.Port.Id=="usb");
        RestoreTuned(TuneOut); if (device.Level("usb")!=88) throw new Exception("manual tuning undo");
        inputBox.SelectedIndex=inputBox.Items.Cast<TuneEndpoint>().ToList().FindIndex(p=>p.Port.Id=="desk");
        diagnosis=new MicReport { Low=true,SpeechDb=-35,NoiseDb=-62,PeakDb=-20 }; diagnosedLevel=50; diagnosedPort="desk";
        ApplyMicFix(); if (device.Level("desk")!=56) throw new Exception("doctor repair");
        RestoreTuned(TuneIn); if (device.Level("desk")!=50) throw new Exception("doctor repair undo");
        var source=groups.First(p=>p.Key=="old-music"); var dest=groups.First(p=>p.Key=="usb");
        for (int n=0;n<3;n++) { levels.Learn(source.Key,65,-35); levels.Learn(dest.Key,30,-70); }
        var plan=LevelPlanner.Plan(55,88,device.GainPoints("old-music"),device.GainPoints("usb"),
            levels.Samples(source.Key),levels.Samples(dest.Key),true,LevelPlanner.Night(DateTime.Now.Hour),DateTime.UtcNow);
        if (plan.Target!=20) throw new Exception("relative gain transfer between two calibrated devices");
        device.Roles=new[]{"old-console","old-music","old-call"};
        device.AfterLevelWrite=delegate { device.Roles[2]="external-call"; };
        int beforeWrites=device.Writes;
        insert(2);
        if (device.Writes!=beforeWrites || device.Roles[2]!="external-call" ||
            !journal.Records().Any(r=>r.ReasonCode=="route_changed"))
            throw new Exception("role changed between attenuation and commit");
        Console.WriteLine("P3 pre-route attenuation, manual/automatic fade, peak cut, call/volume interruption, failure rollback, route undo and microphone repair/restore passed.");
    }

    internal void SmokeHardware()
    {
        WaitFor(() => hasSnapshot);
        if (groups.Count == 0) throw new Exception("No Windows endpoints for live check");
        Console.WriteLine("Live audio groups={0}, output={1}, input={2}, callbacks={3}",groups.Count,
            groups.Sum(g => g.Outputs.Count),groups.Sum(g => g.Inputs.Count),events.Registered);
    }
    internal void SmokeViews(string folder)
    {
        Open(); WaitFor(() => hasSnapshot);
        for (int i = 0; i < 4; i++)
        {
            rail.Index = i; LayoutAll(); Update();
            if (i == UpdatePageIndex)
            {
                var available = new UpdateResult(UpdateState.Available,"发现新版本 v1.5.0。","v1.5.0") {
                    Package = new UpdatePackage(1,123,new String('a',64),new Version(1,5,0))
                };
                updates.ShowRelease(available);
                if (!updates.Controls.Find("InstallUpdate",true).Any(c => c.Visible && c.Enabled))
                    throw new Exception("In-app update action missing");
            }
            using (var bmp = new Bitmap(Width,Height)) { DrawToBitmap(bmp,new Rectangle(0,0,Width,Height)); bmp.Save(Path.Combine(folder,"page-"+i+".png")); }
        }
        rail.Index=TunePageIndex;
        for (int i=0;i<3;i++) {
            SelectTuneTab(i);
            if (i==2) modelBox.SelectedItem=presets.First(p=>p.Model=="Sennheiser HD 600");
            Update();
            using (var bmp=new Bitmap(Width,Height)) { DrawToBitmap(bmp,new Rectangle(0,0,Width,Height)); bmp.Save(Path.Combine(folder,"tuning-"+i+".png")); }
        }
        using (var picker = new ModelPicker(presets, presets.First(p => p.Model == "Sony WH-1000XM4")))
        {
            picker.Show(this); Update(); Application.DoEvents();
            using (var bmp = new Bitmap(picker.Width, picker.Height)) { picker.DrawToBitmap(bmp, new Rectangle(0, 0, picker.Width, picker.Height)); bmp.Save(Path.Combine(folder, "picker.png")); }
        }
        using (var capture = new System.Windows.Forms.Timer { Interval = 180 })
        {
            capture.Tick += delegate {
                var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(w => w.Text == "活动时间线");
                if (dialog == null) return;
                capture.Stop();
                using (var bmp = new Bitmap(dialog.Width,dialog.Height)) { dialog.DrawToBitmap(bmp,new Rectangle(0,0,dialog.Width,dialog.Height)); bmp.Save(Path.Combine(folder,"decisions.png")); }
                dialog.Close();
            };
            journal.Add(DecisionRecord.Create("Added","USB 耳机","唯一符合用途的新增输出","applied"));
            capture.Start(); ShowDecisions();
        }
        using (var osd = new RouteOverlay("USB 耳机",53,delegate {}))
        {
            osd.Display();
            using (var bmp = new Bitmap(osd.Width,osd.Height)) { osd.DrawToBitmap(bmp,osd.ClientRectangle); bmp.Save(Path.Combine(folder,"overlay.png")); }
            WaitFor(() => osd.IsDisposed);
        }
        var sample = new List<AudioItem> {
            new AudioItem { Id="usb",Name="USB 耳机",Flow=0 },
            new AudioItem { Id="desk",Name="HECATE G1200",Flow=0 }
        };
        using (var quickPanel = new QuickPanel(sample,p => p.Id=="desk",38,false,delegate {},delegate {},delegate {},delegate {}))
        {
            quickPanel.Display();
            if (quickPanel.IsDisposed) throw new Exception("Quick panel closed before it could be captured");
            using (var bmp = new Bitmap(quickPanel.Width,quickPanel.Height)) { quickPanel.DrawToBitmap(bmp,quickPanel.ClientRectangle); bmp.Save(Path.Combine(folder,"quick.png")); }
        }
    }
    internal static void WaitFor(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > 30000) throw new TimeoutException("Panel smoke condition");
            Application.DoEvents(); Thread.Sleep(3);
        }
    }
}

internal static class PanelSmokeTest
{
    [STAThread] public static int Main(string[] args)
    {
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string folder = Environment.GetEnvironmentVariable("DESKAUDIO_SMOKE_DIR");
            new SettingsFile(folder, () => new Dictionary<string,string> { { "AutomaticUpdates","0" }, { "PreferNewOutput","0" }, { "EnableTuning","1" } });
            if (args[0]=="bridge") { TuningStream.CheckWindowsBridge(); return 0; }
            bool replay = args[0] == "replay";
            Theme.Palette(args.Contains("dark"));
            if (args.Length > 2) typeof(Theme).GetField("scale",BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null,Single.Parse(args[2],System.Globalization.CultureInfo.InvariantCulture));
            var fake = replay ? new AudioPanel.ReplayAudio() : null;
            using (var open = new EventWaitHandle(false,EventResetMode.AutoReset))
            using (var form = new AudioPanel(true,open,fake ?? new PanelAudio()))
            {
                if (replay) form.SmokeReplay(fake);
                else
                {
                    form.SmokeHardware();
                    if (args[0] == "view") form.SmokeViews(folder);
                    else
                    {
                        var process = Process.GetCurrentProcess(); var start = process.TotalProcessorTime;
                        var clock = Stopwatch.StartNew();
                        using (var context = new ApplicationContext())
                        using (var stop = new System.Windows.Forms.Timer { Interval = 22000 })
                        {
                            stop.Tick += delegate { stop.Stop(); context.ExitThread(); };
                            stop.Start(); Application.Run(context);
                        }
                        process.Refresh();
                        Console.WriteLine("Tray idle (22s): CPU {0:F3}% of host, working set {1:F1} MiB",
                            (process.TotalProcessorTime-start).TotalMilliseconds*100/clock.Elapsed.TotalMilliseconds/Environment.ProcessorCount,
                            process.WorkingSet64/1048576.0);
                    }
                }
            }
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
