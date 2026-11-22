using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

// A decision can be replayed with these observations alone, with no audio device access.
internal sealed class SwitchEvidence
{
    public string[] Eligible { get; set; }
    public string DefaultOutput { get; set; }
    public bool Quiet { get; set; }
    public bool CanRestore { get; set; }
    public bool InUse { get; set; }
    // Absent for v1.1/v1.2 records, which retain their original replay rules.
    public RouteCandidate[] Ranked { get; set; }
    public string Scene { get; set; }
    public string Guard { get; set; }
    public bool PairCurrentInput { get; set; }
    public int Sensitivity { get; set; }
    public int Sessions { get; set; }

    public string ConfirmationReason { get; set; }
    public int ModelVersion { get; set; }
    public string[] PresenceEvidence { get; set; }
    public string GuardCode { get; set; }
    public string SkipCode()
    {
        if (Ranked == null) return null;
        if (Guard != null) return GuardCode ?? "guard";
        if (Quiet) return "quiet_period";
        if (!CanRestore) return "missing_roles";
        var best = Winner();
        if (best == null) return "no_candidate";
        if (RouteTransaction.Equal(best.Id,DefaultOutput)) return PairCurrentInput ? null : "incumbent";
        if (Ranked.Count(p => p != null && p.Score(this) == best.Score(this)) > 1) return "tie";
        var current = Ranked.FirstOrDefault(p => p != null && RouteTransaction.Equal(p.Id,DefaultOutput));
        int lead = best.Score(this) - (current == null ? 0 : current.Score(this));
        int threshold=4+(100-Sensitivity)/4;
        if (ModelVersion==0) return lead<threshold ? "margin" : null;
        return RouteFeedback.Probability(lead)<RouteFeedback.Probability(threshold) ? "margin" : null;
    }

    public RouteCandidate Winner()
    {
        return Ranked.Where(p => p != null && !String.IsNullOrEmpty(p.Id))
            .OrderByDescending(p => p.Score(this)).ThenByDescending(p => RouteTransaction.Equal(p.Id,DefaultOutput))
            .ThenBy(p => p.Id,StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }
    public string Explain()
    {
        if (Ranked == null) return SkipReason() ?? "唯一符合用途的新增输出";
        var best = Winner();
        var scores = String.Join("、",Ranked.Where(p => p != null).Select(p => p.Name + " " + p.Score(this)));
        return Scene + " · " + (SkipReason() ?? ("选择 " + best.Name + " · L" + best.Level)) + (ConfirmationReason == null ? "" : " · " + ConfirmationReason) + "（" + scores + "）";
    }

    public string SkipReason()
    {
        if (Ranked != null)
        {
            switch (SkipCode())
            {
                case null: return null;
                case "quiet_period": return "处于手动操作或切换后的 30 秒静默期";
                case "missing_roles": return "缺少可供撤销的默认角色";
                case "no_candidate": return "没有符合用途和蓝牙配置要求的输出";
                case "incumbent": return "保留当前得分最高的输出";
                case "tie": return "候选同分，保留当前选择";
                case "margin": return "得分优势未达到切换门槛";
                default: return Guard;
            }
        }
        if (Eligible == null || Eligible.Length == 0) return "用途偏好排除了新增输出";
        if (Eligible.Length != 1) return "同时接入多个候选输出";
        if (String.Equals(Eligible[0],DefaultOutput,StringComparison.OrdinalIgnoreCase)) return "已是默认输出";
        if (Quiet) return "处于手动操作或切换后的 30 秒静默期";
        if (!CanRestore) return "缺少可供撤销的默认角色";
        if (InUse) return "录音或通信输出正在使用，或占用状态未知";
        return null;
    }
}

// Cumulative milliseconds from the first notification, on the monotonic clock.
internal sealed class RouteTiming
{
    public long ScanStartMs { get; set; }
    public long ScanEndMs { get; set; }
    public long DecisionMs { get; set; }
    public long? WriteMs { get; set; }
    public long? ReadbackMs { get; set; }
    public static long Elapsed(long origin,long tick)
    {
        return (long)((tick-origin)*1000.0/System.Diagnostics.Stopwatch.Frequency);
    }
}

internal sealed class DecisionRecord
{
    public string Id { get; set; }
    public DateTime Utc { get; set; }
    public string Source { get; set; }
    public string Candidates { get; set; }
    public string Reason { get; set; }
    public string Result { get; set; }
    public string Related { get; set; }
    public string[] Before { get; set; }
    public string ReasonCode { get; set; }
    public RouteTiming Timing { get; set; }
    public long? LatencyMs { get; set; }
    public SwitchEvidence Evidence { get; set; }
    public string Target { get; set; }
    public bool Imported { get; set; }
    public string DeviceKey { get; set; }
    public string Scene { get; set; }
    public string[] InputBefore { get; set; }
    public string[] InputAfter { get; set; }

    public static DecisionRecord Create(string source, string candidates, string reason, string result)
    {
        return new DecisionRecord { Id = Guid.NewGuid().ToString("N"), Utc = DateTime.UtcNow, Source = source,
            Candidates = candidates, Reason = reason, Result = result };
    }
}

internal sealed class DecisionJournal
{
    public sealed class Document
    {
        public int Version { get; set; }
        public List<DecisionRecord> Entries { get; set; }
    }

    readonly string path;
    public event Action Changed;
    public DecisionJournal(string folder) { path = Path.Combine(folder, "decisions.json"); }

    static List<DecisionRecord> Load(string file)
    {
        var doc = JsonFile.Read<Document>(file);
        if (doc == null || doc.Version != 1 || doc.Entries == null || doc.Entries.Any(r =>
            r == null || String.IsNullOrEmpty(r.Id) || r.Utc == default(DateTime) || r.Result == null))
            throw new InvalidDataException("不支持的决策日志格式。");
        return doc.Entries;
    }

    static List<DecisionRecord> Recent(IEnumerable<DecisionRecord> all)
    {
        var now = DateTime.UtcNow;
        return all.Where(r => r.Utc.ToUniversalTime() >= now.AddDays(-30) && r.Utc.ToUniversalTime() <= now.AddMinutes(5))
            .OrderByDescending(r => r.Utc.ToUniversalTime()).Take(1500).OrderBy(r => r.Utc.ToUniversalTime()).ToList();
    }

    public List<DecisionRecord> Records()
    {
        return JsonFile.Locked(path,delegate {
            var entries = LoadLocal();
            var current = Recent(entries);
            if (current.Count != entries.Count) Save(current);
            return current;
        });
    }
    List<DecisionRecord> LoadLocal() { return File.Exists(path) ? Load(path) : new List<DecisionRecord>(); }
    void Save(List<DecisionRecord> next)
    {
        var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        var document = new Document { Version = 1, Entries = next };
        for (;;)
        {
            string json = serializer.Serialize(document);
            int bytes = Encoding.UTF8.GetByteCount(json);
            if (bytes <= 8 * 1024 * 1024) { JsonFile.SaveJson(path,json); return; }
            if (next.Count <= 1) throw new InvalidDataException("单条决策记录过大");
            int excess = (int)Math.Ceiling(next.Count * (1.0 - 8.0 * 1024 * 1024 / bytes));
            next.RemoveRange(0,Math.Min(next.Count - 1,excess));
        }
    }
    void Change(Action<List<DecisionRecord>> update)
    {
        JsonFile.Locked(path,delegate {
            var entries = LoadLocal();
            update(entries);
            Save(Recent(entries));
            return true;
        });
        var changed = Changed;
        if (changed != null) changed();
    }
    public void Add(DecisionRecord record) { Change(all => all.Add(record)); }
    public void Clear() { Change(all=>all.Clear()); }
    public void Export(string file) { JsonFile.Save(file,new Document { Version = 1, Entries = Records() }); }
    public void Import(string file)
    {
        var incoming = Load(file);
        Change(all => {
            var ids = new HashSet<string>(all.Select(r => r.Id),StringComparer.OrdinalIgnoreCase);
            foreach (var r in incoming)
            {
                if (!ids.Add(r.Id)) continue;
                r.Imported = true;
                all.Add(r);
            }
        });
    }
    public DecisionRecord LatestUndo()
    {
        var closed = new HashSet<string>();
        foreach (var r in Enumerable.Reverse(Records()))
        {
            if (r.Imported) continue;
            if (r.Result == "manual") break;
            if (r.Related != null) closed.Add(r.Related);
            if ((r.Result == "applied" || r.Result == "prepared") && !closed.Contains(r.Id) &&
                r.Before != null && r.Before.Length == 3 && r.Before.All(v => !String.IsNullOrEmpty(v)) && !String.IsNullOrEmpty(r.Target)) return r;
        }
        return null;
    }
}
