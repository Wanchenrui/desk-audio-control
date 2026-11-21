using System;
using System.Drawing;
using System.Windows.Forms;

// Base of the custom controls: transparent, double buffered, hover and focus aware.
internal class UiControl : Control
{
    protected bool Hot, Pressed;
    protected UiControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }
    protected bool Ringed { get { return Focused && ShowFocusCues; } }
    protected override void OnMouseEnter(EventArgs e) { Hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
}

// Plain text drawn on whatever surface is behind it.
internal sealed class Caption : UiControl
{
    Tone tone = Tone.Ink;
    public float Pt = 9f;
    public bool Bold, Wrap, TopAligned;
    public ContentAlignment Align = ContentAlignment.MiddleLeft;
    public Tone Role { get { return tone; } set { if (tone != value) { tone = value; Invalidate(); } } }
    public Caption() { SetStyle(ControlStyles.Selectable, false); TabStop = false; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var flags = Align == ContentAlignment.MiddleRight ? TextFormatFlags.Right :
                    Align == ContentAlignment.MiddleCenter ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left;
        if (Wrap) flags |= TextFormatFlags.WordBreak;
        Theme.Draw(e.Graphics, Text, Theme.Face(Pt, Bold ? FontStyle.Bold : FontStyle.Regular), ClientRectangle,
                   Enabled ? Theme.Pick(tone) : Theme.Muted, flags, !TopAligned);
    }
}

// Rounded surface that hosts other controls. Selected tints it with the accent color.
internal class Card : UiControl
{
    bool selected;
    public float Radius = 14f;
    public bool Selected { get { return selected; } set { if (selected != value) { selected = value; Invalidate(true); } } }
    public Card() { SetStyle(ControlStyles.Selectable, false); TabStop = false; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Quality(g); float k = Theme.K(this);
        var r = new RectangleF(0.5f * k, 0.5f * k, Width - k, Height - k);
        Theme.Fill(g, r, Radius * k, selected ? Theme.AccentSoft : Theme.Surface);
        Theme.Outline(g, r, Radius * k, selected ? Theme.Accent : Theme.Border, (selected ? 1.6f : 1f) * k);
    }
}

// Small status pill. Call Fit() after changing Text to size it.
internal sealed class Chip : UiControl
{
    Tone tone = Tone.Muted;
    public bool Dot;
    public Tone Role { get { return tone; } set { if (tone != value) { tone = value; Invalidate(); } } }
    public Chip() { SetStyle(ControlStyles.Selectable, false); TabStop = false; }
    public void Fit()
    {
        float k = Theme.K(this);
        Width = Theme.Measure(Text, Theme.Face(8.5f)) + (int)((Dot ? 32 : 22) * k);
        Height = (int)(22 * k);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Quality(g); float k = Theme.K(this);
        Color c = Theme.Pick(tone);
        Theme.Fill(g, new RectangleF(0, 0, Width, Height), Height / 2f, Theme.Mix(Theme.Surface, c, Theme.Dark ? 0.3f : 0.14f));
        int pad = (int)((Dot ? 22 : 11) * k);
        if (Dot) using (var b = new SolidBrush(c)) g.FillEllipse(b, 11 * k, (Height - 7 * k) / 2f, 7 * k, 7 * k);
        Theme.Draw(g, Text, Theme.Face(8.5f), new Rectangle(pad, 0, Math.Max(0, Width - pad - (int)(9 * k)), Height), c, TextFormatFlags.Left);
    }
}
