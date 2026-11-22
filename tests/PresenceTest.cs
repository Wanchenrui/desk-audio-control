using System;
using System.Collections.Generic;

internal static class PresenceTest
{
    static void Expect(int cap, AudioItem port, bool arrival, int corrections)
    {
        var state=DevicePresence.Assess(port,false,arrival,false,corrections);
        if (state.MaxLevel!=cap) throw new Exception(state.Reason);
    }
    public static void Main()
    {
        var model=new DevicePresence();
        var empty=new List<SmartGroup>();
        var g=new SmartGroup { Key="receiver" }; var receiver=new AudioItem { Id="rx",ParentPresent=true };
        g.Outputs.Add(receiver);
        var connected=new List<SmartGroup> { g };
        var audible=new HashSet<string>(); var history=new Dictionary<string,int>();
        model.Observe(connected,empty,audible,false,true,0,null,history);
        if (receiver.Presence.MaxLevel!=1) throw new Exception("cold start");
        model.Observe(empty,connected,audible,true,true,1000,null,history);
        model.Observe(connected,empty,audible,true,true,1400,null,history);
        if (receiver.Presence.MaxLevel!=1) throw new Exception("driver restart must not confirm");
        model.Observe(empty,connected,audible,true,true,10000,null,history);
        model.Observe(connected,empty,audible,true,true,20000,null,history);
        if (receiver.Presence.MaxLevel!=3) throw new Exception("fresh physical insertion");
        model.Observe(connected,connected,audible,true,true,55000,null,history);
        if (receiver.Presence.MaxLevel!=1) throw new Exception("arrival evidence expires");
        model.Observe(empty,connected,audible,true,true,60000,null,history);
        model.Observe(connected,empty,audible,true,true,62000,null,history);
        if (receiver.Presence.MaxLevel!=3) throw new Exception("arrival at the bounce boundary");
        model.Observe(connected,connected,audible,true,true,91999,null,history);
        if (receiver.Presence.MaxLevel!=3) throw new Exception("unchanged endpoint retains arrival evidence");
        var port=new AudioItem { Id="usb", ParentPresent=true };
        Expect(1,port,false,0);
        Expect(3,port,true,0);
        Expect(0,port,true,3);
        port.LinkConnected=false; Expect(-1,port,false,0);
        port.LinkConnected=null; port.JackConnected=true; Expect(3,port,false,3);
        port.JackConnected=false; Expect(-1,port,true,0);
        if (DevicePresence.Assess(port,true,false,false,0).MaxLevel!=0) throw new Exception("contradictory evidence");
    }
}
