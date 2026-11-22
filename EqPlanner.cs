using System;
using System.Linq;

internal sealed class EqPlan
{
    public EqPreset Filter;
    public int Strength, Rate;
    public double HeadroomDb;
}
internal static class EqPlanner
{
    internal static EqPlan Plan(EqPreset source,int strength,int rate)
    {
        if (strength<0 || strength>100 || rate<8000 || source.Rows.Any(r=>r.Hz>=rate*.5))
            throw new ArgumentOutOfRangeException("rate","强度超出范围或端点采样率不足");
        var rows=source.Rows.Select(r=>new EqPreset.EqRow(r.Type,r.Hz,r.Gain*strength/100.0,r.Q)).ToArray();
        var bands=rows.Select(r=>new EqPreset.Band(r.Type,r.Hz,r.Gain,r.Q,rate)).ToArray();
        double peak=0;
        if (strength>0) {
            // Include shelf limits and filter centers in addition to the log-frequency grid.
            double upper=Math.Min(20000,rate*.5);
            var frequencies=Enumerable.Range(0,2049).Select(i=>20*Math.Pow(upper/20,i/2048.0))
                .Concat(rows.Select(r=>r.Hz)).Concat(new[]{0.0,rate*.5});
            foreach (double hz in frequencies) {
                double c=Math.Cos(2*Math.PI*hz/rate);
                peak=Math.Max(peak,bands.Sum(b=>b.ResponseDb(c,2*c*c-1)));
            }
        }
        double preamp=strength==0 ? 0 : strength==100 ? Math.Min(source.Preamp,-peak) : -peak-.1;
        preamp=Math.Floor(preamp*1000)/1000;
        return new EqPlan { Strength=strength,Rate=rate,HeadroomDb=-preamp-peak,Filter=source.WithParameters(rows,preamp) };
    }
}
