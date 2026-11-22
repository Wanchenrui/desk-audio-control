using System.Collections.Generic;

// Windows boundary for discovery and role changes. The integration test substitutes audio hardware.
internal class PanelAudio
{
    public virtual List<SmartGroup> Scan() { return AudioDiscovery.Scan(); }
    public virtual string Default(int flow,int role) { return AudioCore.Default(flow,role); }
    public virtual string[] Outputs() { return AudioCore.Defaults(0); }
    public virtual void RouteOutputs(string[] targets,string[] expected) { AudioCore.RouteRoles(targets,0,expected); }
    public virtual bool InUse(string id) { return AudioCore.InUse(id); }
}
