using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed class RouteOverlay : Form
{
    readonly Timer timeout = new Timer { Interval = 1200 };
    readonly string label;
    readonly int volume;
    readonly bool hasAction;
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out BoundsRect rect);
    [StructLayout(LayoutKind.Sequential)] struct BoundsRect { public int Left,Top,Right,Bottom; }

    public static bool FullScreen()
    {
        int state;
        if (SHQueryUserNotificationState(out state) == 0 && (state == 2 || state == 3 || state == 4)) return true;
        IntPtr hwnd = GetForegroundWindow();
        BoundsRect r;
        if (hwnd == IntPtr.Zero || hwnd == GetShellWindow() || !GetWindowRect(hwnd,out r)) return false;
        // The desktop's WorkerW can also cover the entire monitor.
        var name = new System.Text.StringBuilder(64);
        GetClassName(hwnd,name,name.Capacity);
        if (name.ToString() == "WorkerW" || name.ToString() == "Progman") return false;
        var screen = Screen.FromHandle(hwnd).Bounds;
        return r.Left <= screen.Left && r.Right >= screen.Right && r.Top <= screen.Top && r.Bottom >= screen.Bottom;
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd,System.Text.StringBuilder text,int size);
    public RouteOverlay(string device,int level,Action action,string actionLabel = "撤销")
    {
        label = device; volume = level; hasAction = action != null;
        if (actionLabel == "执行") timeout.Interval = 6000;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true; BackColor = Theme.Surface; AccessibleName = "音频设备切换 · " + device;
        ClientSize = new Size(P(hasAction ? 352 : 282),P(70));
        using (var shape = Theme.Round(ClientRectangle,P(28))) Region = new Region(shape);
        if (hasAction)
        {
            var button = new PillButton { Text = actionLabel, AccessibleName = actionLabel + "切换", Kind = ButtonLook.Soft };
            button.SetBounds(Width-P(82),P(17),P(66),P(36));
            button.Click += delegate { Hide(); action(); };
            Controls.Add(button);
        }
        timeout.Tick += delegate { timeout.Stop(); Close(); };
    }
    int P(int value) { return Theme.Px(this,value); }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80; return cp; } }
    public void Display()
    {
        var area = Screen.FromHandle(GetForegroundWindow()).WorkingArea;
        Location = new Point(area.Right-Width-P(16),area.Bottom-Height-P(16));
        Show(); timeout.Start();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int x = P(56), w = Width - x - P(hasAction ? 94 : 24);
        Theme.Quality(e.Graphics);
        Theme.Draw(e.Graphics,"",Theme.Icons(16),new Rectangle(P(14),0,P(32),Height),Theme.Accent,TextFormatFlags.HorizontalCenter);
        Theme.Draw(e.Graphics,label,Theme.Face(10,FontStyle.Bold),new Rectangle(x,P(10),w,P(30)),Theme.Ink,TextFormatFlags.Left);
        if (volume >= 0)
        {
            var track = new RectangleF(x,P(45),w,P(5));
            Theme.Fill(e.Graphics,track,P(2),Theme.Track);
            track.Width *= volume/100f;
            if (track.Width > 0) Theme.Fill(e.Graphics,track,P(2),Theme.Accent);
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) timeout.Dispose(); base.Dispose(disposing); }
}
