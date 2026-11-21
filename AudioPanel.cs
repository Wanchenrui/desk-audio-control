using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Window shell, tray icon, hot keys and the refresh loop.
// Device model: AudioPanel.Logic.cs. Commands: AudioPanel.Actions.cs. Controls: AudioPanel.Devices.cs, AudioPanel.Pages.cs.
internal sealed partial class AudioPanel : Form
{
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    readonly EventWaitHandle openEvent;
    readonly EndpointEvents events = new EndpointEvents();
    readonly PlugChanges plugs = new PlugChanges();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly string[] modes = { "auto", "speaker", "headphones", "microphone", "duplex", "show", "hide" };
    readonly string[] modeText = { "自动识别", "音箱（使用输出）", "耳机（使用输出）", "话筒（使用输入）", "耳麦（输入和输出）", "加入日常设备", "收进其他接口" };
    NavRail rail;
    Surface[] pages;
    UpdatePage updates;
    List<SmartGroup> groups = new List<SmartGroup>();
    HashSet<string> previousOutputs;
    string defaultOut, defaultIn, portSignature, hint = "";
    bool busy, closing, hotOut, hotMic, preferNew, built;
    long nextScan, nextView, nextPoll, statusUntil;

    int P(float v) { return Theme.Px(this, v); }

    public AudioPanel(bool hidden, EventWaitHandle open)
    {
        openEvent = open; preferNew = LocalSettings.Get("PreferNewOutput") == "1";
        Text = "桌面音频"; Icon = DeskIcon.Create(); Font = Theme.Face(9f);
        AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
        BackColor = Theme.Window; ForeColor = Theme.Ink; StartPosition = FormStartPosition.Manual;
        Screen target = Screen.AllScreens.FirstOrDefault(s => !s.Primary) ?? Screen.PrimaryScreen;
        var area = target.WorkingArea;
        ClientSize = new Size(Math.Min(P(1040), area.Width - P(24)), Math.Min(P(640), area.Height - P(60)));
        MinimumSize = SizeFromClientSize(new Size(Math.Min(P(980), ClientSize.Width), Math.Min(P(560), ClientSize.Height)));
        Location = new Point(Math.Max(area.X, area.X + (area.Width - Width) / 2), Math.Max(area.Y, area.Y + (area.Height - Height) / 2));
        BuildShell();
        BuildDevices(pages[0]); BuildSettings(pages[1]); BuildUpdates(pages[2]);
        BuildTray();
        IntPtr window = Handle;
        updates.Start();
        hotOut = RegisterHotKey(window, 1, 0x4003, (uint)Keys.F8); hotMic = RegisterHotKey(window, 2, 0x4003, (uint)Keys.F7);
        UpdateShortcuts();
        built = true; LayoutAll(); Chrome();
        RefreshDevices(false);
        LocalSettings.Log("hotkey-output=" + hotOut + " hotkey-mic=" + hotMic + " endpoint-events=" + events.Registered + " groups=" + groups.Count + " daily=" + groups.Count(Primary) + " cycle=" + OutputCycle().Count + " status=" + status.Text);
        timer.Interval = 60; timer.Tick += OnTick; timer.Start();
        Theme.Changed += OnThemeChanged;
        VisibleChanged += delegate { timer.Interval = Visible ? 60 : 420; };
        FormClosing += (s, e) => { if (!closing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        FormClosed += (s, e) => {
            Theme.Changed -= OnThemeChanged; timer.Stop(); events.Dispose();
            UnregisterHotKey(window, 1); UnregisterHotKey(window, 2);
            tray.Visible = false; tray.Dispose(); Application.ExitThread();
        };
    }

    public void Open() { Show(); WindowState = FormWindowState.Normal; Activate(); RefreshDevices(false); }

    void BuildShell()
    {
        rail = new NavRail { Version = UpdateCheck.InstalledVersion.ToString(3) };
        rail.Add("", "设备"); rail.Add("", "切换与设置"); rail.Add("", "更新");
        rail.IndexChanged += delegate { ShowPage(rail.Index); };
        Controls.Add(rail);
        pages = new Surface[3];
        for (int i = 0; i < pages.Length; i++) { pages[i] = new Surface(); Controls.Add(pages[i]); }
        ShowPage(0);
    }
    void ShowPage(int index)
    {
        for (int i = 0; i < pages.Length; i++) pages[i].Visible = i == index;
        if (index == 2) rail.Badge(2, false);
        if (built && index == 0) RefreshLevels();
    }
    void LayoutAll()
    {
        if (!built) return;
        SuspendLayout();
        int railWidth = P(220), w = ClientSize.Width, h = ClientSize.Height;
        rail.SetBounds(0, 0, railWidth, h);
        foreach (var page in pages) page.SetBounds(railWidth, 0, Math.Max(0, w - railWidth), h);
        LayoutDevices(pages[0]); LayoutSettings(pages[1]);
        ResumeLayout(false);
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutAll(); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); LayoutAll(); Invalidate(true); }

    void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Opening += delegate
        {
            menu.Items.Clear(); RefreshDevices(false); ThemedMenu.Apply(menu);
            menu.Items.Add("打开音频面板", null, (o, a) => Open());
            var picks = OutputCycle();
            if (picks.Count > 0) menu.Items.Add(new ToolStripSeparator());
            foreach (var p in picks)
            {
                AudioItem item = p;
                var mi = (ToolStripMenuItem)menu.Items.Add("切到 " + p.Name, null, (o, a) => Act(() => SetDefault(item)));
                mi.Checked = Same(p.Id, defaultOut);
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("默认话筒静音 / 取消静音", null, (o, a) => Act(ToggleDefaultMic));
            menu.Items.Add("退出", null, (o, a) => { closing = true; Close(); });
        };
        tray.ContextMenuStrip = menu; tray.Icon = Icon; tray.Text = "桌面音频"; tray.Visible = true;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Open(); };
    }

    // Runs every 60 ms while visible and every 420 ms in the tray. Device polling stays at 420 ms; only the meters run faster.
    void OnTick(object sender, EventArgs e)
    {
        long now = clock.ElapsedMilliseconds;
        if (now >= nextPoll)
        {
            nextPoll = now + 420;
            if (openEvent.WaitOne(0)) Open();
            if (statusUntil != 0 && now >= statusUntil) { statusUntil = 0; SetStatus(hint, Tone.Muted); }
            if (events.Changed || now >= nextScan) RefreshDevices(true);
            else if (Visible && rail.Index == 0 && now >= nextView && !volume[0].Dragging && !volume[1].Dragging) { nextView = now + 1800; RefreshLevels(); }
        }
        if (Visible && WindowState != FormWindowState.Minimized && rail.Index == 0 && !busy) FeedMeters();
    }

    // The system theme event arrives on a background thread.
    void OnThemeChanged(object sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((Action)ApplyTheme); } catch (InvalidOperationException) { }
    }
    void ApplyTheme()
    {
        if (IsDisposed || !built) return;
        BackColor = Theme.Window; ForeColor = Theme.Ink;
        foreach (var page in pages) page.BackColor = Theme.Window;
        devices.BackColor = Theme.Window;
        Chrome(); Invalidate(true);
    }
    void Chrome()
    {
        if (!IsHandleCreated) return;
        int dark = Theme.Dark ? 1 : 0;
        try { DwmSetWindowAttribute(Handle, 20, ref dark, 4); DwmSetWindowAttribute(Handle, 19, ref dark, 4); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Chrome(); }
}
