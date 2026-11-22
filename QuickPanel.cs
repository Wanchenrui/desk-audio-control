using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

// Small pop-up opened from the tray icon: switch output, change its volume, mute, or open the full window.
internal sealed class QuickPanel : Form
{
    static int closedAt;
    bool done;
    // The tray click that dismisses the panel arrives right after it deactivates; callers use this to avoid reopening it.
    public static bool JustClosed { get { return unchecked(Environment.TickCount - closedAt) < 400; } }

    int P(float v) { return Theme.Px(this, v); }

    public QuickPanel(IList<AudioItem> outputs, Predicate<AudioItem> isCurrent, int volume, bool muted,
        Action<AudioItem> pick, Action<int> setLevel, Action toggleMute, Action openWindow)
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; KeyPreview = true;
        StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true; BackColor = Theme.Surface; Font = Theme.Face(9f); AccessibleName = "音频快捷面板";
        int pad = P(16), w = P(320), y = P(14), row = P(36), gap = P(6), inner = w - 2 * pad;
        var title = new Caption { Text = "输出设备", Pt = 10f, Bold = true };
        title.SetBounds(pad, y, inner, P(24)); Controls.Add(title); y += P(30);
        if (outputs.Count == 0)
        {
            var none = new Caption { Text = "没有检测到可切换的播放设备", Pt = 9f, Role = Tone.Muted };
            none.SetBounds(pad, y, inner, P(28)); Controls.Add(none); y += P(34);
        }
        foreach (var item in outputs)
        {
            var target = item;
            var button = new PillButton { Text = item.Name, Kind = ButtonLook.Soft, On = isCurrent(item), AccessibleName = "切换到 " + item.Name };
            button.SetBounds(pad, y, inner, row);
            button.Click += delegate { Dismiss(); pick(target); };
            Controls.Add(button); y += row + gap;
        }
        y += P(8);
        var slider = new FlatSlider { AccessibleName = "输出音量" };
        slider.SetBounds(pad - P(10), y, inner - P(46), P(32)); slider.Set(volume);
        var level = new Caption { Text = volume + " %", Pt = 10f, Bold = true, Align = ContentAlignment.MiddleRight };
        level.SetBounds(w - pad - P(56), y, P(56), P(32));
        slider.ValueChanged += delegate { level.Text = slider.Value + " %"; setLevel(slider.Value); };
        Controls.Add(slider); Controls.Add(level); y += P(32) + gap;
        var mute = new PillButton { Text = muted ? "已静音" : "静音", On = muted, Kind = ButtonLook.Soft, AccessibleName = "输出静音" };
        mute.SetBounds(pad, y, P(96), row);
        mute.Click += delegate { muted = !muted; mute.Text = muted ? "已静音" : "静音"; mute.On = muted; toggleMute(); };
        var open = new PillButton { Text = "打开完整面板", Kind = ButtonLook.Ghost, AccessibleName = "打开完整面板" };
        open.SetBounds(pad + P(104), y, inner - P(104), row);
        open.Click += delegate { Dismiss(); openWindow(); };
        Controls.Add(mute); Controls.Add(open); y += row + P(14);
        ClientSize = new Size(w, y);
        using (var shape = Theme.Round(new RectangleF(0, 0, w, y), P(16))) Region = new Region(shape);
    }

    void Dismiss() { if (done) return; done = true; Close(); }

    // Opens in the bottom-right corner of the screen the pointer is on, next to the tray.
    public void Display()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(Math.Max(area.Left, area.Right - Width - P(12)), Math.Max(area.Top, area.Bottom - Height - P(12)));
        Show(); Activate();
    }

    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Dismiss(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Dismiss(); e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnFormClosed(FormClosedEventArgs e) { closedAt = Environment.TickCount; base.OnFormClosed(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Theme.Quality(e.Graphics);
        Theme.Outline(e.Graphics, new RectangleF(1, 1, Width - 2, Height - 2), P(16), Theme.Border, 1f);
    }
}
