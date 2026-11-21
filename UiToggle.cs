using System;
using System.Drawing;
using System.Windows.Forms;

// Switch with a text label. Checked raises CheckedChanged; Set updates the state silently.
internal sealed class ToggleSwitch : UiControl
{
    bool on;
    float pos;
    readonly Timer anim = new Timer();
    public event EventHandler CheckedChanged;

    public ToggleSwitch()
    {
        Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.CheckButton; anim.Interval = 15;
        anim.Tick += delegate {
            float goal = on ? 1f : 0f;
            pos += (goal - pos) * 0.45f;
            if (Math.Abs(goal - pos) < 0.02f) { pos = goal; anim.Stop(); }
            Invalidate();
        };
    }
    public bool Checked
    {
        get { return on; }
        set {
            if (on == value) return;
            on = value; anim.Start(); Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            EventHandler h = CheckedChanged; if (h != null) h(this, EventArgs.Empty);
        }
    }
    public void Set(bool value) { on = value; pos = value ? 1f : 0f; anim.Stop(); Invalidate(); }

    sealed class Peer : Control.ControlAccessibleObject
    {
        public Peer(ToggleSwitch owner) : base(owner) { }
        public override AccessibleStates State
        {
            get { AccessibleStates s = base.State; return ((ToggleSwitch)Owner).on ? s | AccessibleStates.Checked : s; }
        }
    }
    protected override AccessibleObject CreateAccessibilityInstance() { return new Peer(this); }
    protected override void OnClick(EventArgs e) { if (Enabled) Checked = !on; base.OnClick(e); }
    protected override bool IsInputKey(Keys key) { return key == Keys.Space || base.IsInputKey(key); }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { e.Handled = true; OnClick(EventArgs.Empty); }
        base.OnKeyUp(e);
    }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; base.OnEnabledChanged(e); }
    protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Quality(g); float k = Theme.K(this);
        float w = 38f * k, h = 22f * k, y = (Height - h) / 2f;
        var track = new RectangleF(k, y, w, h);
        Color off = Hot ? Theme.Mix(Theme.Track, Theme.Ink, 0.12f) : Theme.Track;
        Color back = Theme.Mix(off, Theme.Accent, pos);
        if (!Enabled) back = Theme.Mix(back, Theme.Surface, 0.55f);
        Theme.Fill(g, track, h / 2f, back);
        float d = 16f * k, x = track.X + 3f * k + (w - d - 6f * k) * pos;
        Color thumb = Theme.Mix(Theme.Dark ? Theme.Muted : Theme.Surface, Theme.OnAccent, pos);
        using (var b = new SolidBrush(thumb)) g.FillEllipse(b, x, y + (h - d) / 2f, d, d);
        int left = (int)(w + 14f * k);
        if (!String.IsNullOrEmpty(Text))
            Theme.Draw(g, Text, Theme.Face(9f), new Rectangle(left, 0, Math.Max(0, Width - left), Height),
                       Enabled ? Theme.Ink : Theme.Muted, TextFormatFlags.Left | TextFormatFlags.WordBreak);
        if (Ringed) Theme.Ring(g, new RectangleF(0, y - k, w + 2f * k, h + 2f * k), h / 2f + k, k);
    }
}
