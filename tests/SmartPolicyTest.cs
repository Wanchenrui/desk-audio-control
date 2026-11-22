using System;
using System.Collections.Generic;
using System.Linq;

// Offline smoke cases. All device evidence and user choices live in this process only.
public static class SmartPolicyTest
{
    static int assertions;
    static void Check(bool result, string scenario)
    {
        if (!result) throw new Exception("Failed: " + scenario);
        Console.WriteLine("PASS " + scenario);
        assertions++;
    }
    static AudioDiscovery.Evidence E(string id, int flow, string parent, string adapter, int form, string serial = null)
    {
        var device = new AudioItem { Id = id, Name = adapter, Bus = adapter, Flow = flow, State = 1 };
        return new AudioDiscovery.Evidence { Item = device, Parent = parent, UsbDevice = parent,
            Serial = serial, Adapter = adapter, Form = form };
    }
    public static int Main()
    {
        var onboard = E("onboard", 0, @"HDAUDIO\MOCK", "Onboard", 1);
        var virtualOut = E("virtual", 0, @"ROOT\MockVirtual", "Mock cable", 1);
        var initial = AudioDiscovery.GroupRows(new[] { onboard, virtualOut });
        var prefs = new Dictionary<string,string>();
        Func<SmartGroup,string> mode = g => prefs.ContainsKey(g.Key) ? prefs[g.Key] : "auto";
        var loc = initial.Single(g => g.Name == "Onboard");
        var virt = initial.Single(g => g.Virtual);
        Check(SmartPolicy.IsPrimary(loc, initial, 0, null, mode), "onboard physical output when it is the only physical choice");
        Check(!SmartPolicy.IsPrimary(virt, initial, 0, "virtual", mode), "virtual default remains available under all devices");
        prefs[virt.Key] = "show";
        Check(SmartPolicy.IsPrimary(virt, initial, 0, "virtual", mode), "explicitly shown virtual device");
        prefs.Clear();

        var usbOut = E("new-out", 0, @"USB\VID_1001&PID_2201\A", "Never seen USB interface", 1, "unit99");
        var usbIn  = E("new-in", 1, usbOut.Parent, usbOut.Adapter, 4, "unit99");
        var plugged = AudioDiscovery.GroupRows(new[] { onboard, virtualOut, usbOut, usbIn });
        var usb = plugged.Single(g => !g.Infrastructure);
        Check(usb.Outputs.Count == 1 && usb.Inputs.Count == 1, "new duplex physical grouping");
        Check(SmartPolicy.IsPrimary(usb, plugged, 0, null, mode) && SmartPolicy.IsPrimary(usb, plugged, 1, null, mode), "new duplex exposes both directions");
        Check(SmartPolicy.DescribeRole(usb, "auto", p => p.Flow == 0 ? 1 : 4) == "双向音频", "generic USB duplex stays unclassified");
        Check(!SmartPolicy.IsPrimary(loc, plugged, 0, usbOut.Item.Id, mode), "unused onboard falls into other devices");
        Check(SmartPolicy.IsPrimary(loc, plugged, 0, onboard.Item.Id, mode), "physical default remains visible");
        prefs[usb.Key] = "microphone";
        Check(!SmartPolicy.IsPrimary(usb, plugged, 0, null, mode) && SmartPolicy.IsPrimary(usb, plugged, 1, null, mode), "user microphone role restricts output only");
        Check(SmartPolicy.IsPrimary(loc, plugged, 0, null, mode), "fallback evaluated separately for playback");
        Check(!SmartPolicy.FilterPorts(usb, 0, false, mode(usb)).Any() && SmartPolicy.FilterPorts(usb, 0, true, mode(usb)).Count == 1, "all ports exposes collapsed connectors");
        Check(!SmartPolicy.FilterPorts(usb, 1, false, "headphones").Any() && !SmartPolicy.FilterPorts(usb, 1, false, "speaker").Any(), "speaker and headphone labels collapse recording ports");
        Check(SmartPolicy.DescribeRole(usb,"auto", p => p.Flow == 0 ? 3 : 4) == "耳机与输入", "reported headphone form is recognized");
        prefs[usb.Key] = "hide";
        Check(!SmartPolicy.IsPrimary(usb, plugged, 0, usbOut.Item.Id, mode) && !SmartPolicy.IsPrimary(usb, plugged, 1, usbIn.Item.Id, mode), "explicitly hidden group");
        prefs[usb.Key] = "microphone";
        var rebound = E("replacement-endpoint-id", 1, @"USB\VID_1001&PID_2201\NewPort", usbIn.Adapter, 4, "unit99");
        var returned = AudioDiscovery.GroupRows(new[] { rebound }).Single();
        Check(returned.Key == usb.Key && mode(returned) == "microphone", "serial-backed unit survives endpoint ID and USB port change");
        var model2 = E("replacement-hardware", 0, @"USB\VID_1002&PID_2202\B", "Replacement", 3);
        var replacement = AudioDiscovery.GroupRows(new[] { model2, onboard });
        var newModel = replacement.Single(g => !g.Infrastructure);
        Check(mode(newModel) == "auto" && SmartPolicy.IsPrimary(newModel, replacement, 0, null, mode), "different replacement model is discovered automatically");
        Check(SmartPolicy.DescribeRole(newModel,"auto",p => 3) == "耳机", "render-only headphone form");
        var sameModel = E("separate-unit", 0, model2.Parent + "-other", model2.Adapter, 3);
        var twins = AudioDiscovery.GroupRows(new[] { model2, sameModel });
        Check(twins.Count == 2 && twins.Select(g=>g.Key).Distinct().Count() == 2, "simultaneous identical serial-less hardware remains separate");

        usbOut.Item.Presence=DevicePresence.Assess(new AudioItem { JackConnected=false },false,false,false,0);
        Check(!SmartPolicy.IsPrimary(usb,plugged,0,null,mode),"an unplugged output does not mask fallback devices");
        var hands = E("hf",0,@"BTHENUM\MOCK","Mock Hands-Free AG Audio",5).Item;
        var stereo = E("a2dp",0,@"BTHENUM\MOCK","Mock Stereo",3).Item;
        var extra = E("extra",0,@"BTHENUM\MOCK","Mock output",3).Item;
        var profiles = new List<AudioItem> { hands, stereo, extra };
        Check(SmartPolicy.SelectPort(profiles,0,"hf",null) == stereo, "prefer stereo to default hands-free playback");
        Check(SmartPolicy.SelectPort(profiles,0,"extra",null) == extra, "retain current full-quality playback profile");
        Check(SmartPolicy.SelectPort(profiles,0,"extra","hf") == hands, "honor user's explicit chosen profile");
        Check(SmartPolicy.SelectPort(profiles,0,"extra","missing") == extra, "stale profile falls back");
        Check(SmartPolicy.SelectPort(new List<AudioItem>(),0,null,null) == null, "removed output yields no stale routing target");
        Console.WriteLine(assertions + " synthetic policy/discovery checks passed.");
        return 0;
    }
}
