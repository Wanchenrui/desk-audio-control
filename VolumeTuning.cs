using System;
using System.Linq;
using System.Globalization;
using System.Web.Script.Serialization;

internal sealed class GainPoint
{
    public readonly int Level;
    public readonly double Db;
    public GainPoint(int level,double db) { Level=level; Db=db; }
}

internal static class Landing
{
    internal static int Percentile(int[] values,double q)
    {
        var ordered=values.OrderBy(v=>v).ToArray();
        return ordered[(int)Math.Ceiling(q * ordered.Length)-1];
    }

    internal static int FromDb(double db,GainPoint[] points)
    {
        var sorted=points.OrderBy(p=>p.Db).ToArray();
        if (db<=sorted[0].Db) return sorted[0].Level;
        for (int i=1;i<sorted.Length;i++) {
            var a=sorted[i-1]; var b=sorted[i];
            if (db<=b.Db) return (int)Math.Round(a.Level+(b.Level-a.Level)*(db-a.Db)/(b.Db-a.Db));
        }
        return sorted.Last().Level;
    }
    internal static int Ceiling(int[] samples) { return samples.Length==0 ? 35 : Math.Min(60,Percentile(samples,.85)); }

    // Comfort anchors are user-matched endpoint settings, not measured sound pressure.
    internal static int Plan(int source,int current,int[] from,int[] to,bool headphones)
    {
        if (current==0) return 0;
        int target=current;
        if (to.Length>0)
        {
            int offset=from.Length==0 ? 0 : source-Percentile(from,.5);
            target=Percentile(to,.5)+offset;
        }
        if (headphones)
        {
            int ceiling=Ceiling(to);
            target=Math.Min(ceiling,target);
        }
        return Math.Max(0,Math.Min(100,target));
    }
}

internal sealed class VolumeChange
{
    public string Endpoint { get; set; }
    public string Owner { get; set; }
    public int Original { get; set; }
    public int Last { get; set; }
    public int Next { get; set; }
}

// Save intent before changing hardware. A restarted instance accepts only the last observed
// or proposed level, so it can restore an interrupted ramp without replacing a later user edit.
internal sealed class VolumeTuning
{
    readonly SettingsFile store;
    readonly Func<string,int> read;
    readonly Action<string,int> write;
    readonly JavaScriptSerializer json=new JavaScriptSerializer();
    public VolumeTuning(SettingsFile store,Func<string,int> read,Action<string,int> write)
    {
        this.store=store; this.read=read; this.write=write;
    }
    public ComfortSample[] Samples(string group)
    {
        string data=store.Get("Comfort/"+group) ?? "";
        if (data.StartsWith("v2|",StringComparison.Ordinal)) {
            var rows=json.Deserialize<ComfortSample[]>(data.Substring(3)) ?? new ComfortSample[0];
            return rows.Where(s=>s!=null && s.Level>0 && s.Level<=100 &&
                (!s.Db.HasValue || s.Db>=-200 && s.Db<=50)).Take(16).ToArray();
        }
        var legacy=new System.Collections.Generic.List<ComfortSample>();
        foreach (var entry in data.Split(',')) {
            var parts=entry.Split('@'); int level; double db;
            if (!Int32.TryParse(parts[0],out level) || level<=0 || level>100) continue;
            var sample=new ComfortSample { Level=level };
            if (parts.Length==2 && Double.TryParse(parts[1],NumberStyles.Float,CultureInfo.InvariantCulture,out db)
                && db>=-200 && db<=50) sample.Db=db;
            legacy.Add(sample);
        }
        return legacy.Take(16).ToArray();
    }
    public int[] History(string group) { return Samples(group).Select(s=>s.Level).ToArray(); }
    public GainPoint[] Anchors(string group)
    {
        return Samples(group).Where(s=>s.Db.HasValue).Select(s=>new GainPoint(s.Level,s.Db.Value)).ToArray();
    }
    public void Learn(string group,int value,double db=Double.NaN)
    {
        if (value<=0 || value>100) return;
        var now=DateTime.UtcNow;
        var sample=new ComfortSample { Level=value,Utc=now,Night=LevelPlanner.Night(now.ToLocalTime().Hour) };
        if (db>=-200 && db<=50) sample.Db=db;
        store.WithFreshValues(delegate {
            var rows=Samples(group).ToList();
            rows.Add(sample);
            store.Put("Comfort/"+group,"v2|"+json.Serialize(rows.Skip(Math.Max(0,rows.Count-16)).ToArray()));
            return true;
        });
    }
    public void Forget(string group) { store.Put("Comfort/"+group,null); }

    public VolumeChange Saved(string id)
    {
        string data=store.Get("TuneUndo/"+id);
        if (data==null) return null;
        var tx=json.Deserialize<VolumeChange>(data);
        if (tx==null || tx.Endpoint!=id || String.IsNullOrEmpty(tx.Owner) ||
            new[]{tx.Original,tx.Last,tx.Next}.Any(v=>v<0 || v>100)) throw new InvalidOperationException("音量还原记录无效");
        return tx;
    }
    void Save(VolumeChange tx) { store.Put("TuneUndo/"+tx.Endpoint,json.Serialize(tx)); }
    public VolumeChange Begin(string id,string owner,int start)
    {
        return store.WithFreshValues(delegate {
            int original=read(id);
            var tx=new VolumeChange { Endpoint=id,Owner=owner,Original=original,Last=original,Next=original };
            Save(tx); WriteStep(tx,start); return tx;
        });
    }
    public void Step(VolumeChange tx,int next)
    {
        store.WithFreshValues(delegate { WriteStep(tx,next); return true; });
    }
    void WriteStep(VolumeChange tx,int next)
    {
        if (next<0 || next>100) throw new ArgumentOutOfRangeException("next");
        if (read(tx.Endpoint)!=tx.Next || !Owns(tx.Endpoint,tx.Owner))
            throw new InvalidOperationException("音量已变化，已保留当前设置");
        if (next==tx.Next) return;
        tx.Last=tx.Next; tx.Next=next;
        Save(tx);
        write(tx.Endpoint,next);
        int actual=read(tx.Endpoint);
        if (Math.Abs(actual-next)>1) throw new InvalidOperationException("设备未采用预期的音量");
        tx.Next=actual; tx.Last=actual; Save(tx);
    }
    public bool Owns(string endpoint,string owner)
    {
        var saved=Saved(endpoint);
        return saved!=null && saved.Owner==owner;
    }
    public bool Restore(string id,string owner)
    {
        return store.WithFreshValues(()=>RestoreCurrent(id,owner));
    }
    bool RestoreCurrent(string id,string owner)
    {
        var tx=Saved(id);
        if (tx==null || tx.Owner!=owner) return false;
        int current=read(id);
        if (current!=tx.Last && current!=tx.Next && current!=tx.Original)
            throw new InvalidOperationException("音量已被手动调整，已保留当前设置");
        if (current!=tx.Original) {
            // Persist the restore itself as a pending write.
            tx.Last=current; tx.Next=tx.Original; Save(tx);
            write(id,tx.Original);
            if (Math.Abs(read(id)-tx.Original)>1) throw new InvalidOperationException("设备未恢复原音量");
        }
        store.Put("TuneUndo/"+id,null);
        return true;
    }
}
