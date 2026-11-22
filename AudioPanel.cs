using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Window shell, tray icon, hot keys and the refresh loop.
// Device model: AudioPanel.Logic.cs. Commands: AudioPanel.Actions.cs. Controls: AudioPanel.Devices.cs, AudioPanel.Pages.cs.
internal sealed partial class AudioPanel : Form
{
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    const int TunePageIndex=2, UpdatePageIndex=3;
    readonly EventWaitHandle openEvent;
    readonly PanelAudio audio;
    readonly AudioEventBus bus = new AudioEventBus();
    readonly EndpointEvents events;
    readonly System.Windows.Forms.Timer scanTimer = new System.Windows.Forms.Timer();
    readonly OutputArrivals arrivals = new OutputArrivals();
    readonly DecisionJournal journal = new DecisionJournal(AppConfig.Folder);
    RouteOverlay overlay;
    const ScanReason Hardware = ScanReason.Added | ScanReason.Removed | ScanReason.State | ScanReason.Property | ScanReason.Default;
    ScanReason pendingReasons;
    readonly HashSet<string> pendingIds=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    bool fullScan;
    long pendingSince;
    long nextJournalCleanup;
    long actionRevision, quietUntil;
    bool hotUndo, hasSnapshot, cycleOnReady;
    Dictionary<string,int> corrections;
    Dictionary<string,long> arrivalsUntil=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
    string selectedPort;
    long selectedUntil,nextPresenceHistory;
    readonly Dictionary<string,long> missingSince=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly string[] modes = { "auto", "speaker", "headphones", "microphone", "duplex", "show", "hide" };
    readonly string[] modeText = { "自动识别", "音箱（使用输出）", "耳机（使用输出）", "话筒（使用输入）", "耳麦（输入和输出）", "加入日常设备", "收进其他接口" };
    NavRail rail;
    Surface[] pages;
    UpdatePage updates;
    List<SmartGroup> groups = new List<SmartGroup>();
    string defaultOut, defaultIn, portSignature, hint = "";
    bool busy, closing, hotOut, hotMic, preferNew, built;
    long nextScan = long.MaxValue, nextView, nextPoll, statusUntil;
    readonly ScanGate gate = new ScanGate(400, 2000);

    int P(float v) { return Theme.Px(this, v); }

    public AudioPanel(bool hidden, EventWaitHandle open) : this(hidden,open,new PanelAudio()) { }

    internal AudioPanel(bool hidden, EventWaitHandle open, PanelAudio audio)
    {
        this.audio = audio;
        events = new EndpointEvents(bus);
        openEvent = open; preferNew = LocalSettings.Get("PreferNewOutput") == "1";
        Text = "桌面音频"; Icon = DeskIcon.Create(); Font = Theme.Face(9f);
        AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
        BackColor = Theme.Window; ForeColor = Theme.Ink; StartPosition = FormStartPosition.Manual;
        Screen target = Screen.AllScreens.FirstOrDefault(s => !s.Primary) ?? Screen.PrimaryScreen;
        var area = target.WorkingArea;
        ClientSize = new Size(Math.Min(P(1040), area.Width - P(24)), Math.Min(P(640), area.Height - P(60)));
        MinimumSize = SizeFromClientSize(new Size(Math.Min(P(980), ClientSize.Width), Math.Min(P(560), ClientSize.Height)));
        Location = new Point(Math.Max(area.X, area.X + (area.Width - Width) / 2), Math.Max(area.Y, area.Y + (area.Height - Height) / 2));
        InitLanding(); BuildShell();
        BuildDevices(pages[0]); BuildSettings(pages[1]); BuildUpdates(pages[UpdatePageIndex]); BuildTuning(pages[TunePageIndex]);
        BuildTray();
        IntPtr window = Handle;
        updates.Start();
        hotOut = RegisterHotKey(window, 1, 0x4003, (uint)Keys.F8); hotMic = RegisterHotKey(window, 2, 0x4003, (uint)Keys.F7);
        hotUndo = RegisterHotKey(window, 3, 0x4003, (uint)Keys.F9);
        UpdateShortcuts();
        built = true; LayoutAll(); Chrome();
        journal.Changed += delegate { corrections=null; };
        bus.Wake += WakeScanner;
        scanTimer.Tick += delegate { PumpScanner(); };
        bus.Publish(ScanReason.Startup);
        WakeScanner(); // A callback may already be waiting from before the window handle existed.
        if (AppConfig.Store.Warning != null) { hint = AppConfig.Store.Warning; SetStatus(hint,Tone.Warn); }
        timer.Interval = hidden ? 420 : 60; timer.Tick += OnTick; timer.Start();
        Theme.Changed += OnThemeChanged;
        VisibleChanged += delegate { timer.Interval = Visible ? 60 : 420; };
        FormClosing += (s, e) => { if (!closing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        FormClosed += (s, e) => {
            CancelTuningJob(); StopLanding(); landingTimer.Dispose(); Theme.Changed -= OnThemeChanged; timer.Stop(); bus.Wake -= WakeScanner; scanTimer.Dispose(); events.Dispose(); if (overlay != null) overlay.Dispose(); foreach (var peak in peaks) peak.Dispose();
            UnregisterHotKey(window, 1); UnregisterHotKey(window, 2); UnregisterHotKey(window, 3);
            tray.Visible = false; tray.Dispose(); Application.ExitThread();
        };
    }

    public void Open() { Show(); WindowState = FormWindowState.Normal; Activate(); bus.Publish(ScanReason.Refresh); }

    void BuildShell()
    {
        rail = new NavRail { Version = UpdateCheck.InstalledVersion.ToString(3) };
        rail.Add("", "设备"); rail.Add("", "切换与设置"); rail.Add("","声音调优"); rail.Add("", "更新");
        rail.IndexChanged += delegate { ShowPage(rail.Index); };
        Controls.Add(rail);
        pages = new Surface[4];
        for (int i = 0; i < pages.Length; i++) { pages[i] = new Surface(); Controls.Add(pages[i]); }
        ShowPage(0);
    }
    void ShowPage(int index)
    {
        for (int i = 0; i < pages.Length; i++) pages[i].Visible = i == index;
        if (index == UpdatePageIndex) { rail.Badge(UpdatePageIndex, false); if (updates != null) updates.Visit(); }
        if (index!=TunePageIndex) CancelTuningJob();
        if (built && index==TunePageIndex) { rail.Badge(TunePageIndex,false); RefreshTuningPage(); }
        if (built && index == 0) RefreshLevels();
    }
    void LayoutAll()
    {
        if (!built) return;
        SuspendLayout();
        int railWidth = P(220), w = ClientSize.Width, h = ClientSize.Height;
        rail.SetBounds(0, 0, railWidth, h);
        foreach (var page in pages) page.SetBounds(railWidth, 0, Math.Max(0, w - railWidth), h);
        LayoutDevices(pages[0]); LayoutSettings(pages[1]); LayoutTuning(pages[TunePageIndex]);
        ResumeLayout(false);
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutAll(); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); LayoutAll(); Invalidate(true); }

    void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Opening += delegate
        {
            menu.Items.Clear(); bus.Publish(ScanReason.Refresh); ThemedMenu.Apply(menu);
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
            menu.Items.Add("撤销最近自动切换", null, (o, a) => Act(() => UndoRoute("托盘")));
            if (pendingRoute != null && clock.ElapsedMilliseconds < offerUntil)
                menu.Items.Add("执行建议 · " + pendingRoute.Evidence.Winner().Name, null, (o,a) => Act(AcceptOffer));
            menu.Items.Add("活动时间线…", null, (o, a) => Act(ShowDecisions,false));
            menu.Items.Add("退出", null, (o, a) => { closing = true; Close(); });
        };
        tray.ContextMenuStrip = menu; tray.Icon = Icon; tray.Text = "桌面音频"; tray.Visible = true;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowQuick(); };
    }

    QuickPanel quick;
    // Left click on the tray icon opens a small panel; the full window stays one click further.
    void ShowQuick()
    {
        if (QuickPanel.JustClosed) return;
        if (quick != null && !quick.IsDisposed) { quick.Close(); return; }
        if (!hasSnapshot) { Open(); return; }
        try
        {
            string id = audio.Default(0,1);
            var current = id == null ? null : groups.FirstOrDefault(g => ContainsId(g.Outputs,id));
            string name = current == null ? "默认输出" : current.Name;
            var panel = new QuickPanel(OutputCycle(),p => current != null && ContainsId(current.Outputs,p.Id),
                id == null ? 0 : audio.Level(id),id != null && audio.Muted(id),
                p => Act(() => SetDefault(p)),
                n => Act(() => {
                    if (id == null) return;
                    quietUntil = clock.ElapsedMilliseconds + 30000;
                    if (ramp != null && Same(ramp.Endpoint,id)) StopLanding();
                    audio.SetLevel(id,n);
                },false),
                () => Act(() => { if (id != null) Toggle(new AudioItem { Id = id, Flow = 0, Name = name }); }),
                Open);
            panel.FormClosed += delegate { if (quick == panel) quick = null; };
            quick = panel; panel.Display();
        }
        catch (Exception e) { Fail(e.Message); }
    }

    // Endpoint events wake the scan scheduler independently of the tray polling interval.
    void OnTick(object sender, EventArgs e)
    {
        long now = clock.ElapsedMilliseconds;
        if (now >= nextScan)
        {
            nextScan = long.MaxValue;
            bus.Publish(ScanReason.Poll);
        }
        if (now >= nextPoll)
        {
            nextPoll = now + 420;
            try { LearnIfDue(); } catch (Exception error) { Fail(error.Message); }
            if (now >= nextJournalCleanup)
            {
                nextJournalCleanup = now + 24L * 60 * 60 * 1000;
                try { journal.Records(); } catch (Exception error) { Fail("决策记录无法读取 · " + error.Message); }
            }
            if (openEvent.WaitOne(0)) Open();
            if (statusUntil != 0 && now >= statusUntil) { statusUntil = 0; SetStatus(hint, Tone.Muted); }
            if (!gate.Running && Visible && rail.Index == 0 && now >= nextView && !volume[0].Dragging && !volume[1].Dragging) { nextView = now + 1800; RefreshLevels(); }
        }
        if (Visible && WindowState != FormWindowState.Minimized && rail.Index == 0 && !busy) FeedMeters();
    }

    // Endpoint callbacks only post a wakeup. All scheduling state belongs to the window thread.
    void WakeScanner()
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)PumpScanner); }
        catch (InvalidOperationException) { }
    }

    void PumpScanner()
    {
        if (closing || IsDisposed) return;
        scanTimer.Stop();
        long now = clock.ElapsedMilliseconds;
        long since;
        string[] dirty;
        ScanReason batch = bus.Drain(out since,out dirty);
        if (dirty==null) fullScan=true; else pendingIds.UnionWith(dirty);
        if (pendingReasons == ScanReason.None && batch != ScanReason.None) pendingSince = since;
        pendingReasons |= batch;
        if ((batch & Hardware) != 0)
            gate.Request(now);
        else if (batch != ScanReason.None) gate.Refresh(now);
        if (gate.Running || !gate.Pending) return;
        if (gate.TryBegin(now))
        {
            var reasons = pendingReasons;
            pendingReasons = ScanReason.None;
            var ids=fullScan ? null : pendingIds.ToArray();
            pendingIds.Clear(); fullScan=false;
            RunScan(reasons,pendingSince,ids);
        }
        else { scanTimer.Interval = gate.Delay(now); scanTimer.Start(); }
    }

    async void RunScan(ScanReason reasons,long since,string[] ids)
    {
        long revision = actionRevision;
        try
        {
            long scanStart=Stopwatch.GetTimestamp();
            var snap = await Task.Run(()=>TakeSnapshot(ids));
            snap.ScanStart=scanStart;
            if (closing || IsDisposed) return;
            PumpScanner(); // Pick up hardware changes that arrived while Windows was enumerating.
            if ((pendingReasons & Hardware) != 0) { pendingReasons |= reasons; pendingSince = since; }
            else if (revision == actionRevision) Apply(snap,reasons,since);
            else { pendingReasons |= reasons; pendingSince = since; bus.Publish(ScanReason.Refresh); }
        }
        catch (Exception e) { if (!closing && !IsDisposed) ScanFailed(e); }
        finally
        {
            gate.End();
            if (!closing && !IsDisposed) { nextScan = NextScanAt(); PumpScanner(); }
        }
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
        filterList.ForeColor=Theme.Ink; filterList.Restyle();
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
