using System;
using System.Linq;

public static class DiscoveryTest {
    static void Check(bool ok,string label) {
        if(!ok)throw new Exception(label);
        Console.WriteLine("PASS " + label);
    }
    static AudioDiscovery.Evidence R(string id,int flow,int form,string parent,string root,string container,string serial,string name) {
        return new AudioDiscovery.Evidence{Item=new AudioItem{Id=id,Name=name,Bus=name,Flow=flow,State=1},Parent=parent,
            UsbDevice=root,Container=container,Serial=serial,Adapter=name,Form=form};
    }
    public static void Main() {
        const string model=@"USB\VID_CAFE&PID_0011";
        var a=R("ep1",0,1,model+@"&MI_00\a",model+@"\portA","a",null,"Replacement");
        var b=R("ep2",1,4,model+@"&MI_01\b",model+@"\portA","a",null,"Replacement");
        var grouped=AudioDiscovery.GroupRows(new[]{a,b});
        Check(grouped.Count==1&&grouped[0].Inputs.Count==1&&grouped[0].Outputs.Count==1&&!grouped[0].Infrastructure,"Unseen USB duplex grouped by whole USB parent");
        var clone=R("ep3",0,1,model+@"&MI_00\c",model+@"\portB","b",null,"Replacement");
        Check(AudioDiscovery.GroupRows(new[]{clone})[0].Key==grouped[0].Key,"Single serial-less USB device can move ports");
        var twins=AudioDiscovery.GroupRows(new[]{a,clone});
        Check(twins.Count==2&&twins[0].Key!=twins[1].Key&&twins.All(g=>g.Key!=grouped[0].Key),"Concurrent identical models have distinct keys and do not inherit ambiguous preference");
        Check(twins.Any(g=>g.Key==AudioDiscovery.GroupRows(new[]{clone})[0].Key),"Ambiguity remembered during session");
        a.Serial="SERIAL-R";clone.Serial="SERIAL-R";
        Check(AudioDiscovery.GroupRows(new[]{a,clone}).Select(g=>g.Key).Distinct().Count()==2,"Cloned hardware serials remain separate");
        var stereo=R("s",0,3,@"BTHENUM\a",null,"bt",null,"Headset Stereo");
        var hfp=R("h",0,5,@"BTHHFENUM\b",null,"bt",null,"Headset Hands-Free AG Audio");
        var mic=R("m",1,5,@"BTHHFENUM\b",null,"bt",null,"Headset Hands-Free AG Audio");
        var bt=AudioDiscovery.GroupRows(new[]{stereo,hfp,mic});
        Check(bt.Count==1&&bt[0].Name=="Headset"&&bt[0].Outputs.Count==2&&bt[0].Inputs.Count==1,"Bluetooth stereo/call profiles share one physical group");
        stereo.Container=hfp.Container=mic.Container=null;
        stereo.BluetoothAddress=hfp.BluetoothAddress=mic.BluetoothAddress="001122334455";
        Check(AudioDiscovery.GroupRows(new[]{stereo,hfp,mic}).Count==1,"Bluetooth address from ancestry groups profiles without container");
        stereo.BluetoothAddress=hfp.BluetoothAddress=mic.BluetoothAddress=null;
        var ambiguous=AudioDiscovery.GroupRows(new[]{stereo,hfp,mic});
        Check(ambiguous.Count==2&&ambiguous.Select(g=>g.Name).Distinct().Count()==2,"Matching Bluetooth names alone never merge uncertain devices; duplicate display names distinguished");
        var monitor=R("HDMI1",0,9,@"HDAUDIO\slot",null,null,null,"Monitor");
        var monitor2=R("HDMI2",0,9,@"HDAUDIO\slot",null,null,null,"Second monitor");
        var soft=R("soft",0,1,@"ROOT\MEDIA\0007",null,null,null,"Driver");
        var missing=R("unknown",0,1,null,null,null,null,"Unidentified audio");
        var ports=AudioDiscovery.GroupRows(new[]{monitor,monitor2,soft,missing});
        Check(ports.Count==4&&ports.Count(g=>g.Infrastructure)==3&&ports.Count(g=>g.Virtual)==1,"Two display connectors and software classified separately, unknown physical candidate surfaced");
        var analogOut=R("jack",0,3,@"HDAUDIO\codec",null,null,null,"Codec");
        var analogIn=R("line",1,2,@"HDAUDIO\codec",null,null,null,"Codec");
        analogOut.Sensed=true;analogIn.Sensed=false;
        var analog=AudioDiscovery.GroupRows(new[]{analogOut,analogIn})[0];
        Check(analog.SensedOutputs.SequenceEqual(new[]{"jack"})&&analog.SensedInputs.Count==0,"Analog jack presence stays scoped to the confirmed direction");
    }
}
