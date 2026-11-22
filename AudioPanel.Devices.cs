using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

// Device page: the list on the left, the selected device with its playback and recording lanes on the right.
internal sealed partial class AudioPanel
{
    DeviceList devices;
    ToggleSwitch showAll;
    Caption counts, status, heroName, heroDetail;
    Card heroCard;
    HeroTile hero;
    PillButton roleButton, balance, fx;
    readonly Card[] lane = new Card[2];
    readonly Caption[] laneName = new Caption[2], laneValue = new Caption[2];
    readonly PillButton[] picker = new PillButton[2], route = new PillButton[2], mute = new PillButton[2];
    readonly FlatSlider[] volume = new FlatSlider[2];
    readonly LevelMeter[] meter = new LevelMeter[2];
    readonly AudioCore.PeakMeter[] peaks = { new AudioCore.PeakMeter(), new AudioCore.PeakMeter() };
    readonly AudioItem[] chosen = new AudioItem[2];
    readonly List<AudioItem>[] choices = new List<AudioItem>[] { new List<AudioItem>(), new List<AudioItem>() };

    void BuildDevices(Control page)
    {
        counts = Cap(page, "", 14f, true, Tone.Ink);
        showAll = new ToggleSwitch { Text = "显示其他接口", AccessibleName = "显示其他接口" };
        showAll.CheckedChanged += delegate { portSignature = null; bus.Publish(ScanReason.Refresh); };
        page.Controls.Add(showAll);
        devices = new DeviceList();
        devices.SelectionChanged += delegate { ShowSelection(); };
        page.Controls.Add(devices);
        status = Cap(page, "", 9f, false, Tone.Muted);
        heroCard = new Card();
        page.Controls.Add(heroCard);
        hero = new HeroTile();
        heroCard.Controls.Add(hero);
        heroName = Cap(heroCard, "", 11.5f, true, Tone.Ink);
        heroDetail = Cap(heroCard, "", 9f, false, Tone.Muted);
        roleButton = new PillButton { Text = "识别用途", Kind = ButtonLook.Soft, AccessibleName = "设备用途" };
        roleButton.Click += delegate { PickRole(); };
        heroCard.Controls.Add(roleButton);
        for (int f = 0; f < 2; f++) BuildLane(page, f);
    }

    void BuildLane(Control page, int f)
    {
        int flow = f;
        string word = f == 0 ? "播放" : "录音";
        lane[f] = new Card();
        page.Controls.Add(lane[f]);
        laneName[f] = Cap(lane[f], word, 11f, true, Tone.Ink);
        picker[f] = new PillButton { Kind = ButtonLook.Ghost, Glyph = "", AccessibleName = word + "端口" };
        picker[f].Click += delegate { PickPort(flow); };
        lane[f].Controls.Add(picker[f]);
        volume[f] = new FlatSlider { AccessibleName = word + "音量" };
        volume[f].ValueChanged += delegate
        {
            if (busy) return;
            Act(() =>
            {
                var p = Chosen(flow);
                if (p == null) return;
                int n = volume[flow].Value;
                quietUntil = clock.ElapsedMilliseconds + 30000;
                if (ramp!=null && Same(ramp.Endpoint,p.Id)) StopLanding();
                audio.SetLevel(p.Id,n);
                if (flow==0 && Selected()!=null && !Selected().Virtual) QueueComfort(Selected().Key,p.Id,n,1500);
                laneValue[flow].Text = n + " %";
            }, false);
        };
        lane[f].Controls.Add(volume[f]);
        laneValue[f] = Cap(lane[f], "—", 10f, true, Tone.Ink);
        laneValue[f].Align = ContentAlignment.MiddleRight;
        meter[f] = new LevelMeter();
        lane[f].Controls.Add(meter[f]);
        route[f] = Pill(lane[f], "设为默认", ButtonLook.Soft, word + "设为默认", () => SetDefault(Chosen(flow)));
        mute[f] = Pill(lane[f], "静音", ButtonLook.Soft, word + "静音", () => Toggle(Chosen(flow)));
        if (f == 0)
        {
            balance = Pill(lane[f], "左右平衡…", ButtonLook.Soft, "左右平衡", AdjustBalance);
            fx = Pill(lane[f], "声音增强…", ButtonLook.Soft, "声音增强", Enhancements);
        }
    }

    void PickRole()
    {
        var g = Selected();
        if (g == null) return;
        ThemedMenu.Choose(roleButton, modeText, Array.IndexOf(modes, SmartPolicy.Mode(g)), n =>
            Act(() => { SmartPolicy.SetMode(g, modes[n]); portSignature = null; }));
    }
    void PickPort(int flow)
    {
        var list = choices[flow];
        var g = Selected();
        if (g == null || list.Count == 0) return;
        var names = list.Select(p => p.Name).ToList();
        int current = list.FindIndex(p => chosen[flow] != null && Same(p.Id, chosen[flow].Id));
        ThemedMenu.Choose(picker[flow], names, current, n => Act(() =>
        {
            chosen[flow] = list[n];
            SmartPolicy.Remember(g, flow, list[n].Id);
            RefreshLevels();
        }, false));
    }

    void LayoutDevices(Control page)
    {
        int pad = P(28), gap = P(20), vgap = P(12), w = page.Width, h = page.Height;
        counts.SetBounds(pad, P(20), Math.Max(0, w - 2 * pad - P(210)), P(34));
        showAll.SetBounds(w - pad - P(190), P(22), P(190), P(30));
        int top = P(68), bottom = h - P(56);
        int listW = Math.Max(P(250), (w - 2 * pad - gap) * 40 / 100);
        devices.SetBounds(pad, top, listW, Math.Max(0, bottom - top));
        status.SetBounds(pad, h - P(44), Math.Max(0, w - 2 * pad), P(28));
        int x = pad + listW + gap, cw = Math.Max(0, w - x - pad);
        int heroH = P(116), rem = Math.Max(0, bottom - top - heroH - 2 * vgap);
        int playH = Math.Max(P(156), rem / 2), capH = Math.Max(P(156), rem - playH);
        heroCard.SetBounds(x, top, cw, heroH);
        lane[0].SetBounds(x, top + heroH + vgap, cw, playH);
        lane[1].SetBounds(x, top + heroH + vgap + playH + vgap, cw, capH);
        int textW = Math.Max(0, cw - P(92) - P(18));
        hero.SetBounds(P(18), (heroH - P(60)) / 2, P(60), P(60));
        heroName.SetBounds(P(92), P(14), textW, P(30));
        heroDetail.SetBounds(P(92), P(46), textW, P(22));
        roleButton.SetBounds(P(92), P(76), Math.Min(P(250), textW), P(30));
        for (int f = 0; f < 2; f++)
        {
            int m = P(18), iw = Math.Max(0, cw - 2 * m), ch = lane[f].Height;
            int rows = P(32) + P(32) + P(12) + P(32);
            int ig = Math.Max(P(8), Math.Min(P(22), (ch - rows - P(20)) / 3));
            int y = Math.Max(P(10), (ch - rows - 3 * ig) / 2);
            laneName[f].SetBounds(m, y, P(56), P(32));
            picker[f].SetBounds(m + P(60), y, Math.Max(0, iw - P(60)), P(32));
            y += P(32) + ig;
            volume[f].SetBounds(m - P(10), y, Math.Max(0, iw - P(50)), P(32));
            laneValue[f].SetBounds(cw - m - P(56), y, P(56), P(32));
            y += P(32) + ig;
            meter[f].SetBounds(m, y, iw, P(12));
            y += P(12) + ig;
            PillButton[] row = f == 0 ? new PillButton[] { route[0], mute[0], balance, fx } : new PillButton[] { route[1], mute[1] };
            int bg = P(8), bw = (iw - (row.Length - 1) * bg) / row.Length, by = y;
            if (f == 1) bw = Math.Min(bw, P(150));
            for (int i = 0; i < row.Length; i++) row[i].SetBounds(m + i * (bw + bg), by, bw, P(32));
        }
    }
}

// Plain page background that hosts one page of the window.
internal sealed class Surface : Panel
{
    public Surface()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Window;
    }
}

// Rounded icon tile for the selected device. Live fills it with the accent color.
internal sealed class HeroTile : UiControl
{
    public string Glyph = "";
    public bool Live;
    public HeroTile() { SetStyle(ControlStyles.Selectable, false); TabStop = false; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Quality(g);
        Theme.Fill(g, new RectangleF(0, 0, Width, Height), 16 * Theme.K(this), Live ? Theme.Accent : Theme.SurfaceHi);
        Theme.Draw(g, Glyph, Theme.Icons(22f), ClientRectangle, Live ? Theme.OnAccent : Theme.Accent, TextFormatFlags.HorizontalCenter);
    }
}
