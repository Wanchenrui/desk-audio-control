using System;
using System.Drawing;
using System.Windows.Forms;

internal enum ButtonLook { Soft, Solid, Ghost }

// Capsule button. On marks a toggled or current state; Glyph is an optional Segoe MDL2 icon.
internal sealed class PillButton : UiControl
{
    public ButtonLook Kind = ButtonLook.Soft;
    public string Glyph;
    bool on;
    public bool On { get { return on; } set { if (on != value) { on = value; Invalidate(); } } }
    public PillButton() { Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.PushButton; }
    protected override bool IsInputKey(Keys key) { return key == Keys.Enter || key == Keys.Space || base.IsInputKey(key); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; base.OnEnabledChanged(e); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { Pressed = true; Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e) { Pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Pressed = true; Invalidate(); e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (Pressed && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)) { Pressed = false; Invalidate(); e.Handled = true; if (Enabled) OnClick(EventArgs.Empty); }
        base.OnKeyUp(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Quality(g); float k = Theme.K(this);
        float t = Enabled ? (Pressed ? 0.24f : (Hot ? 0.12f : 0f)) : 0f;
        Color fill = Color.Empty, ink = Theme.Ink, line = Color.Empty;
        if (on) { fill = Theme.Mix(Theme.AccentSoft, Theme.Accent, t); ink = Theme.Accent; line = Theme.Accent; }
        else if (Kind == ButtonLook.Solid) { fill = Theme.Mix(Theme.Accent, Theme.Ink, t); ink = Theme.OnAccent; }
        else if (Kind == ButtonLook.Soft) fill = Theme.Mix(Theme.SurfaceHi, Theme.Accent, t);
        else { line = Theme.Border; if (t > 0) fill = Theme.Mix(Theme.Surface, Theme.Accent, t); }
        if (!Enabled) { ink = Theme.Mix(ink, Theme.Muted, 0.6f); if (!fill.IsEmpty) fill = Theme.Mix(fill, Theme.Surface, 0.5f); }
        var r = new RectangleF(0.5f * k, 0.5f * k, Width - k, Height - k);
        float rad = Math.Min(r.Height / 2f, 20f * k);
        if (!fill.IsEmpty) Theme.Fill(g, r, rad, fill);
        if (!line.IsEmpty) Theme.Outline(g, r, rad, line, k);
        Font f = Theme.Face(9f, on || Kind == ButtonLook.Solid ? FontStyle.Bold : FontStyle.Regular);
        var box = new Rectangle((int)(10 * k), 0, Math.Max(0, Width - (int)(20 * k)), Height);
        if (String.IsNullOrEmpty(Glyph)) Theme.Draw(g, Text, f, box, ink, TextFormatFlags.HorizontalCenter);
        else
        {
            Font gf = Theme.Icons(10f);
            int gw = Theme.Measure(Glyph, gf), tw = Theme.Measure(Text, f), gap = (int)(6 * k);
            int x = Math.Max(box.X, (Width - (gw + gap + tw)) / 2);
            Theme.Draw(g, Glyph, gf, new Rectangle(x, 0, gw + 2, Height), ink, TextFormatFlags.Left);
            Theme.Draw(g, Text, f, new Rectangle(x + gw + gap, 0, Math.Max(0, box.Right - x - gw - gap), Height), ink, TextFormatFlags.Left);
        }
        if (Ringed) Theme.Ring(g, new RectangleF(0, 0, Width, Height), rad + k, k);
    }
}
