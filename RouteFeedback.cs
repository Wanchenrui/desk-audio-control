using System;
using System.Collections.Generic;
using System.Linq;

// Posterior acceptance odds within one device/scene. No exploration on live routes.
internal sealed class RouteFeedback
{
    const int Prior=4, MinimumOutcomes=8;
    public int Kept, Corrected;
    public int Offset
    {
        get {
            if (Kept+Corrected<MinimumOutcomes) return 0;
            double odds=(Prior+Kept)/(double)(Prior+Corrected);
            return (int)Math.Round(Math.Max(-18,Math.Min(18,10*Math.Log(odds))));
        }
    }
    public static RouteFeedback From(IEnumerable<DecisionRecord> rows,string key,string scene,DateTime now)
    {
        var local=rows.Where(r=>!r.Imported && r.DeviceKey==key && r.Scene==scene).ToList();
        var corrected=new HashSet<string>(local.Where(r=>r.Result=="undone" || r.Result=="corrected").Select(r=>r.Related));
        var feedback=new RouteFeedback();
        foreach (var action in local.Where(r=>r.Result=="applied" && r.Utc.ToUniversalTime()<=now.AddSeconds(-30))) {
            if (corrected.Contains(action.Id)) feedback.Corrected++; else feedback.Kept++;
        }
        return feedback;
    }
    public static double Probability(double lead) { return 1/(1+Math.Exp(-lead/20.0)); }
}
