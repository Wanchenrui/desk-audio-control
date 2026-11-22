using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

public static class SmartPolicy
{
    static readonly string[] Modes = { "auto", "speaker", "headphones", "microphone", "duplex", "show", "hide" };
    static bool Same(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

    static string GroupHash(SmartGroup g)
    {
        if (g == null || String.IsNullOrWhiteSpace(g.Key)) throw new ArgumentException("Device group has no key.");
        using (var hash = SHA256.Create())
        {
            var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(g.Key.ToUpperInvariant()));
            return BitConverter.ToString(bytes).Replace("-", "");
        }
    }
    static string ReadSetting(SmartGroup g, string suffix)
    {
        return AppConfig.Store.Get("DeviceRoles/" + GroupHash(g) + suffix);
    }
    static void WriteSetting(SmartGroup g, string suffix, string value)
    {
        AppConfig.Store.Put("DeviceRoles/" + GroupHash(g) + suffix,value);
    }
    public static string Mode(SmartGroup g)
    {
        string mode = ReadSetting(g, ".Role");
        return Array.IndexOf(Modes, mode) >= 0 ? mode : "auto";
    }
    public static void SetMode(SmartGroup g, string mode)
    {
        if (Array.IndexOf(Modes, mode) < 0) throw new ArgumentOutOfRangeException("mode");
        WriteSetting(g, ".Role", mode == "auto" ? null : mode);
    }

    static List<AudioItem> Ports(SmartGroup group, int flow)
    {
        if (flow == 0) return group.Outputs;
        if (flow == 1) return group.Inputs;
        throw new ArgumentOutOfRangeException("flow");
    }
    static bool Allowed(string mode, int flow)
    {
        if (mode == "hide") return false;
        if (mode == "speaker" || mode == "headphones") return flow == 0;
        if (mode == "microphone") return flow == 1;
        return true;
    }
    public static List<AudioItem> Choices(SmartGroup g, int flow, bool allPorts)
    {
        return FilterPorts(g, flow, allPorts, allPorts ? "auto" : Mode(g));
    }
    internal static List<AudioItem> FilterPorts(SmartGroup g, int flow, bool allPorts, string mode)
    {
        var candidates = Ports(g, flow);
        if (allPorts) return new List<AudioItem>(candidates);
        if (Allowed(mode,flow)) return candidates.Where(DevicePresence.Available).ToList();
        return new List<AudioItem>();
    }
    internal static bool IsPrimary(SmartGroup g, List<SmartGroup> all, int flow, string defaultId, Func<SmartGroup,string> modeOf)
    {
        string mode = modeOf(g);
        var ports = Ports(g, flow);
        if (!ports.Any(DevicePresence.Available) || !Allowed(mode, flow)) return false;
        if (mode != "auto") return true;
        if (g.Virtual) return false;
        if (ports.Any(DevicePresence.Confirmed) || !g.Infrastructure || ports.Any(p => Same(p.Id, defaultId))) return true;
        // A real built-in, HDMI or other infrastructure endpoint is a fallback for its own direction.
        return !all.Any(other => {
            if (other.Virtual || !Ports(other,flow).Any(DevicePresence.Available)) return false;
            string otherMode = modeOf(other);
            return (!other.Infrastructure || otherMode != "auto") && Allowed(otherMode, flow);
        });
    }
    public static bool PrimaryForFlow(SmartGroup g, List<SmartGroup> all, int flow, string defaultId)
    {
        return IsPrimary(g, all, flow, defaultId, Mode);
    }
    public static bool Primary(SmartGroup g, List<SmartGroup> all, string outDefault, string inDefault)
    {
        return PrimaryForFlow(g, all, 0, outDefault) || PrimaryForFlow(g, all, 1, inDefault);
    }

    static string PortSetting(int flow)
    {
        if (flow == 0) return ".Output";
        if (flow == 1) return ".Input";
        throw new ArgumentOutOfRangeException("flow");
    }
    public static void Remember(SmartGroup g, int flow, string id)
    {
        if (id != null && !Ports(g, flow).Any(p => Same(p.Id, id)))
            throw new ArgumentException("Selected port does not belong to this group.");
        WriteSetting(g, PortSetting(flow), id);
    }
    internal static bool HandsFree(AudioItem p)
    {
        string text = (p.Name ?? "") + " " + (p.Bus ?? "");
        string[] hints = { "hands-free", "hands free", "handsfree", "ag audio", "ag-audio", "免提", "通话音频", "freisprechen", "mains libres", "manos libres" };
        return p.HandsFreeProfile || hints.Any(s => text.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
    }
    internal static AudioItem StereoOutput(SmartGroup g,string defaultId)
    {
        return SelectStereo(g,defaultId,ReadSetting(g,".Output"));
    }
    internal static AudioItem SelectStereo(SmartGroup g,string defaultId,string remembered)
    {
        return SelectPort(g.Outputs.Where(p => DevicePresence.Available(p) && !HandsFree(p) && p.Channels != 1).ToList(),0,defaultId,remembered);
    }
    internal static AudioItem SelectPort(List<AudioItem> choices, int flow, string defaultId, string rememberedId)
    {
        var explicitPort = choices.FirstOrDefault(p => Same(p.Id, rememberedId));
        if (explicitPort != null) return explicitPort;
        // Capture can legitimately be mono hands-free audio. Prefer stereo playback when both profiles are exposed.
        return choices.OrderBy(p => flow == 0 && HandsFree(p) ? 1 : 0)
                      .ThenBy(p => Same(p.Id, defaultId) ? 0 : 1)
                      .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }
    public static AudioItem Pick(SmartGroup g, int flow, string defaultId)
    {
        return SelectPort(Choices(g, flow, false), flow, defaultId, ReadSetting(g, PortSetting(flow)));
    }
    internal static string DescribeRole(SmartGroup g, string mode, Func<AudioItem,int> form)
    {
        if (mode == "speaker") return "音箱";
        if (mode == "headphones") return "耳机";
        if (mode == "microphone") return "话筒";
        if (mode == "duplex") return "双向音频";
        if (g.Virtual) return "虚拟音频";
        if (g.Outputs.Count == 0)
            return g.Inputs.Any(p => form(p) == 4 || form(p) == 5) ? "话筒" : "录音输入";
        bool headphones = g.Outputs.Any(p => form(p) == 3 || form(p) == 5);
        if (g.Inputs.Count > 0) return headphones ? "耳机与输入" : "双向音频";
        if (headphones) return "耳机";
        if (g.Outputs.Any(p => form(p) == 1)) return "扬声器输出";
        return "播放输出";
    }
    public static string RoleLabel(SmartGroup g)
    {
        return DescribeRole(g, Mode(g), p => { int form; return g.Forms.TryGetValue(p.Id,out form) ? form : -1; });
    }
}
