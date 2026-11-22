using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

internal static class FoundationTest
{
    static int count;
    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
        count++;
    }

    public static void Main()
    {
        var gate = new ScanGate(400, 2000);
        var bus = new AudioEventBus();
        int wakes = 0; long since;
        bus.Wake += delegate { wakes++; };
        bus.Publish(ScanReason.Added);
        bus.Publish(ScanReason.State);
        Check(wakes == 1 && bus.Drain(out since) == (ScanReason.Added | ScanReason.State), "coalesced notification retains both causes");
        bus.Publish(ScanReason.Default);
        Check(wakes == 2 && bus.Drain(out since) == ScanReason.Default, "rearm after draining");

        string[] dirty;
        bus.Publish(ScanReason.State,"usb"); bus.Publish(ScanReason.Property,"USB");
        bus.Drain(out since,out dirty);
        Check(dirty.Length==1,"dirty endpoints deduplicate across a burst");
        bus.Publish(ScanReason.Poll); bus.Drain(out since,out dirty);
        Check(dirty==null,"periodic scan fully reconciles missed notifications");
        var arrivals = new OutputArrivals();
        Check(arrivals.Observe(new[] { "speaker" }).Length == 0, "startup establishes baseline");
        gate.Request(1000);
        gate.Request(1030);
        Check(!gate.TryBegin(1429) && gate.TryBegin(1430), "event burst replay");
        Check(arrivals.Observe(new[] { "speaker","usb" }).SequenceEqual(new[] { "usb" }), "insertion survives opening the panel");
        gate.End();
        Check(arrivals.Observe(new[] { "usb","speaker" }).Length == 0, "a second view cannot repeat a decision");
        arrivals.Observe(new[] { "speaker" });
        Check(arrivals.Observe(new[] { "speaker","a","b" }).Length == 2, "simultaneous arrivals stay ambiguous");

        var observed = new SwitchEvidence { Eligible = new[] { "usb" }, DefaultOutput = "speakers", CanRestore = true };
        Check(observed.SkipReason() == null, "eligible insertion routes");
        observed.InUse = true;
        Check(observed.SkipReason() != null, "active recording or communication suppresses a switch");
        observed.InUse = false; observed.Quiet = true;
        Check(observed.SkipReason() != null, "recent manual action suppresses a switch");
        observed.Quiet = false; observed.CanRestore = false;
        Check(observed.SkipReason() != null, "missing role snapshot suppresses a switch");
        observed.CanRestore = true;
        string[] roles = { "console", "music", "call" };
        Action<int,string> set = (i,v) => roles[i] = v;
        bool writesObserved = false;
        RouteTransaction.Apply((string[])roles.Clone(), i => roles[i], set, null, () => writesObserved = true);
        Check(writesObserved,"measure completed role writes before readback");
        string[] original = RouteTransaction.Apply(new[] { "usb","usb","usb" }, i => roles[i], set, null);
        Check(original[2] == "call" && roles.All(v => v == "usb"), "record all previous roles");
        bool refused = false;
        try { RouteTransaction.Apply(original, i => roles[i], set, new[] { "usb","other","usb" }); }
        catch (InvalidOperationException) { refused = true; }
        Check(refused && roles.All(v => v == "usb"), "undo respects an intervening change");
        RouteTransaction.Apply(original, i => roles[i], set, new[] { "usb","usb","usb" });
        Check(roles.SequenceEqual(original), "undo restores mixed roles");
        bool failed = false;
        try { RouteTransaction.Apply(new[] { "bad","bad","bad" }, i => roles[i], (i,v) => { if(i == 1 && v == "bad") throw new IOException(); roles[i]=v; }, null); }
        catch (IOException) { failed = true; }
        Check(failed && roles.SequenceEqual(original), "partial writes roll back");

        string folder = Path.Combine(Path.GetTempPath(), "DeskAudio.Foundation." + Guid.NewGuid().ToString("N"));
        try
        {
            var cfg = new SettingsFile(folder, () => new Dictionary<string,string> { { "PreferNewOutput","1" } });
            Check(cfg.Get("PreferNewOutput") == "1", "first-run migration");
            string freshFolder = Path.Combine(folder, "fresh");
            var freshCfg = new SettingsFile(freshFolder, SettingsFile.FreshDefaults);
            Check(freshCfg.Get("PreferNewOutput") == "1" && freshCfg.Get("EnableTuning") == "1", "fresh install turns smart switching and tuning on");
            freshCfg.Put("PreferNewOutput","0");
            var freshAgain = new SettingsFile(freshFolder, SettingsFile.FreshDefaults);
            Check(freshAgain.Get("PreferNewOutput") == "0" && freshAgain.Get("EnableTuning") == "1", "restart keeps the user's own choice");
            Check(new SettingsFile(Path.Combine(folder, "legacy"), () => new Dictionary<string,string>()).Get("EnableTuning") == null, "an upgrade without those keys stays off");
            cfg.Put("PreferNewOutput","0");
            cfg.Put("example", "persist");
            var reopened = new SettingsFile(folder, () => { throw new Exception("unexpected second migration"); });
            Check(reopened.Get("example") == "persist" && reopened.Get("PreferNewOutput") == "0", "restart preserves configuration");
            cfg.Put("from-first","keep");
            reopened.Put("from-second","keep");
            string usb="AmbiguousUsbIdentities/VID_CAFE&PID_0001";
            cfg.Put(usb,"1"); cfg.Put(usb+":S:cloned","1"); cfg.Put("AmbiguousUsbIdentities/VID_CAFE&PID_0002","1");
            cfg.Put("Comfort/USB:VID_CAFE&PID_0001","keep");
            Check(reopened.ResetUsbIdentity("vid_cafe&pid_0001")==2 && reopened.ResetUsbIdentity("VID_CAFE&PID_0001")==0,
                "reset one model and its duplicate serials exactly once");
            Check(reopened.Get("AmbiguousUsbIdentities/VID_CAFE&PID_0002")=="1" && reopened.Get("Comfort/USB:VID_CAFE&PID_0001")=="keep",
                "retain other identities and calibration settings");
            Check(new SettingsFile(folder,null).Get("from-first") == "keep", "separate writers preserve other settings");
            reopened.Put("example", null);
            Check(reopened.Get("example") == null, "deleted value stays deleted");

            File.WriteAllText(Path.Combine(folder,"settings.json"),"{\"Version\":12}");
            var future = new SettingsFile(folder, () => { throw new Exception(); });
            Check(future.Warning != null && future.Get("PreferNewOutput") == null, "unknown schema uses manual mode");
            bool protectedFile = false;
            try { future.Put("a","b"); } catch (IOException) { protectedFile = true; }
            Check(protectedFile && File.ReadAllText(Path.Combine(folder,"settings.json")).Contains("12"), "future settings are preserved");
            var log = new DecisionJournal(folder);
            var decision = DecisionRecord.Create("Added", "USB", "one eligible output", "applied");
            decision.Evidence = observed;
            decision.Before = original;
            decision.Target = "usb";
            log.Add(decision);
            Check(new DecisionJournal(folder).LatestUndo().Id == decision.Id, "undo survives restart");
            var undo = DecisionRecord.Create("Hotkey", "USB", "restore roles", "undone");
            undo.Related = decision.Id;
            log.Add(undo);
            Check(log.LatestUndo() == null, "undo is consumed once");
            string backup = Path.Combine(folder,"export.json");
            log.Export(backup);
            log.Clear();
            log.Import(backup);
            Check(log.Records().First(r => r.Evidence != null).Evidence.SkipReason() == null, "exported event decisions replay offline");
            Check(log.Records().Count == 2 && log.LatestUndo() == null, "imported history is informational");
            log.Import(backup);
            Check(log.Records().Count == 2, "repeat import is idempotent");
            var expired = DecisionRecord.Create("Poll","","old","skipped");
            expired.Utc = DateTime.UtcNow.AddDays(-31);
            log.Add(expired);
            Check(log.Records().All(d => d.Id != expired.Id), "30 day retention");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder,true); }
        Console.WriteLine(count + " foundation checks passed.");
    }
}
