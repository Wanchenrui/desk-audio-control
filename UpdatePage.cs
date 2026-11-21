using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;

// Update page: installed version, background check and access to the private repository.
internal sealed class UpdatePage : Panel
{
    const string TokenCreationPage = "https://github.com/settings/personal-access-tokens/new?name=DeskAudio%20updates&target_name=Wanchenrui&contents=read";
    readonly Form owner;
    readonly Action<string> notify;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly Card versionCard = new Card(), accessCard = new Card();
    readonly Caption title = new Caption(), heading = new Caption(), status = new Caption(), lastCheck = new Caption();
    readonly Caption accessHeading = new Caption(), accessBody = new Caption();
    readonly ToggleSwitch automatic = new ToggleSwitch(), signedIn = new ToggleSwitch();
    readonly PillButton check = new PillButton(), releases = new PillButton(), access = new PillButton(), create = new PillButton();
    bool running;
    string pageUrl = UpdateCheck.ReleasesPage;
    readonly string version = UpdateCheck.InstalledVersion.ToString(3);

    public UpdatePage(Form owner, Action<string> notify)
    {
        this.owner = owner;
        this.notify = notify;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Controls.Add(title); Controls.Add(versionCard); Controls.Add(accessCard);
        title.Text = "更新"; title.Pt = 14f; title.Bold = true;
        heading.Text = "桌面音频  v" + version; heading.Pt = 11f; heading.Bold = true;
        status.Wrap = true;
        string saved = LocalSettings.Get("UpdateStatusVersion") == version ? LocalSettings.Get("UpdateStatus") : null;
        status.Text = String.IsNullOrWhiteSpace(saved) ? "尚未检查此版本。" : saved;
        lastCheck.Role = Tone.Muted; lastCheck.Pt = 8.5f;
        ShowLastCheck();
        Setup(check, "立即检查", ButtonLook.Solid, StartCheck);
        Setup(releases, "打开发布页", ButtonLook.Soft, delegate { OpenPage(pageUrl); });
        automatic.Text = "每天在后台检查新版本"; automatic.AccessibleName = automatic.Text;
        automatic.Set(LocalSettings.Get("AutomaticUpdates") != "0");
        automatic.CheckedChanged += delegate {
            LocalSettings.Put("AutomaticUpdates", automatic.Checked ? "1" : "0");
            if (automatic.Checked) CheckIfDue();
        };
        versionCard.Controls.AddRange(new Control[] { heading, status, lastCheck, check, releases, automatic });

        accessHeading.Text = "私有仓库访问"; accessHeading.Pt = 11f; accessHeading.Bold = true;
        signedIn.Text = "允许使用本机 GitHub 登录检查更新（Git for Windows 或 GitHub CLI）"; signedIn.AccessibleName = signedIn.Text;
        signedIn.Set(UpdateCheck.UseSignedInAccount);
        signedIn.CheckedChanged += delegate {
            UpdateCheck.UseSignedInAccount = signedIn.Checked;
            StartCheck();
        };
        accessBody.Text = "也可单独保存此仓库的只读令牌：选择 desk-audio-control 仓库，Contents 设为 Read-only。";
        accessBody.Role = Tone.Muted; accessBody.Wrap = true;
        Setup(access, "设置更新凭据…", ButtonLook.Soft, EditAccess);
        Setup(create, "在 GitHub 创建只读令牌", ButtonLook.Ghost, delegate { OpenPage(TokenCreationPage); });
        accessCard.Controls.AddRange(new Control[] { accessHeading, signedIn, accessBody, access, create });
    }

    public void Start()
    {
        timer.Interval = 5000;
        timer.Tick += delegate { timer.Interval = 60000; CheckIfDue(); };
        timer.Start();
    }

    void Setup(PillButton button, string text, ButtonLook look, Action action)
    {
        button.Text = text; button.Kind = look; button.AccessibleName = text;
        button.Click += delegate { action(); };
    }
    int P(float v) { return Theme.Px(this, v); }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using (var b = new SolidBrush(Theme.Window)) e.Graphics.FillRectangle(b, e.ClipRectangle);
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Arrange(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Arrange(); }
    void Arrange()
    {
        int pad = P(28), m = P(18), gap = P(12), cw = Math.Max(0, Width - 2 * pad), iw = Math.Max(0, cw - 2 * m);
        title.SetBounds(pad, P(20), cw, P(34));
        int y = P(68), h1 = P(212), h2 = P(184);
        versionCard.SetBounds(pad, y, cw, h1);
        heading.SetBounds(m, P(10), iw, P(30));
        status.SetBounds(m, P(44), iw, P(44));
        lastCheck.SetBounds(m, P(90), iw, P(22));
        check.SetBounds(m, P(122), P(116), P(34));
        releases.SetBounds(m + P(126), P(122), P(172), P(34));
        automatic.SetBounds(m, P(168), iw, P(32));
        accessCard.SetBounds(pad, y + h1 + gap, cw, h2);
        accessHeading.SetBounds(m, P(10), iw, P(30));
        signedIn.SetBounds(m, P(44), iw, P(32));
        accessBody.SetBounds(m, P(84), iw, P(44));
        access.SetBounds(m, P(134), P(150), P(34));
        create.SetBounds(m + P(160), P(134), P(210), P(34));
    }

    static Tone ToneOf(UpdateState state)
    {
        switch (state)
        {
            case UpdateState.Current: return Tone.Good;
            case UpdateState.Available: return Tone.Accent;
            case UpdateState.NeedsAccess: return Tone.Warn;
            case UpdateState.Failed: return Tone.Bad;
            default: return Tone.Muted;
        }
    }
    DateTime? LastAttempt()
    {
        DateTime time;
        string stored = LocalSettings.Get("UpdateAttemptUtc");
        if (!DateTime.TryParseExact(stored, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time))
            return null;
        return time.ToUniversalTime();
    }
    void ShowLastCheck()
    {
        var last = LastAttempt();
        lastCheck.Text = last.HasValue ? "上次检查    " + last.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
    }
    void CheckIfDue()
    {
        if (!automatic.Checked || running) return;
        var last = LastAttempt();
        var now = DateTime.UtcNow;
        if (LocalSettings.Get("UpdateStatusVersion") != version || !last.HasValue ||
            now < last.Value || now - last.Value >= TimeSpan.FromDays(1)) StartCheck();
    }
    async void StartCheck()
    {
        if (running || IsDisposed) return;
        running = true;
        check.Enabled = access.Enabled = signedIn.Enabled = false;
        status.Role = Tone.Muted; status.Text = "正在向 GitHub 查询最新正式版…";
        try {
            UpdateResult result = await Task.Run(() => UpdateCheck.Check());
            if (IsDisposed || owner.IsDisposed) return;
            status.Role = ToneOf(result.State); status.Text = result.Message;
            pageUrl = result.PageUrl ?? UpdateCheck.ReleasesPage;
            bool available = result.State == UpdateState.Available;
            releases.Text = available ? "查看新版本并下载" : "打开发布页";
            releases.Kind = available ? ButtonLook.Solid : ButtonLook.Soft;
            releases.Invalidate();
            if (available && automatic.Checked && LocalSettings.Get("LastNotifiedUpdate") != result.Tag) {
                if (!owner.Visible || !Visible) notify(result.Tag);
                LocalSettings.Put("LastNotifiedUpdate", result.Tag);
            }
        } catch (Exception) {
            if (!IsDisposed) { status.Role = Tone.Warn; status.Text = "本次检查未完成，请稍后重试。"; }
        } finally {
            running = false;
            if (!IsDisposed && !owner.IsDisposed) {
                LocalSettings.Put("UpdateStatus", status.Text);
                LocalSettings.Put("UpdateStatusVersion", version);
                LocalSettings.Put("UpdateAttemptUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                ShowLastCheck();
                check.Enabled = access.Enabled = signedIn.Enabled = true;
            }
        }
    }
    void OpenPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { MessageBox.Show(owner, "无法打开默认浏览器。", "更新"); }
    }

    void EditAccess()
    {
        float k = Theme.K(this);
        Func<float, int> px = v => (int)Math.Round(v * k);
        using (var dialog = new Form {
            Text = "更新凭据", Font = Theme.Face(9f), BackColor = Theme.Window, ForeColor = Theme.Ink,
            AutoScaleMode = AutoScaleMode.None, FormBorderStyle = FormBorderStyle.FixedDialog, KeyPreview = true,
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false
        }) {
            dialog.ClientSize = new Size(px(500), px(232));
            int pad = px(24), w = px(452);
            var label = new Caption { Text = UpdateCheck.HasSavedToken ? "已保存一枚令牌。输入新令牌可替换，也可移除。" : "粘贴此仓库的细粒度只读令牌。", Wrap = true };
            label.SetBounds(pad, px(16), w, px(40));
            var input = new TextBox {
                UseSystemPasswordChar = true, MaxLength = 1024, AccessibleName = "GitHub 只读令牌", BorderStyle = BorderStyle.FixedSingle,
                BackColor = Theme.Surface, ForeColor = Theme.Ink, Font = Theme.Face(10f)
            };
            input.SetBounds(pad, px(64), w, px(30));
            var hint = new Caption { Text = "令牌由 Windows 加密保存在当前用户账户中。", Role = Tone.Muted, Pt = 8.5f };
            hint.SetBounds(pad, px(104), w, px(26));
            var clear = new PillButton { Text = "移除已存令牌", Kind = ButtonLook.Ghost, Enabled = UpdateCheck.HasSavedToken };
            clear.SetBounds(pad, px(170), px(136), px(36));
            var cancel = new PillButton { Text = "取消", Kind = ButtonLook.Ghost };
            cancel.SetBounds(pad + w - px(236), px(170), px(100), px(36));
            var save = new PillButton { Text = "保存并检查", Kind = ButtonLook.Solid, Enabled = false };
            save.SetBounds(pad + w - px(128), px(170), px(128), px(36));
            Action doSave = delegate {
                if (!save.Enabled) return;
                try { UpdateCheck.SaveToken(input.Text.Trim()); input.Clear(); dialog.DialogResult = DialogResult.OK; }
                catch (Exception) { MessageBox.Show(dialog, "保存失败，请检查令牌格式和当前用户的写入权限。", "更新凭据"); }
            };
            input.TextChanged += delegate { save.Enabled = !String.IsNullOrWhiteSpace(input.Text); };
            input.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; doSave(); } };
            save.Click += delegate { doSave(); };
            clear.Click += delegate {
                try { UpdateCheck.ClearToken(); dialog.DialogResult = DialogResult.OK; }
                catch (Exception) { MessageBox.Show(dialog, "无法移除凭据。", "更新凭据"); }
            };
            cancel.Click += delegate { dialog.DialogResult = DialogResult.Cancel; };
            dialog.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) dialog.DialogResult = DialogResult.Cancel; };
            dialog.Controls.AddRange(new Control[] { label, input, hint, clear, cancel, save });
            dialog.Shown += delegate { input.Focus(); };
            if (dialog.ShowDialog(owner) == DialogResult.OK) StartCheck();
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
