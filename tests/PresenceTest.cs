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
