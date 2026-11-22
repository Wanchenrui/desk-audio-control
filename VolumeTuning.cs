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
    public int[] History(string group)
    {
        string value=store.Get("Comfort/"+group);
        if (String.IsNullOrEmpty(value)) return new int[0];
        return value.Split(',').Select(s=>{ int v; return Int32.TryParse(s.Split('@')[0],out v) ? v : -1; })
            .Where(v=>v>0 && v<=100).Take(16).ToArray();
    }
    public GainPoint[] Anchors(string group)
    {
        var list=new System.Collections.Generic.List<GainPoint>();
        foreach (var entry in (store.Get("Comfort/"+group)??"").Split(',')) {
            var parts=entry.Split('@'); int level; double db;
            if (parts.Length==2 && Int32.TryParse(parts[0],out level) && level>0 && level<=100 &&
                Double.TryParse(parts[1],NumberStyles.Float,CultureInfo.InvariantCulture,out db) && db>=-200 && db<=50)
                list.Add(new GainPoint(level,db));
        }
        return list.ToArray();
    }
    public void Learn(string group,int value,double db=Double.NaN)
    {
        if (value<=0 || value>100) return;
        string entry=value.ToString(CultureInfo.InvariantCulture);
        if (!Double.IsNaN(db) && !Double.IsInfinity(db)) entry+="@"+db.ToString("R",CultureInfo.InvariantCulture);
        store.WithFreshValues(delegate {
            var samples=(store.Get("Comfort/"+group)??"").Split(new[]{','},StringSplitOptions.RemoveEmptyEntries).Concat(new[]{entry}).ToList();
            if (samples.Count>16) samples.RemoveAt(0);
            store.Put("Comfort/"+group,String.Join(",",samples)); return true;
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
