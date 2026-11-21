using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

// Scrolling stack of DeviceCard. Rows are matched by position, so a refresh never rebuilds cards.
internal sealed class DeviceList : Panel
{
    readonly List<DeviceCard> cards = new List<DeviceCard>();
    string selectedKey;
    public event EventHandler SelectionChanged;

    public DeviceList()
    {
        AutoScroll = true; BackColor = Theme.Window;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.List; AccessibleName = "设备列表";
    }
    static bool Same(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    public string SelectedKey { get { return selectedKey; } }
    public SmartGroup SelectedGroup
    {
        get { var c = cards.Find(x => x.Selected); return c == null ? null : c.Row.Group; }
    }

    // Keeps the current selection when it still exists, otherwise falls back to preferKey, then the first row.
    public void SetRows(IList<DeviceRow> rows, string preferKey)
    {
        bool recount = cards.Count != rows.Count;
        SuspendLayout();
        while (cards.Count > rows.Count)
        {
            var last = cards[cards.Count - 1];
            cards.RemoveAt(cards.Count - 1); Controls.Remove(last); last.Dispose();
        }
        while (cards.Count < rows.Count)
        {
            var card = new DeviceCard();
            card.Picked += OnPicked; cards.Add(card); Controls.Add(card);
        }
        string keep = null;
        foreach (var r in rows) if (Same(r.Key, selectedKey)) keep = r.Key;
        if (keep == null) foreach (var r in rows) if (Same(r.Key, preferKey)) { keep = r.Key; break; }
        if (keep == null && rows.Count > 0) keep = rows[0].Key;
        selectedKey = keep;
        for (int i = 0; i < rows.Count; i++)
        {
            cards[i].Bind(rows[i]);
            cards[i].Selected = Same(rows[i].Key, selectedKey);
        }
        if (recount) Arrange();
        ResumeLayout(false);
        Invalidate();
    }

    void OnPicked(object sender, EventArgs e) { Choose(((DeviceCard)sender).Row.Key, true); }
    public bool Choose(string key, bool raise)
    {
        if (Same(key, selectedKey)) return false;
        selectedKey = key;
        DeviceCard shown = null;
        foreach (var c in cards) { c.Selected = Same(c.Row.Key, key); if (c.Selected) shown = c; }
        if (shown != null) ScrollControlIntoView(shown);
        if (raise) { EventHandler h = SelectionChanged; if (h != null) h(this, EventArgs.Empty); }
        return true;
    }
    public void Step(int delta)
    {
        int i = cards.FindIndex(c => c.Selected);
        int n = Math.Max(0, Math.Min(cards.Count - 1, i + delta));
        if (cards.Count == 0 || n == i) return;
        Choose(cards[n].Row.Key, true);
        cards[n].Focus();
    }

    public void Arrange()
    {
        float k = Theme.K(this);
        int gap = (int)(10 * k), h = (int)(68 * k), pad = (int)(2 * k);
        AutoScrollMinSize = new Size(0, pad + cards.Count * (h + gap));
        int w = Math.Max(0, ClientSize.Width - 2 * pad), y = pad + AutoScrollPosition.Y;
        foreach (var c in cards) { c.SetBounds(pad, y, w, h); y += h + gap; }
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Arrange(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var b = new SolidBrush(BackColor)) e.Graphics.FillRectangle(b, e.ClipRectangle);
        if (cards.Count > 0) return;
        float k = Theme.K(this);
        var box = new Rectangle(0, ClientSize.Height / 2 - (int)(40 * k), ClientSize.Width, (int)(30 * k));
        Theme.Draw(e.Graphics, "等待设备连接", Theme.Face(11f, FontStyle.Bold), box, Theme.Ink, TextFormatFlags.HorizontalCenter);
        box.Y += (int)(30 * k);
        Theme.Draw(e.Graphics, "接入 USB 或蓝牙音频设备后会自动出现", Theme.Face(9f), box, Theme.Muted, TextFormatFlags.HorizontalCenter);
    }
}
