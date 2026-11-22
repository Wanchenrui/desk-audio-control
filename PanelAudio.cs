using System;
using System.Linq;
using System.Collections.Generic;

// Windows boundary for discovery and role changes. The integration test substitutes audio hardware.
internal class PanelAudio
{
    internal Action OutputWritesComplete;
    readonly AudioDiscovery.Cache discovery=new AudioDiscovery.Cache();
    public virtual List<SmartGroup> Scan() { return discovery.Read(null); }
    public virtual List<SmartGroup> ScanChanged(string[] changed) { return discovery.Read(changed); }
    public virtual string Default(int flow,int role) { return AudioCore.Default(flow,role); }
    public virtual string[] Outputs() { return AudioCore.Defaults(0); }
    public virtual string[] Inputs() { return AudioCore.Defaults(1); }
    public virtual void RouteOutputs(string[] targets,string[] expected) { AudioCore.RouteRoles(targets,0,expected,OutputWritesComplete); }
    public virtual void RouteInputs(string[] targets,string[] expected) { AudioCore.RouteRoles(targets,1,expected); }
    public virtual SceneSignals Context(List<SmartGroup> groups,string[] roles)
    {
        return SceneContext.Read(groups.SelectMany(g => g.Inputs),groups.SelectMany(g => g.Outputs),roles,true,true);
    }
    public virtual GainPoint[] GainPoints(string id) { return AudioCore.GainPoints(id); }
    public virtual int Level(string id) { return AudioCore.Volume(id); }
    public virtual void SetLevel(string id,int value) { AudioCore.SetVolume(id,value); }
    public virtual bool Muted(string id) { return AudioCore.Muted(id); }
    public virtual float Peak(string id) { return AudioCore.Peak(id); }
    public virtual string TuningGuard()
    {
        try { return SceneContext.Read(AudioCore.List(1),AudioCore.List(0),Outputs(),true).Guard(); }
        catch (Exception) { return "无法确认设备占用"; }
    }
    public virtual SwitchGuard RouteGuard(string[] roles)
    {
        try { return SwitchGuard.Evaluate(SceneContext.Read(AudioCore.List(1),AudioCore.List(0),roles)); }
        catch (Exception) { return SwitchGuard.Evaluate(new SceneSignals { Unknown=true }); }
    }

    public void RoutePair(string[] outputs,string[] before,string[] inputs,string[] beforeInputs)
    {
        if (!Outputs().SequenceEqual(before,StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("默认输出已经变化");
        if (inputs == null) { RouteOutputs(outputs,before); return; }
        RouteInputs(inputs,beforeInputs);
        try { RouteOutputs(outputs,before); }
        catch (Exception failure)
        {
            try { RouteInputs(beforeInputs,inputs); }
            catch (Exception rollback) { throw new InvalidOperationException("输出切换失败，原话筒角色未能完整恢复",new AggregateException(failure,rollback)); }
            throw;
        }
    }

    public void Restore(DecisionRecord action)
    {
        var applied = new[] { action.Target,action.Target,action.Target };
        if (action.Result != "prepared")
        {
            RoutePair(action.Before,applied,action.InputBefore,action.InputAfter);
            return;
        }
        // After interruption the capture half, or only some output roles, may have committed.
        var outputs = Outputs();
        var inputs = action.InputBefore == null ? null : Inputs();
        CheckInterrupted(outputs,action.Before,applied);
        if (inputs != null) CheckInterrupted(inputs,action.InputBefore,action.InputAfter);
        RoutePair(action.Before,outputs,action.InputBefore,inputs);
    }
    static void CheckInterrupted(string[] current,string[] before,string[] applied)
    {
        for (int i = 0; i < current.Length; i++)
            if (!RouteTransaction.Equal(current[i],before[i]) && !RouteTransaction.Equal(current[i],applied[i]))
                throw new InvalidOperationException("默认角色已变化，已保留当前选择");
    }
}
