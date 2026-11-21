using System;
using System.Drawing;
using System.Windows.Forms;

// Volume slider. Dragging is true while the pointer holds the thumb; Set updates the value silently.
internal sealed class FlatSlider : UiControl
{
    int val;
    bool drag;
    public int Minimum = 0, Maximum = 100;
    public bool FromCenter;
    public event EventHandler ValueChanged;
    public bool Dragging { get { return drag; } }
    public int Value { get { return val; } }
    public FlatSlider() { Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.Slider; }
    public void Set(int v) { Change(v, false); }

    void Change(int v, bool raise)
    {
        v = Math.Max(Minimum, Math.Min(Maximum, v));
        if (v == val) return;
        val = v; Invalidate();
        if (!raise) return;
        AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
        EventHandler h = ValueChanged; if (h != null) h(this, EventArgs.Empty);
    }
    int FromX(int x)
    {
        float k = Theme.K(this), pad = 10f * k, span = Math.Max(1f, Width - 2f * pad);
        return Minimum + (int)Math.Round((Maximum - Minimum) * Math.Max(0f, Math.Min(1f, (x - pad) / span)));
    }

    sealed class Peer : Control.ControlAccessibleObject
    {
        public Peer(FlatSlider owner) : base(owner) { }
        public override string Value { get { return ((FlatSlider)Owner).val + " %"; } }
    }
    protected override AccessibleObject CreateAccessibilityInstance() { return new Peer(this); }
    protected override bool IsInputKey(Keys key)
    {
        switch (key) {
            case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
            case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown: return true;
        }
        return base.IsInputKey(key);
    }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; base.OnEnabledChanged(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        drag = true; Change(FromX(e.X), true); Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (drag) Change(FromX(e.X), true); }
    protected override void OnMouseUp(MouseEventArgs e) { drag = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!Focused) { base.OnMouseWheel(e); return; }
        Change(val + Math.Sign(e.Delta) * 2, true);
        var handled = e as HandledMouseEventArgs; if (handled != null) handled.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int step = 0;
        switch (e.KeyCode) {
            case Keys.Left: case Keys.Down: step = -2; break;
            case Keys.Right: case Keys.Up: step = 2; break;
            case Keys.PageDown: step = -10; break;
            case Keys.PageUp: step = 10; break;
            case Keys.Home: Change(Minimum, true); e.Handled = true; break;
            case Keys.End: Change(Maximum, true); e.Handled = true; break;
        }
        if (step != 0) { Change(val + step, true); e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Quality(g); float k = Theme.K(this);
        float pad = 10f * k, cy = Height / 2f, th = 6f * k, span = Width - 2f * pad;
        float frac = Maximum == Minimum ? 0f : (val - Minimum) / (float)(Maximum - Minimum);
        Color tint = Enabled ? Theme.Accent : Theme.Mix(Theme.Accent, Theme.Surface, 0.65f);
        Theme.Fill(g, new RectangleF(pad, cy - th / 2f, span, th), th / 2f, Theme.Track);
        float from = FromCenter ? 0.5f : 0f, lo = Math.Min(from, frac), hi = Math.Max(from, frac);
        if (hi > lo) Theme.Fill(g, new RectangleF(pad + span * lo, cy - th / 2f, span * (hi - lo), th), th / 2f, tint);
        float d = (drag ? 18f : (Hot ? 17f : 15f)) * k, x = pad + span * frac;
        using (var b = new SolidBrush(Theme.Surface)) g.FillEllipse(b, x - d / 2f, cy - d / 2f, d, d);
        using (var pen = new Pen(tint, 2f * k)) g.DrawEllipse(pen, x - d / 2f + k, cy - d / 2f + k, d - 2f * k, d - 2f * k);
        if (Ringed) Theme.Ring(g, new RectangleF(0, 0, Width, Height), 8f * k, k);
    }
}

// Segmented peak meter. Feed a 0..1 level on every refresh; it smooths decay and holds the peak cell.
internal sealed class LevelMeter : UiControl
{
    float level, hold;
    public int Cells = 28;
    public LevelMeter() { SetStyle(ControlStyles.Selectable, false); TabStop = false; }
    public void Feed(float v)
    {
        v = Math.Max(0f, Math.Min(1f, v));
        level = v > level ? v : level - (level - v) * 0.3f;
        hold = v >= hold ? v : Math.Max(v, hold - 0.03f);
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Quality(g); float k = Theme.K(this);
        int n = Cells;
        float gap = 2f * k, cw = (Width - gap * (n - 1)) / n, h = Math.Min(Height, 8f * k), y = (Height - h) / 2f;
        int top = Math.Min(n - 1, (int)(hold * n));
        for (int i = 0; i < n; i++)
        {
            float pos = (i + 0.5f) / n;
            bool lit = Enabled && pos <= level, peak = Enabled && hold > 0.03f && i == top;
            Theme.Fill(g, new RectangleF(i * (cw + gap), y, cw, h), 1.5f * k, lit || peak ? Theme.Meter(pos) : Theme.Track);
        }
    }
}
