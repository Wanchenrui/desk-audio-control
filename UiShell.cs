using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// Left rail: brand, page navigation and a live summary of the Windows defaults.
internal sealed class NavRail : UiControl
{
    sealed class Entry { public string Glyph, Text; public bool Badge; }
    readonly List<Entry> entries = new List<Entry>();
    int index, hot = -1;
    string outName = "", inName = "";
    public string Version = "";
    public event EventHandler IndexChanged;

    public NavRail() { AccessibleRole = AccessibleRole.PageTabList; AccessibleName = "导航"; }
    public void Add(string glyph, string text) { entries.Add(new Entry { Glyph = glyph, Text = text }); Invalidate(); }
    public int Index
    {
        get { return index; }
        set
        {
            if (value < 0 || value >= entries.Count || value == index) return;
            index = value; Invalidate();
            EventHandler h = IndexChanged; if (h != null) h(this, EventArgs.Empty);
        }
    }
    public void Badge(int i, bool on)
    {
        if (i >= 0 && i < entries.Count && entries[i].Badge != on) { entries[i].Badge = on; Invalidate(); }
    }
    public void Defaults(string output, string input)
    {
        if (outName == output && inName == input) return;
        outName = output; inName = input; Invalidate();
    }

    Rectangle Slot(int i)
    {
        float k = Theme.K(this);
        return new Rectangle((int)(12 * k), (int)((96 + i * 48) * k), Width - (int)(24 * k), (int)(44 * k));
    }
    int HitTest(Point p)
    {
        for (int i = 0; i < entries.Count; i++) if (Slot(i).Contains(p)) return i;
        return -1;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = HitTest(e.Location);
        if (i != hot) { hot = i; Cursor = i < 0 ? Cursors.Default : Cursors.Hand; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hot != -1) { hot = -1; Invalidate(); }
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int i = HitTest(e.Location);
        if (e.Button == MouseButtons.Left && i >= 0) { Focus(); Index = i; }
    }
    protected override bool IsInputKey(Keys key) { return key == Keys.Up || key == Keys.Down || base.IsInputKey(key); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Up) { Index = index - 1; e.Handled = true; }
        else if (e.KeyCode == Keys.Down) { Index = index + 1; e.Handled = true; }
        base.OnKeyDown(e);
    }

    void Footer(Graphics g, string label, string name, int y, float k)
    {
        int x = (int)(22 * k), w = Width - (int)(44 * k);
        Theme.Draw(g, label, Theme.Face(8f), new Rectangle(x, y, w, (int)(16 * k)), Theme.Muted, TextFormatFlags.Left);
        Theme.Draw(g, String.IsNullOrEmpty(name) ? "无" : name, Theme.Face(9.5f), new Rectangle(x, y + (int)(17 * k), w, (int)(22 * k)), Theme.Ink, TextFormatFlags.Left);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Quality(g); float k = Theme.K(this);
        g.Clear(Theme.Surface);
        using (var line = new Pen(Theme.Border, k)) g.DrawLine(line, Width - k, 0, Width - k, Height);

        float d = 36 * k, bx = 22 * k, by = 26 * k, cx = bx + d / 2, cy = by + d / 2;
        using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, bx, by, d, d);
        using (var bar = new Pen(Theme.OnAccent, 2.6f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(bar, cx - 7 * k, cy - 3.5f * k, cx - 7 * k, cy + 3.5f * k);
            g.DrawLine(bar, cx, cy - 8 * k, cx, cy + 8 * k);
            g.DrawLine(bar, cx + 7 * k, cy - 5 * k, cx + 7 * k, cy + 5 * k);
        }
        int tx = (int)(70 * k), tw = Width - (int)(82 * k);
        Theme.Draw(g, "桌面音频", Theme.Face(12f, FontStyle.Bold), new Rectangle(tx, (int)(24 * k), tw, (int)(24 * k)), Theme.Ink, TextFormatFlags.Left);
        Theme.Draw(g, "DeskAudio" + (Version.Length > 0 ? " · v" + Version : ""), Theme.Face(8f), new Rectangle(tx, (int)(48 * k), tw, (int)(18 * k)), Theme.Muted, TextFormatFlags.Left);

        for (int i = 0; i < entries.Count; i++)
        {
            var s = Slot(i); bool on = i == index;
            if (on || i == hot) Theme.Fill(g, s, 12 * k, on ? Theme.AccentSoft : Theme.SurfaceHi);
            if (on) Theme.Fill(g, new RectangleF(s.X + 3 * k, s.Y + (s.Height - 18 * k) / 2f, 3 * k, 18 * k), 1.5f * k, Theme.Accent);
            Theme.Draw(g, entries[i].Glyph, Theme.Icons(12f), new Rectangle(s.X + (int)(16 * k), s.Y, (int)(24 * k), s.Height), on ? Theme.Accent : Theme.Muted, TextFormatFlags.Left);
            Theme.Draw(g, entries[i].Text, Theme.Face(9.5f, on ? FontStyle.Bold : FontStyle.Regular), new Rectangle(s.X + (int)(52 * k), s.Y, s.Width - (int)(76 * k), s.Height), on ? Theme.Accent : Theme.Ink, TextFormatFlags.Left);
            if (entries[i].Badge) using (var b = new SolidBrush(Theme.Warn)) g.FillEllipse(b, s.Right - 20 * k, s.Y + (s.Height - 8 * k) / 2f, 8 * k, 8 * k);
            if (on && Ringed) Theme.Ring(g, new RectangleF(s.X - k, s.Y - k, s.Width + 2 * k, s.Height + 2 * k), 13 * k, k);
        }

        int fy = Height - (int)(118 * k);
        using (var line = new Pen(Theme.Border, k)) g.DrawLine(line, 20 * k, fy, Width - 20 * k, fy);
        Footer(g, "默认输出", outName, fy + (int)(14 * k), k);
        Footer(g, "默认输入", inName, fy + (int)(64 * k), k);
    }
}
