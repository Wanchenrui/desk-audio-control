using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

// Settings page, the update page host, and the small control helpers shared by the pages.
internal sealed partial class AudioPanel
{
    readonly Card[] block = new Card[3];
    readonly Caption[] heading = new Caption[3];
    Caption settingsTitle, keyOutLabel, keyMicLabel, keyUndoLabel, identifyBody;
    Chip keyOut, keyMic, keyUndo;
    ToggleSwitch autoRoute, startup;
    PillButton soundApps, soundPanel, restore, decisionsButton;
    Caption sceneStatus, sensitivityLabel;
    FlatSlider sensitivitySlider;
    PillButton autonomyButton, offerButton;
    readonly string[] autonomyLabels = { "自适应（初始 L2）","L0 只提示","L1 一键执行","L2 切换并提示撤销","L3 静默切换" };

    void RefreshSmartSettings()
    {
        sceneStatus.Text = "当前场景 · " + (scenes.Current ?? "识别中");
        autonomyButton.Text = autonomyLabels[autonomy + 1];
        sensitivityLabel.Text = "保守 → 积极   " + sensitivity + "%";
        offerButton.Enabled = pendingRoute != null && clock.ElapsedMilliseconds < offerUntil;
        offerButton.Text = offerButton.Enabled ? "执行建议 · " + pendingRoute.Evidence.Winner().Name : "暂无待执行建议";
    }

    Caption Cap(Control parent, string text, float pt, bool bold, Tone tone)
    {
        var c = new Caption { Text = text, Pt = pt, Bold = bold, Role = tone };
        parent.Controls.Add(c);
        return c;
    }
    PillButton Pill(Control parent, string text, ButtonLook look, string name, Action fn)
    {
        var b = new PillButton { Text = text, Kind = look, AccessibleName = name };
        b.Click += delegate { Act(fn); };
        parent.Controls.Add(b);
        return b;
    }
    ToggleSwitch Switch(Control parent, string text, bool on, Action<bool> changed)
    {
        var s = new ToggleSwitch { Text = text, AccessibleName = text };
        s.Set(on);
        s.CheckedChanged += delegate { changed(s.Checked); };
        parent.Controls.Add(s);
        return s;
    }

    void BuildSettings(Control page)
    {
        ((Panel)page).AutoScroll = true;
        settingsTitle = Cap(page, "切换与设置", 14f, true, Tone.Ink);
        string[] titles = { "快捷键", "智能切换", "设备识别" };
        for (int i = 0; i < block.Length; i++)
        {
            block[i] = new Card();
            page.Controls.Add(block[i]);
            heading[i] = Cap(block[i], titles[i], 10.5f, true, Tone.Ink);
        }
        keyOutLabel = Cap(block[0], "切换默认输出", 9.5f, false, Tone.Ink);
        keyMicLabel = Cap(block[0], "默认话筒静音 / 取消静音", 9.5f, false, Tone.Ink);
        keyUndoLabel = Cap(block[0], "撤销最近自动切换", 9.5f, false, Tone.Ink);
        keyUndo = new Chip { Dot = true };
        block[0].Controls.Add(keyUndo);
        keyOut = new Chip { Dot = true };
        keyMic = new Chip { Dot = true };
        block[0].Controls.Add(keyOut);
        block[0].Controls.Add(keyMic);
        autoRoute = Switch(block[1], "根据设备接入与场景变化推荐或切换", preferNew, on =>
        {
            try { LocalSettings.Put("PreferNewOutput", on ? "1" : "0"); preferNew = on; pendingRoute = null; RefreshSmartSettings(); }
            catch (Exception e) { autoRoute.Set(preferNew); Fail(e.Message); }
        });
        sceneStatus = Cap(block[1],"",9.5f,true,Tone.Ink);
        autonomyButton = Pill(block[1],"",ButtonLook.Soft,"选择自治级别",delegate {
            ThemedMenu.Choose(autonomyButton,autonomyLabels,autonomy + 1,n => Act(delegate {
                LocalSettings.Put("RouteAutonomy",(n - 1).ToString()); autonomy = n - 1; pendingRoute = null; RefreshSmartSettings();
            },false));
        });
        sensitivityLabel = Cap(block[1],"",9f,false,Tone.Muted);
        sensitivitySlider = new FlatSlider { AccessibleName = "切换积极度" };
        sensitivitySlider.Set(sensitivity);
        sensitivitySlider.ValueChanged += delegate {
            try { LocalSettings.Put("RouteSensitivity",sensitivitySlider.Value.ToString()); sensitivity = sensitivitySlider.Value; pendingRoute = null; RefreshSmartSettings(); }
            catch (Exception e) { sensitivitySlider.Set(sensitivity); Fail(e.Message); }
        };
        block[1].Controls.Add(sensitivitySlider);
        offerButton = Pill(block[1],"",ButtonLook.Soft,"执行切换建议",AcceptOffer);
        RefreshSmartSettings();
        startup = Switch(block[1], "登录 Windows 后启用托盘和快捷键", LocalSettings.Startup, on => Act(() => LocalSettings.Startup = on, false));
        identifyBody = Cap(block[2], "新接入的 USB 和蓝牙音频设备会自动出现。双向声卡会列出播放和录音；可在设备页选择实际用途，同一设备下次接入时会沿用。\n板载、显示器和软件线路可在设备页打开“显示其他接口”查看，选择“加入日常设备”即可纳入快捷切换。", 9f, false, Tone.Muted);
        identifyBody.Wrap = true; identifyBody.TopAligned = true;
        soundApps = Pill(page, "应用音量和输出", ButtonLook.Soft, "应用音量和输出", () => Process.Start("ms-settings:apps-volume"));
        soundPanel = Pill(page, "Windows 声音设置", ButtonLook.Soft, "Windows 声音设置", () => Process.Start("ms-settings:sound"));
        decisionsButton = Pill(page, "活动时间线与撤销…", ButtonLook.Soft, "活动时间线与撤销", ShowDecisions);
        if (!String.IsNullOrEmpty(LocalSettings.Get("BeforeInput")))
            restore = Pill(page, "恢复原默认话筒并退出…", ButtonLook.Ghost, "恢复设置", Restore);
    }

    void UpdateShortcuts()
    {
        keyOut.Text = "Ctrl + Alt + F8" + (hotOut ? "" : " · 被占用");
        keyOut.Role = hotOut ? Tone.Good : Tone.Warn;
        keyMic.Text = "Ctrl + Alt + F7" + (hotMic ? "" : " · 被占用");
        keyMic.Role = hotMic ? Tone.Good : Tone.Warn;
        keyUndo.Text = "Ctrl + Alt + F9" + (hotUndo ? "" : " · 被占用");
        keyUndo.Role = hotUndo ? Tone.Good : Tone.Warn;
        hint = "切换输出  Ctrl + Alt + F8      话筒静音  Ctrl + Alt + F7      撤销  Ctrl + Alt + F9";
    }

    void LayoutSettings(Control page)
    {
        int pad = P(28), m = P(18), gap = P(12), cw = Math.Max(0, page.Width - 2 * pad), iw = Math.Max(0, cw - 2 * m);
        settingsTitle.SetBounds(pad, P(20), cw, P(34));
        int bodyH = Theme.MeasureWrapped(identifyBody.Text, Theme.Face(9f), iw) + P(2);
        int[] heights = { P(144), P(204), P(42) + bodyH + P(18) };
        int y = P(68);
        for (int i = 0; i < block.Length; i++)
        {
            block[i].SetBounds(pad, y, cw, heights[i]);
            heading[i].SetBounds(m, P(10), iw, P(28));
            y += heights[i] + gap;
        }
        keyOut.Fit(); keyMic.Fit(); keyUndo.Fit();
        keyUndoLabel.SetBounds(m, P(108), P(210), P(28));
        keyUndo.Location = new Point(m + P(220), P(108) + (P(28) - keyUndo.Height) / 2);
        keyOutLabel.SetBounds(m, P(44), P(210), P(28));
        keyMicLabel.SetBounds(m, P(76), P(210), P(28));
        keyOut.Location = new Point(m + P(220), P(44) + (P(28) - keyOut.Height) / 2);
        keyMic.Location = new Point(m + P(220), P(76) + (P(28) - keyMic.Height) / 2);
        autoRoute.SetBounds(m, P(44), iw, P(32));
        sceneStatus.SetBounds(m + P(112), P(10), iw - P(112), P(28));
        autonomyButton.SetBounds(m,P(82),P(170),P(32));
        offerButton.SetBounds(m + P(182),P(82),iw - P(182),P(32));
        sensitivityLabel.SetBounds(m,P(120),P(158),P(32));
        sensitivitySlider.SetBounds(m + P(164),P(120),iw - P(164),P(32));
        startup.SetBounds(m, P(160), iw, P(32));
        identifyBody.SetBounds(m, P(42), iw, bodyH);
        soundApps.SetBounds(pad, y + P(4), P(156), P(34));
        soundPanel.SetBounds(pad + P(166), y + P(4), P(166), P(34));
        decisionsButton.SetBounds(pad + P(342), y + P(4), P(162), P(34));
        if (restore != null) restore.SetBounds(pad, y + P(44), P(230), P(32));
    }

    void BuildUpdates(Control page)
    {
        updates = new UpdatePage(this, tag =>
        {
            rail.Badge(2, true);
            tray.ShowBalloonTip(5000, "桌面音频有新版本", tag + " 已发布，可在更新页安装并重启。", ToolTipIcon.Info);
        }, delegate { closing = true; Close(); });
        updates.Dock = DockStyle.Fill;
        page.Controls.Add(updates);
    }
}
