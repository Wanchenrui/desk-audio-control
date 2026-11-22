using System;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Read-only table of a preset's filters: aligned columns, with each gain drawn as a bar around a zero line.
internal sealed class EqTable : Panel
{
    EqPreset preset;

    public EqTable()
    {
        AutoScroll = true; BackColor = Theme.Surface; TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        AccessibleRole = AccessibleRole.Table; AccessibleName = "参数均衡器推荐";
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr window, string app, string list);
    int Head { get { return Theme.Px(this, 26f); } }
    int Line { get { return Theme.Px(this, 22f); } }
    // Height that shows the preamp row and the given number of filters without scrolling.
    public int Wanted(int filters) { return Head + Line * (filters + 1) + 2; }
    static Rectangle Cell(int x, int w, int y, int h, float k) { return new Rectangle((int)(x * k), y, (int)(w * k), h); }
    static string TypeName(string type) { return type == "PK" ? "峰值" : type == "LSC" ? "低频搁架" : "高频搁架"; }
    static string Freq(double hz) { return hz >= 1000 ? (hz / 1000).ToString("0.##", CultureInfo.InvariantCulture) + " kHz" : hz.ToString("0.#", CultureInfo.InvariantCulture) + " Hz"; }
    static string Signed(double v, string unit) { return (v > 0 ? "+" : "") + v.ToString("0.0", CultureInfo.InvariantCulture) + unit; }

    // Screen readers get the full parameter text, since the rows are drawn rather than child controls.
    public void Bind(EqPreset next)
    {
        if (preset == next) return;
        preset = next;
        AccessibleDescription = next == null ? "" : next.Parameters;
        AutoScrollPosition = Point.Empty;
        Fit(); Invalidate();
    }
    void Fit() { AutoScrollMinSize = preset == null ? Size.Empty : new Size(0, Head + Line * (preset.Rows.Count + 1)); }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Fit(); }
    // The native scroll bar follows the app theme, so dark mode does not get a white strip.
    public void Restyle()
    {
        BackColor = Theme.Surface;
        if (IsHandleCreated)
        {
            try { SetWindowTheme(Handle, Theme.Dark ? "DarkMode_Explorer" : "Explorer", null); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
        Invalidate();
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Restyle(); }
    protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; float k = Theme.K(this);
        using (var back = new SolidBrush(Theme.Surface)) g.FillRectangle(back, e.ClipRectangle);
        if (preset == null) return;
        int head = Head, line = Line, width = ClientSize.Width, pad = (int)(10 * k);
        int right = width - pad, qLeft = right - (int)(48 * k);
        int barLeft = (int)(266 * k), barRight = qLeft - (int)(10 * k);
        var body = Theme.Face(9f); var strong = Theme.Face(9f, FontStyle.Bold);
        int y = head + AutoScrollPosition.Y;

        Theme.Draw(g, "前置增益", body, Cell(10, 120, y, line, k), Theme.Muted, TextFormatFlags.Left);
        Theme.Draw(g, Signed(preset.Preamp, " dB"), strong, Cell(190, 62, y, line, k), Theme.Ink, TextFormatFlags.Right);
        for (int i = 0; i < preset.Rows.Count; i++)
        {
            var r = preset.Rows[i];
            int top = y + line * (i + 1);
            if (top + line < head || top > ClientSize.Height) continue;
            if (i % 2 == 0) using (var stripe = new SolidBrush(Theme.SurfaceHi)) g.FillRectangle(stripe, 0, top, width, line);
            Color tone = r.Gain >= 0 ? Theme.Accent : Theme.Muted;
            Theme.Draw(g, (i + 1).ToString(CultureInfo.InvariantCulture), body, Cell(10, 24, top, line, k), Theme.Muted, TextFormatFlags.Left);
            Theme.Draw(g, TypeName(r.Type), body, Cell(36, 76, top, line, k), Theme.Ink, TextFormatFlags.Left);
            Theme.Draw(g, Freq(r.Hz), body, Cell(112, 72, top, line, k), Theme.Ink, TextFormatFlags.Right);
            Theme.Draw(g, Signed(r.Gain, " dB"), strong, Cell(190, 62, top, line, k), r.Gain >= 0 ? Theme.Accent : Theme.Ink, TextFormatFlags.Right);
            Theme.Draw(g, r.Q.ToString("0.00", CultureInfo.InvariantCulture), body, new Rectangle(qLeft, top, right - qLeft, line), Theme.Ink, TextFormatFlags.Right);
            int bw = barRight - barLeft;
            if (bw <= (int)(40 * k)) continue;
            float cx = barLeft + bw / 2f, half = bw / 2f, len = (float)Math.Min(1.0, Math.Abs(r.Gain) / 12.0) * half;
            float bh = 6 * k, by = top + (line - bh) / 2f;
            using (var pen = new Pen(Theme.Border, 1f)) g.DrawLine(pen, cx, top + 4 * k, cx, top + line - 4 * k);
            Theme.Quality(g);
            Theme.Fill(g, new RectangleF(r.Gain >= 0 ? cx : cx - len, by, Math.Max(len, 1f), bh), 3 * k, tone);
        }

        // The header is drawn last so rows scroll underneath it.
        using (var back = new SolidBrush(Theme.Surface)) g.FillRectangle(back, 0, 0, width, head);
        var cap = Theme.Face(8.5f);
        Theme.Draw(g, "#", cap, Cell(10, 24, 0, head, k), Theme.Muted, TextFormatFlags.Left);
        Theme.Draw(g, "类型", cap, Cell(36, 76, 0, head, k), Theme.Muted, TextFormatFlags.Left);
        Theme.Draw(g, "频率", cap, Cell(112, 72, 0, head, k), Theme.Muted, TextFormatFlags.Right);
        Theme.Draw(g, "增益", cap, Cell(190, 62, 0, head, k), Theme.Muted, TextFormatFlags.Right);
        Theme.Draw(g, "Q", cap, new Rectangle(qLeft, 0, right - qLeft, head), Theme.Muted, TextFormatFlags.Right);
        using (var pen = new Pen(Theme.Border, 1f)) g.DrawLine(pen, 0, head - 1, width, head - 1);
    }
}
