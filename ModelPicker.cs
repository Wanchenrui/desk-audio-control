using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

// Searchable list of the measured presets. Folding punctuation lets "wh1000xm4" find "WH-1000XM4".
internal sealed class ModelPicker : Form
{
    sealed class Row { public EqPreset Preset; public string Key; }
    sealed class SmoothList : ListBox { public SmoothList() { DoubleBuffered = true; } }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr flag, string text);

    readonly List<Row> all, shown = new List<Row>();
    readonly EqPreset focus;
    readonly string[] kinds = { null, "over-ear", "in-ear" };
    readonly PillButton[] filters = new PillButton[3];
    readonly TextBox search = new TextBox();
    readonly SmoothList list = new SmoothList();
    readonly Caption count = new Caption();
    readonly PillButton choose = new PillButton();
    int kind;
    public EqPreset Picked { get; private set; }
    public bool Cleared { get; private set; }

    int P(float v) { return Theme.Px(this, v); }
    static string Fold(string text) { return Regex.Replace((text ?? "").ToLowerInvariant(), "[^a-z0-9]", ""); }

    public ModelPicker(EqPreset[] catalog, EqPreset current)
    {
        all = catalog.Select(p => new Row { Preset = p, Key = Fold(p.Model) }).ToList();
        focus = current;
        Text = "选择实物型号"; Font = Theme.Face(9f); BackColor = Theme.Window; ForeColor = Theme.Ink;
        AutoScaleMode = AutoScaleMode.None; FormBorderStyle = FormBorderStyle.FixedDialog; KeyPreview = true;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(P(520), P(570));
        int pad = P(20), w = ClientSize.Width - 2 * pad, gap = P(8), tab = P(104);
        search.BorderStyle = BorderStyle.FixedSingle; search.BackColor = Theme.Surface; search.ForeColor = Theme.Ink;
        search.Font = Theme.Face(10f); search.AccessibleName = "搜索型号"; search.SetBounds(pad, P(18), w, P(30));
        search.HandleCreated += delegate { SendMessage(search.Handle, 0x1501, new IntPtr(1), "输入型号，例如 WH-1000XM4"); };
        search.TextChanged += delegate { Refilter(); };
        string[] names = { "全部", "头戴式", "入耳式" };
        for (int i = 0; i < 3; i++)
        {
            int n = i;
            int total = all.Count(r => kinds[n] == null || r.Preset.Category == kinds[n]);
            var b = new PillButton { Text = names[i] + " " + total, Kind = ButtonLook.Soft, On = i == 0, AccessibleName = names[i] };
            b.SetBounds(pad + i * (tab + gap), P(58), tab, P(32));
            b.Click += delegate { kind = n; for (int j = 0; j < 3; j++) filters[j].On = j == n; Refilter(); search.Focus(); };
            filters[i] = b; Controls.Add(b);
        }
        count.Role = Tone.Muted; count.Pt = 9f; count.Align = ContentAlignment.MiddleRight;
        count.SetBounds(pad + 3 * (tab + gap), P(58), Math.Max(0, w - 3 * (tab + gap)), P(32));
        var frame = new Card { Radius = 10f };
        frame.SetBounds(pad, P(102), w, P(398));
        list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = P(30); list.BorderStyle = BorderStyle.None;
        list.BackColor = Theme.Surface; list.IntegralHeight = false; list.AccessibleName = "型号列表";
        list.SetBounds(P(4), P(4), w - P(8), P(390));
        list.DrawItem += DrawRow;
        list.SelectedIndexChanged += delegate { choose.Enabled = list.SelectedIndex >= 0; };
        list.DoubleClick += delegate { Accept(); };
        list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; Accept(); } };
        frame.Controls.Add(list);
        var clear = new PillButton { Text = "不使用预设", Kind = ButtonLook.Ghost, AccessibleName = "不使用预设" };
        var cancel = new PillButton { Text = "取消", Kind = ButtonLook.Ghost, AccessibleName = "取消" };
        choose.Text = "选择"; choose.Kind = ButtonLook.Solid; choose.AccessibleName = "选择该型号";
        int by = P(516), bh = P(36);
        clear.SetBounds(pad, by, P(120), bh);
        choose.SetBounds(pad + w - P(96), by, P(96), bh);
        cancel.SetBounds(choose.Left - P(104), by, P(96), bh);
        clear.Click += delegate { Cleared = true; DialogResult = DialogResult.OK; };
        cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
        choose.Click += delegate { Accept(); };
        Controls.AddRange(new Control[] { search, count, frame, clear, cancel, choose });
        KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; e.Handled = true; return; }
            bool moves = e.KeyCode == Keys.Down || e.KeyCode == Keys.Up || e.KeyCode == Keys.PageDown || e.KeyCode == Keys.PageUp;
            if (ActiveControl != search || !(moves || e.KeyCode == Keys.Enter)) return;
            int step = e.KeyCode == Keys.Down ? 1 : e.KeyCode == Keys.Up ? -1 : e.KeyCode == Keys.PageDown ? 8 : e.KeyCode == Keys.PageUp ? -8 : 0;
            if (moves) Shift(step); else Accept();
            e.Handled = e.SuppressKeyPress = true;
        };
        Shown += delegate { search.Focus(); };
        Refilter();
    }

    void Refilter()
    {
        string[] words = search.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => Fold(s)).Where(s => s.Length > 0).ToArray();
        int at = list.SelectedIndex;
        EqPreset keep = at >= 0 && at < shown.Count ? shown[at].Preset : focus;
        var found = all.Where(r => (kinds[kind] == null || r.Preset.Category == kinds[kind]) && words.All(w => r.Key.Contains(w)))
            .OrderBy(r => words.Length > 0 && r.Key.StartsWith(words[0], StringComparison.Ordinal) ? 0 : 1).ToList();
        shown.Clear(); shown.AddRange(found);
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var row in shown) list.Items.Add(row.Preset.Model);
        list.EndUpdate();
        list.SelectedIndex = shown.Count == 0 ? -1 : Math.Max(0, shown.FindIndex(r => r.Preset == keep));
        count.Role = shown.Count == 0 ? Tone.Warn : Tone.Muted;
        count.Text = shown.Count == 0 ? "没有匹配的型号" : shown.Count + " / " + all.Count + " 款";
    }

    void Shift(int delta)
    {
        if (shown.Count == 0) return;
        list.SelectedIndex = Math.Max(0, Math.Min(shown.Count - 1, Math.Max(0, list.SelectedIndex) + delta));
    }
    void Accept()
    {
        int i = list.SelectedIndex;
        if (i < 0 || i >= shown.Count) return;
        Picked = shown[i].Preset;
        DialogResult = DialogResult.OK;
    }

    void DrawRow(object sender, DrawItemEventArgs e)
    {
        var g = e.Graphics; float k = Theme.K(this);
        using (var back = new SolidBrush(Theme.Surface)) g.FillRectangle(back, e.Bounds);
        if (e.Index < 0 || e.Index >= shown.Count) return;
        bool on = (e.State & DrawItemState.Selected) != 0;
        var cell = new RectangleF(e.Bounds.X + 2 * k, e.Bounds.Y + k, e.Bounds.Width - 4 * k, e.Bounds.Height - 2 * k);
        if (on) { Theme.Quality(g); Theme.Fill(g, cell, 8 * k, Theme.AccentSoft); }
        var box = Rectangle.Round(cell);
        var preset = shown[e.Index].Preset;
        string tag = preset.Category == "in-ear" ? "入耳式" : "头戴式";
        int tagW = Theme.Measure(tag, Theme.Face(8.5f)) + (int)(16 * k);
        Theme.Draw(g, tag, Theme.Face(8.5f), new Rectangle(box.Right - tagW, box.Y, tagW - (int)(8 * k), box.Height), Theme.Muted, TextFormatFlags.Right);
        Theme.Draw(g, preset.Model, Theme.Face(9.5f, on ? FontStyle.Bold : FontStyle.Regular), new Rectangle(box.X + (int)(10 * k), box.Y, Math.Max(0, box.Width - tagW - (int)(14 * k)), box.Height), on ? Theme.Accent : Theme.Ink, TextFormatFlags.Left);
    }
}
