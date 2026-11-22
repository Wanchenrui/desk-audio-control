using System;
using System.Collections.Generic;
using System.Linq;

internal sealed class ComfortSample
{
    public int Level { get; set; }
    public double? Db { get; set; }
    public DateTime? Utc { get; set; }
    public bool? Night { get; set; }
}

internal sealed class LevelPlan
{
    public int Start,Target,Ceiling;
    public double? StartDb, TargetDb, CeilingDb;
    public int[] Steps;
    public string Reason;
}

internal static class LevelPlanner
{
    const int MinimumSamples=3;
    public static bool Night(int hour) { return hour>=22 || hour<6; }

    // Never sweep device volume to measure its curve. Combine read-only range/current
    // readings with earlier observed gain points; reject ambiguous or reversed curves.
    internal static GainPoint[] Curve(IEnumerable<GainPoint> points)
    {
        var result=new List<GainPoint>();
        foreach (var group in points.OrderBy(p=>p.Level).GroupBy(p=>p.Level)) {
            if (group.Key<0 || group.Key>100 || group.Any(p=>Double.IsNaN(p.Db) || Math.Abs(p.Db)>200)
                || group.Max(p=>p.Db)-group.Min(p=>p.Db)>1) return null;
            var point=new GainPoint(group.Key,group.Average(p=>p.Db));
            if (result.Count>0 && point.Db<=result.Last().Db) return null;
            result.Add(point);
        }
        return result.Count>=3 && result[0].Level==0 && result.Last().Level==100 ? result.ToArray() : null;
    }
    internal static double DbAt(GainPoint[] curve,int level)
    {
        if (level<=curve[0].Level) return curve[0].Db;
        for (int i=1;i<curve.Length;i++) {
            var a=curve[i-1]; var b=curve[i];
            if (level<=b.Level) return a.Db+(level-a.Level)*(b.Db-a.Db)/(b.Level-a.Level);
        }
        return curve.Last().Db;
    }
    static GainPoint[] Observed(GainPoint[] native,ComfortSample[] samples)
    {
        if (native.Length<3) return null;
        return Curve(native.Concat(samples.Where(s=>s.Db.HasValue).Select(s=>new GainPoint(s.Level,s.Db.Value))));
    }
    static double Weight(ComfortSample s,DateTime now)
    {
        return s.Utc.HasValue ? Math.Pow(.5,Math.Max(0,(now-s.Utc.Value.ToUniversalTime()).TotalDays)/30) : .5;
    }
    static double Quantile(ComfortSample[] samples,Func<ComfortSample,double> value,double q,DateTime now)
    {
        double threshold=samples.Sum(s=>Weight(s,now))*q, sum=0;
        foreach (var s in samples.OrderBy(value)) { sum+=Weight(s,now); if (sum>=threshold) return value(s); }
        return samples.Max(value);
    }
    static ComfortSample[] Filter(ComfortSample[] samples,bool night,DateTime now)
    {
        var local=samples.Where(s=>(!s.Night.HasValue || s.Night==night) &&
            (!s.Utc.HasValue || (now-s.Utc.Value.ToUniversalTime()).TotalDays<=90)).ToArray();
        if (local.Length<MinimumSamples) return local;
        double median=Quantile(local,s=>s.Level,.5,now);
        double mad=Quantile(local,s=>Math.Abs(s.Level-median),.5,now);
        return local.Where(s=>Math.Abs(s.Level-median)<=Math.Max(5,3*mad)).ToArray();
    }
    internal static LevelPlan Plan(int source,int current,GainPoint[] sourceCurve,GainPoint[] targetCurve,
        ComfortSample[] sourceHistory,ComfortSample[] targetHistory,bool phones,bool night,DateTime now,int userCeiling=60)
    {
        var from=Filter(sourceHistory,night,now); var to=Filter(targetHistory,night,now);
        var a=Observed(sourceCurve,sourceHistory); var b=Observed(targetCurve,targetHistory);
        bool cold=from.Length<MinimumSamples || to.Length<MinimumSamples;
        int cap=phones ? Math.Min(userCeiling,to.Length<MinimumSamples ? 35 : (int)Quantile(to,s=>s.Level,.85,now)) : 100;
        int target=current;
        string reason="样本不足，保留或降低当前音量";
        if (!cold && a!=null && b!=null) {
            double db=Quantile(to,s=>DbAt(b,s.Level),.5,now)+DbAt(a,source)-Quantile(from,s=>DbAt(a,s.Level),.5,now);
            target=Landing.FromDb(db,b);
            reason="按当前时段的舒适增益匹配";
        } else if (!cold) {
            target=(int)(Quantile(to,s=>s.Level,.5,now)+source-Quantile(from,s=>s.Level,.5,now));
            reason="驱动增益曲线不足或不单调，使用音量刻度";
        }
        target=source==0 || current==0 ? 0 : Math.Max(0,Math.Min(cap,target));
        int start=phones ? Math.Min(current,Math.Min(12,target/3)) : Math.Min(current,target);
        var plan=new LevelPlan { Start=start,Target=target,Ceiling=cap,Reason=reason,Steps=new int[6] };
        if (b!=null) { plan.StartDb=DbAt(b,start); plan.TargetDb=DbAt(b,target); plan.CeilingDb=DbAt(b,cap); }
        for (int i=0;i<plan.Steps.Length;i++) {
            double t=(i+1.0)/plan.Steps.Length;
            plan.Steps[i]=b==null ? (int)Math.Round(start+(target-start)*t) :
                Landing.FromDb(plan.StartDb.Value+(plan.TargetDb.Value-plan.StartDb.Value)*t,b);
        }
        return plan;
    }
}
