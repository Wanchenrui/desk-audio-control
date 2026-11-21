using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

// Light/dark aware drop-down menus, used by the tray icon and the port pickers.
internal static class ThemedMenu
{
    sealed class Renderer : ToolStripProfessionalRenderer
    {
        public Renderer() : base(new ProfessionalColorTable()) { RoundedEdges = false; }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Surface)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            Theme.Quality(e.Graphics);
            Theme.Fill(e.Graphics, new RectangleF(4, 1, e.Item.Width - 8, e.Item.Height - 2), 6, Theme.AccentSoft);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 10, e.Item.Height / 2, e.Item.Width - 10, e.Item.Height / 2);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? Theme.Muted : (e.Item.Selected ? Theme.Accent : Theme.Ink);
            base.OnRenderItemText(e);
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Theme.Draw(e.Graphics, "", Theme.Icons(10f), e.ImageRectangle, Theme.Accent, TextFormatFlags.HorizontalCenter);
        }
    }

    public static void Apply(ContextMenuStrip menu)
    {
        menu.Renderer = new Renderer();
        menu.Font = Theme.Face(9.5f);
        menu.BackColor = Theme.Surface;
        menu.ForeColor = Theme.Ink;
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = true;
        menu.Padding = new Padding(0, 4, 0, 4);
    }

    // Opens a single-choice list just below anchor and reports the picked index.
    public static void Choose(Control anchor, IList<string> labels, int current, Action<int> pick)
    {
        var menu = new ContextMenuStrip();
        Apply(menu);
        for (int i = 0; i < labels.Count; i++)
        {
            int n = i;
            var item = new ToolStripMenuItem(labels[i]) { Checked = i == current };
            item.Click += delegate { pick(n); };
            menu.Items.Add(item);
        }
        menu.MinimumSize = new Size(anchor.Width, 0);
        menu.Closed += delegate { anchor.BeginInvoke((Action)menu.Dispose); };
        menu.Show(anchor, new Point(0, anchor.Height + 4));
    }
}
