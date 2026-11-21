using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

// Device model of the panel: what is eligible, what is shown, and how a refresh reconciles with Windows.
internal sealed partial class AudioPanel
{
    static bool Same(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    static bool ContainsId(IEnumerable<AudioItem> ports, string id) { return ports.Any(p => Same(p.Id, id)); }
    SmartGroup Selected() { return devices.SelectedGroup; }
    AudioItem Chosen(int flow) { return chosen[flow]; }
    bool Eligible(SmartGroup g, int flow)
    {
        return SmartPolicy.PrimaryForFlow(g, groups, flow, flow == 0 ? defaultOut : defaultIn) ||
               (SmartPolicy.Mode(g) == "auto" && !g.Virtual && plugs.Recent(g, flow));
    }
    bool Primary(SmartGroup g) { return Eligible(g, 0) || Eligible(g, 1); }
    string NameFor(string id, int flow)
    {
        var g = groups.FirstOrDefault(x => ContainsId(flow == 0 ? x.Outputs : x.Inputs, id));
        return g == null ? "无" : g.Name;
    }
    void SetStatus(string text, Tone tone)
    {
        if (status == null) return;
        status.Role = tone; status.Text = text;
    }

    // Segoe MDL2 Assets glyph for the kind of device a group represents.
    static string GlyphFor(SmartGroup g, string role)
    {
        if (g.Virtual) return "";
        if (g.Kind == "显示器音频") return "";
        if (role.Contains("与输入") || role.Contains("双向")) return "";
        if (role.Contains("耳机")) return "";
        if (role.Contains("话筒") || role.Contains("录音")) return "";
        return "";
    }
    DeviceRow RowFor(SmartGroup g)
    {
        string role = SmartPolicy.RoleLabel(g);
        return new DeviceRow {
            Group = g, Title = g.Name, Sub = role == g.Kind ? role : role + "  ·  " + g.Kind.Replace("音频", ""),
            Glyph = GlyphFor(g, role), Out = ContainsId(g.Outputs, defaultOut), In = ContainsId(g.Inputs, defaultIn), Dim = !Primary(g)
        };
    }

    void RefreshDevices(bool allowRoute)
    {
        if (busy || IsDisposed) return;
        busy = true;
        try
        {
            groups = AudioDiscovery.Scan(); plugs.Observe(groups);
            defaultOut = AudioCore.Default(0, 1); defaultIn = AudioCore.Default(1, 1);
            var outputGroups = groups.Where(g => !g.Virtual && Eligible(g, 0) && SmartPolicy.Choices(g, 0, false).Count > 0 &&
                (!g.Infrastructure || plugs.Recent(g, 0) || SmartPolicy.Mode(g) != "auto")).ToList();
            if (allowRoute && preferNew && previousOutputs != null)
            {
                var added = outputGroups.Where(g => !previousOutputs.Contains(g.Key)).ToList();
                if (added.Count == 1)
                {
                    var next = SmartPolicy.Pick(added[0], 0, defaultOut);
                    if (next != null && !Same(next.Id, defaultOut)) { AudioCore.Route(next.Id, 0); defaultOut = next.Id; }
                }
            }
            previousOutputs = new HashSet<string>(outputGroups.Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
            var ordered = groups.OrderBy(g => !Primary(g)).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var shown = ordered.Where(g => Primary(g) || showAll.Checked).ToList();
            counts.Text = "日常设备  " + ordered.Count(Primary) + (showAll.Checked ? "    ·    全部 " + ordered.Count : "");
            var preferred = shown.FirstOrDefault(g => ContainsId(g.Outputs, defaultOut));
            devices.SetRows(shown.Select(g => RowFor(g)).ToList(), preferred == null ? null : preferred.Key);
            ShowSelection();
            string o = NameFor(defaultOut, 0), m = NameFor(defaultIn, 1);
            rail.Defaults(o, m);
            if (statusUntil == 0) SetStatus(hint, Tone.Muted);
            string tip = "桌面音频 · " + o; tray.Text = tip.Length > 62 ? tip.Substring(0, 62) : tip;
        }
        catch (Exception e)
        {
            statusUntil = 0; SetStatus("设备状态暂时不可用 · " + e.Message, Tone.Warn); LocalSettings.Log(e.ToString());
        }
        finally { busy = false; nextScan = clock.ElapsedMilliseconds + (events.Registered ? 10000 : 2500); }
    }

    void ShowSelection()
    {
        var g = Selected();
        roleButton.Enabled = g != null;
        roleButton.Text = g == null ? "识别用途" : "用途 · " + modeText[Math.Max(0, Array.IndexOf(modes, SmartPolicy.Mode(g)))];
        heroName.Text = g == null ? "等待设备连接" : g.Name;
        heroDetail.Text = g == null ? "接入 USB 或蓝牙音频设备后会自动出现" : g.Details;
        hero.Glyph = g == null ? "" : GlyphFor(g, SmartPolicy.RoleLabel(g));
        hero.Live = g != null && (ContainsId(g.Outputs, defaultOut) || ContainsId(g.Inputs, defaultIn));
        hero.Invalidate();
        var found = new List<AudioItem>[2];
        for (int f = 0; f < 2; f++) found[f] = g == null ? new List<AudioItem>() : SmartPolicy.Choices(g, f, showAll.Checked);
        string sig = (g == null ? "" : g.Key) + "|" + String.Join("|", found.SelectMany(x => x).Select(x => x.Id));
        if (sig != portSignature)
        {
            portSignature = sig;
            for (int f = 0; f < 2; f++)
            {
                string old = chosen[f] == null ? null : chosen[f].Id;
                var pick = g == null ? null : SmartPolicy.Pick(g, f, f == 0 ? defaultOut : defaultIn);
                choices[f] = found[f];
                int i = found[f].FindIndex(d => Same(d.Id, old));
                if (i < 0 && pick != null) i = found[f].FindIndex(d => Same(d.Id, pick.Id));
                chosen[f] = i >= 0 ? found[f][i] : (found[f].Count == 0 ? null : found[f][0]);
            }
        }
        RefreshLevels();
    }

    void RefreshLevels()
    {
        for (int f = 0; f < 2; f++)
        {
            var p = chosen[f]; bool ok = p != null;
            mute[f].Enabled = route[f].Enabled = volume[f].Enabled = picker[f].Enabled = ok;
            picker[f].Text = ok ? p.Name : (f == 0 ? "没有可用的播放端口" : "没有可用的录音端口");
            if (f == 0) balance.Enabled = fx.Enabled = ok;
            if (!ok)
            {
                laneValue[f].Text = "—"; mute[f].Text = "静音"; mute[f].On = false; route[f].Text = "设为默认"; route[f].On = false;
                volume[f].Set(0); meter[f].Feed(0);
                continue;
            }
            try
            {
                int n = AudioCore.Volume(p.Id);
                if (!volume[f].Dragging) volume[f].Set(n);
                laneValue[f].Text = n + " %";
                bool muted = AudioCore.Muted(p.Id);
                mute[f].Text = muted ? "已静音" : "静音"; mute[f].On = muted;
                laneValue[f].Role = muted ? Tone.Muted : Tone.Ink;
                bool current = Same(p.Id, f == 0 ? defaultOut : defaultIn);
                route[f].Text = current ? "当前默认" : "设为默认"; route[f].On = current;
            }
            catch (COMException) { laneValue[f].Text = "离线"; }
        }
    }

    // Peak is linear 0..1; the meter shows it on a 54 dB scale.
    void FeedMeters()
    {
        for (int f = 0; f < 2; f++)
        {
            var p = chosen[f]; float v = 0f;
            try { if (p != null) v = peaks[f].Read(p.Id); else peaks[f].Reset(); }
            catch (COMException) { }
            catch (InvalidCastException) { }
            meter[f].Feed(v <= 0.002f ? 0f : (float)((20.0 * Math.Log10(v) + 54.0) / 54.0));
        }
    }

    List<AudioItem> OutputCycle()
    {
        return groups.Where(g => Eligible(g, 0)).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .Where(g => SmartPolicy.Choices(g, 0, false).Count > 0).Select(g => SmartPolicy.Pick(g, 0, defaultOut)).Where(p => p != null).ToList();
    }
}
