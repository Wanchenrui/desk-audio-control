using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

// Commands: routing, mute, hot key actions, the native effects page, balance and restore.
internal sealed partial class AudioPanel
{
    void Cycle()
    {
        if (!hasSnapshot) { cycleOnReady = true; bus.Publish(ScanReason.Refresh); return; }
        defaultOut = AudioCore.Default(0,1);
        var options = OutputCycle();
        if (options.Count == 0) throw new InvalidOperationException("当前没有可切换的日常播放设备。");
        var current = groups.FirstOrDefault(g => ContainsId(g.Outputs, defaultOut));
        int idx = current == null ? -1 : options.FindIndex(p => ContainsId(current.Outputs, p.Id));
        SetDefault(options[(idx + 1) % options.Count]);
    }
    void SetDefault(AudioItem p)
    {
        if (p == null) return;
        AudioCore.Route(p.Id, p.Flow);
        actionRevision++;
        quietUntil = clock.ElapsedMilliseconds + 30000;
        if (p.Flow == 0)
        {
            defaultOut = p.Id;
            RecordManual(p);
        }
        else defaultIn = p.Id;
        ShowRoute(p.Name,p.Id,false);
    }
    void Toggle(AudioItem p)
    {
        if (p == null) return;
        bool b = !AudioCore.Muted(p.Id);
        quietUntil = clock.ElapsedMilliseconds + 30000;
        AudioCore.SetMute(p.Id, b);
        Notify(p.Name + (b ? " 已静音" : " 已取消静音"));
    }
    void ToggleDefaultMic()
    {
        string id = AudioCore.Default(1, 1);
        if (id == null) throw new InvalidOperationException("未找到默认录音设备。");
        Toggle(new AudioItem { Id = id, Flow = 1, Name = "默认话筒" });
    }

    // Result messages show in the status line while the window is open, otherwise as a tray balloon.
    void Notify(string s)
    {
        if (!Visible) { tray.ShowBalloonTip(1000, "桌面音频", s, ToolTipIcon.Info); return; }
        SetStatus(s, Tone.Good); statusUntil = clock.ElapsedMilliseconds + 4000;
    }
    void Fail(string message)
    {
        if (Visible) { SetStatus(message, Tone.Bad); statusUntil = clock.ElapsedMilliseconds + 8000; }
        else tray.ShowBalloonTip(2000, "桌面音频", message, ToolTipIcon.Warning);
    }
    void Act(Action fn, bool refresh = true)
    {
        try { fn(); if (refresh && !IsDisposed) bus.Publish(ScanReason.Refresh); }
        catch (Exception e) { Fail(e.Message); }
    }

    void Enhancements()
    {
        var p = Chosen(0); if (p == null) return;
        Rectangle area = Screen.FromHandle(Handle).WorkingArea;
        var thread = new Thread(() =>
        {
            try { AudioTuning.Open(p.Id, p.Bus, area); }
            catch (Exception e) { if (!IsDisposed) BeginInvoke((Action)(() => Fail(e.Message))); }
        });
        thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    void AdjustBalance()
    {
        var p = Chosen(0); if (p == null) return;
        int old = AudioCore.Balance(p.Id);
        using (var dialog = new BalanceDialog(p.Name, old))
            if (dialog.ShowDialog(this) == DialogResult.OK) { quietUntil = clock.ElapsedMilliseconds + 30000; AudioCore.SetBalance(p.Id, dialog.Value); }
    }
    void Restore()
    {
        if (MessageBox.Show(this, "恢复安装前的默认输入，并关闭桌面音频的接入自动切换、登录启动和快捷键？", "恢复设置", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        string id = LocalSettings.Get("BeforeInput");
        if (String.IsNullOrEmpty(id)) throw new InvalidOperationException("尚未记录安装前的默认输入。");
        AudioCore.Route(id, 1); LocalSettings.Put("PreferNewOutput", "0"); LocalSettings.Startup = false; closing = true; Close();
    }

    protected override void WndProc(ref Message msg)
    {
        if (msg.Msg == 0x0312)
        {
            if (msg.WParam.ToInt32() == 1) Act(Cycle);
            if (msg.WParam.ToInt32() == 2) Act(ToggleDefaultMic);
            if (msg.WParam.ToInt32() == 3) Act(() => UndoRoute("热键"));
        }
        base.WndProc(ref msg);
    }
}

internal sealed class BalanceDialog : Form
{
    readonly FlatSlider slider = new FlatSlider();
    readonly Caption reading = new Caption();
    public int Value { get { return slider.Value; } }

    public BalanceDialog(string device, int current)
    {
        Text = device + " · 左右平衡"; Font = Theme.Face(9f); BackColor = Theme.Window; ForeColor = Theme.Ink;
        AutoScaleMode = AutoScaleMode.None; FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        float k = Theme.K(this);
        ClientSize = new Size((int)(440 * k), (int)(196 * k));
        int pad = (int)(24 * k), w = ClientSize.Width - 2 * pad, ten = (int)(10 * k), row = (int)(142 * k), bh = (int)(34 * k);
        reading.Pt = 12f; reading.Bold = true; reading.SetBounds(pad, (int)(18 * k), w, (int)(32 * k));
        slider.Minimum = -100; slider.Maximum = 100; slider.FromCenter = true; slider.AccessibleName = "左右平衡";
        slider.SetBounds(pad - ten, (int)(58 * k), w + 2 * ten, (int)(36 * k)); slider.Set(current);
        var left = new Caption { Text = "左", Role = Tone.Muted }; left.SetBounds(pad, (int)(94 * k), (int)(40 * k), (int)(24 * k));
        var right = new Caption { Text = "右", Role = Tone.Muted, Align = ContentAlignment.MiddleRight };
        right.SetBounds(pad + w - (int)(40 * k), (int)(94 * k), (int)(40 * k), (int)(24 * k));
        var center = new PillButton { Text = "居中", Kind = ButtonLook.Soft }; center.SetBounds(pad, row, (int)(90 * k), bh);
        var apply = new PillButton { Text = "应用", Kind = ButtonLook.Solid }; apply.SetBounds(pad + w - (int)(96 * k), row, (int)(96 * k), bh);
        var cancel = new PillButton { Text = "取消", Kind = ButtonLook.Ghost }; cancel.SetBounds(apply.Left - (int)(104 * k), row, (int)(96 * k), bh);
        slider.ValueChanged += delegate { Read(); };
        center.Click += delegate { slider.Set(0); Read(); };
        apply.Click += delegate { DialogResult = DialogResult.OK; };
        cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
        Controls.AddRange(new Control[] { reading, slider, left, right, center, cancel, apply });
        Read();
        Shown += delegate { slider.Focus(); };
    }
    void Read()
    {
        int v = slider.Value;
        reading.Text = v == 0 ? "居中" : (v < 0 ? "偏左  " : "偏右  ") + Math.Abs(v) + " %";
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
        if (keyData == Keys.Enter && !(ActiveControl is PillButton)) { DialogResult = DialogResult.OK; return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
