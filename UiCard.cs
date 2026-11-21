using System;
using System.Drawing;
using System.Windows.Forms;

// One row of the device list: what is shown for a SmartGroup, without any policy.
internal sealed class DeviceRow
{
    public SmartGroup Group;
    public string Title, Sub, Glyph;
    public bool Out, In, Dim;
    public string Key { get { return Group.Key; } }
    public bool SameLook(DeviceRow o)
    {
        return o != null && Title == o.Title && Sub == o.Sub && Glyph == o.Glyph && Out == o.Out && In == o.In && Dim == o.Dim;
    }
}

// Selectable device card. Up and Down move through the owning DeviceList.
internal sealed class DeviceCard : UiControl
{
    bool selected;
    public DeviceRow Row;
    public event EventHandler Picked;

    public DeviceCard() { Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.ListItem; }
    public bool Selected
    {
        get { return selected; }
        set { if (selected != value) { selected = value; Invalidate(); AccessibilityNotifyClients(AccessibleEvents.StateChange, -1); } }
    }
    public void Bind(DeviceRow row)
    {
        bool changed = !row.SameLook(Row);
        Row = row;
        AccessibleName = row.Title + "，" + row.Sub;
        if (changed) Invalidate();
    }
    void Raise() { EventHandler h = Picked; if (h != null) h(this, EventArgs.Empty); }
    protected override void OnClick(EventArgs e) { base.OnClick(e); Focus(); Raise(); }
    protected override bool IsInputKey(Keys key)
    {
        return key == Keys.Up || key == Keys.Down || key == Keys.Enter || key == Keys.Space || base.IsInputKey(key);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var list = Parent as DeviceList;
        if (e.KeyCode == Keys.Up && list != null) { list.Step(-1); e.Handled = true; }
        else if (e.KeyCode == Keys.Down && list != null) { list.Step(1); e.Handled = true; }
        else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { Raise(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    // Draws a status pill ending at x = right and returns where the next one may end.
    static int Pill(Graphics g, string text, int right, int mid, float k, Tone tone)
    {
        Font f = Theme.Face(8.5f);
        int w = Theme.Measure(text, f) + (int)(30 * k), h = (int)(22 * k), x = right - w;
        Color c = Theme.Pick(tone);
        Theme.Fill(g, new RectangleF(x, mid - h / 2f, w, h), h / 2f, Theme.Mix(Theme.Surface, c, Theme.Dark ? 0.3f : 0.14f));
        using (var b = new SolidBrush(c)) g.FillEllipse(b, x + 10 * k, mid - 3.5f * k, 7 * k, 7 * k);
        Theme.Draw(g, text, f, new Rectangle(x + (int)(22 * k), mid - h / 2, w - (int)(28 * k), h), c, TextFormatFlags.Left);
        return x - (int)(6 * k);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Row == null) return;
        var g = e.Graphics; Theme.Quality(g); float k = Theme.K(this);
        var r = new RectangleF(0.5f * k, 0.5f * k, Width - k, Height - k);
        Color back = selected ? Theme.AccentSoft : Theme.Mix(Theme.Surface, Theme.SurfaceHi, Hot ? 1f : 0f);
        Theme.Fill(g, r, 14 * k, back);
        Theme.Outline(g, r, 14 * k, selected ? Theme.Accent : Theme.Border, (selected ? 1.6f : 1f) * k);
        float tile = 44 * k;
        var t = new RectangleF(14 * k, (Height - tile) / 2f, tile, tile);
        bool live = Row.Out || Row.In;
        Theme.Fill(g, t, 12 * k, live ? Theme.Accent : (selected ? Theme.Surface : Theme.SurfaceHi));
        Theme.Draw(g, Row.Glyph, Theme.Icons(16f), Rectangle.Round(t), live ? Theme.OnAccent : (Row.Dim ? Theme.Muted : Theme.Accent), TextFormatFlags.HorizontalCenter);
        int right = Width - (int)(14 * k), subMid = (int)(46 * k);
        if (Row.In) right = Pill(g, "输入", right, subMid, k, Tone.Good);
        if (Row.Out) right = Pill(g, "输出", right, subMid, k, Tone.Good);
        int x = (int)(70 * k), full = Math.Max(0, Width - (int)(14 * k) - x), w = Math.Max(0, right - x);
        Theme.Draw(g, Row.Title, Theme.Face(10f, FontStyle.Bold), new Rectangle(x, (int)(11 * k), full, (int)(24 * k)), Row.Dim ? Theme.Muted : Theme.Ink, TextFormatFlags.Left);
        Theme.Draw(g, Row.Sub, Theme.Face(8.5f), new Rectangle(x, (int)(36 * k), w, (int)(20 * k)), Theme.Muted, TextFormatFlags.Left);
        if (Ringed) Theme.Ring(g, new RectangleF(0, 0, Width, Height), 15 * k, k);
    }
}
