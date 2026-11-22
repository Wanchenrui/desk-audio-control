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
        public volatile bool Connected, ActiveCall;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public int Scans, Writes;
        public long AppliedAt;
        public string[] Roles = { "old-console", "old-music", "old-call" };
        public override List<SmartGroup> Scan()
        {
            Interlocked.Increment(ref Scans);
            var result = new List<SmartGroup> { Group("old-music","音箱") };
            if (Connected) result.Add(Group("usb","USB 耳机"));
            return result;
        }
        static SmartGroup Group(string id,string name)
        {
            var g = new SmartGroup { Key = id, Name = name, Kind = "USB", Details = "1 路播放" };
            g.Outputs.Add(new AudioItem { Id = id, Name = name, Flow = 0, State = 1 });
            return g;
        }
        public override string Default(int flow,int role) { return flow == 0 ? Roles[role] : null; }
        public override string[] Outputs() { return (string[])Roles.Clone(); }
        public override bool InUse(string id) { return ActiveCall; }
        public override void RouteOutputs(string[] targets,string[] expected)
        {
            RouteTransaction.Apply(targets,i => Roles[i],(i,v) => Roles[i]=v,expected);
            Writes++; AppliedAt = Clock.ElapsedMilliseconds;
        }
    }

    internal void SmokeReplay(ReplayAudio device)
    {
        WaitFor(() => hasSnapshot);
        var elapsed = new List<long>();
        preferNew = true;
        for (int i = 0; i < 7; i++)
        {
            // Advance past the quiet period between independent replay runs.
            quietUntil = 0;
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
        for (int i = 0; i < 3; i++)
        {
            rail.Index = i; LayoutAll(); Update();
            if (i == 2)
            {
                var available = new UpdateResult(UpdateState.Available,"发现新版本 v1.3.0。","v1.3.0") {
                    Package = new UpdatePackage(1,123,new String('a',64),new Version(1,3,0))
                };
                updates.ShowRelease(available);
                if (!updates.Controls.Find("InstallUpdate",true).Any(c => c.Visible && c.Enabled))
                    throw new Exception("In-app update action missing");
            }
            using (var bmp = new Bitmap(Width,Height)) { DrawToBitmap(bmp,new Rectangle(0,0,Width,Height)); bmp.Save(Path.Combine(folder,"page-"+i+".png")); }
        }
        using (var capture = new System.Windows.Forms.Timer { Interval = 180 })
        {
            capture.Tick += delegate {
                var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(w => w.Text == "决策记录");
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
            new SettingsFile(folder, () => new Dictionary<string,string> { { "AutomaticUpdates","0" }, { "PreferNewOutput","0" } });
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
