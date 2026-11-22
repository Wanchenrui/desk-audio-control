using System;
using System.Collections.Generic;
using System.Linq;

internal sealed class PresenceResult
{
    internal readonly string State,Reason;
    internal readonly int MaxLevel;
    internal PresenceResult(string state,string reason,int level) { State=state; Reason=reason; MaxLevel=level; }
}

internal static class DevicePresence
{
    // A present USB receiver alone says nothing about the headset behind it.
    internal static PresenceResult Assess(AudioItem port,bool playing,bool arrived,bool selected,int corrections)
    {
        bool negative=port.JackConnected==false || port.LinkConnected==false;
        bool positive=port.JackConnected==true || port.LinkConnected==true;
        if (negative && (positive || playing)) return new PresenceResult("不明","连接状态与其他在线证据冲突",0);
        if (negative) return new PresenceResult("已离线",port.JackConnected==false ? "驱动报告插孔已拔出" : "蓝牙链路已断开",-1);
        if (positive) return new PresenceResult("确认在线",port.JackConnected==true ? "驱动检测到插头" : "蓝牙链路已连接",3);
        if (corrections>=3) return new PresenceResult("不明","该设备曾多次在自动切换后被立即改回",0);
        if (playing || selected) return new PresenceResult("确认在线",playing ? "有活动会话且端点峰值非零" : "近期由用户选中",3);
        if (arrived && port.ParentPresent) return new PresenceResult("确认在线","检测到新端口及其即插即用父节点",3);
        return new PresenceResult("可能在线","只有活动端点，缺少实物连接佐证",1);
    }

    internal static Dictionary<string,int> Corrections(IEnumerable<DecisionRecord> rows)
    {
        var actions=rows.Where(r=>!r.Imported && r.Result=="applied").ToLookup(r=>r.Id);
        return rows.Where(r=>!r.Imported && r.Related!=null && r.DeviceKey!=null &&
            (r.Result=="corrected" || r.Result=="undone") && actions[r.Related].Any(a=>
            a.DeviceKey==r.DeviceKey && (r.Utc-a.Utc).TotalSeconds>=0 && (r.Utc-a.Utc).TotalSeconds<=30))
            .GroupBy(r=>r.DeviceKey,StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g=>g.Key,g=>g.Select(r=>r.Related).Distinct().Count(),StringComparer.OrdinalIgnoreCase);
    }

    internal static bool Available(AudioItem p) { return p.Presence==null || p.Presence.MaxLevel>=0; }
    internal static bool Confirmed(AudioItem p) { return p.Presence!=null && p.Presence.MaxLevel==3; }
}
