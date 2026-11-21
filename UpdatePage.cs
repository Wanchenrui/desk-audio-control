using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class UpdatePage : TabPage
{
    const string TokenCreationPage = "https://github.com/settings/personal-access-tokens/new?name=DeskAudio%20updates&target_name=Wanchenrui&contents=read";
    readonly Form owner;
    readonly Action<string> notify;
    readonly Timer timer = new Timer();
    readonly CheckBox automatic = new CheckBox();
    readonly Label status = new Label();
    readonly Label lastCheck = new Label();
    readonly Button check = new Button();
    readonly Button releases = new Button();
    readonly Button access = new Button();
    readonly CheckBox signedIn = new CheckBox();
    bool running;
    string pageUrl = UpdateCheck.ReleasesPage;
    readonly string version = UpdateCheck.InstalledVersion.ToString(3);

    public UpdatePage(Form owner, Action<string> notify) : base("更新")
    {
        this.owner = owner;
        this.notify = notify;
        BackColor = Color.White;
        AutoScroll = true;
        AddLabel("桌面音频   v" + version, 18, 16, 620, 36, true);
        automatic.Text = "每天在后台检查新版本";
        automatic.Checked = LocalSettings.Get("AutomaticUpdates") != "0";
        Place(automatic, 18, 67, 600, 32);
        automatic.CheckedChanged += delegate {
            LocalSettings.Put("AutomaticUpdates", automatic.Checked ? "1" : "0");
            if (automatic.Checked) CheckIfDue();
        };

        status.Text = LocalSettings.Get("UpdateStatusVersion") == version ? LocalSettings.Get("UpdateStatus") : null;
        if (String.IsNullOrWhiteSpace(status.Text)) status.Text = "尚未检查此版本。";
        Place(status, 19, 121, 620, 56);
        lastCheck.ForeColor = Color.DimGray;
        Place(lastCheck, 19, 184, 615, 24);
        ShowLastCheck();
        SetButton(check, "立即检查", 18, 221, 116, delegate { StartCheck(); });
        SetButton(releases, "打开发布页", 147, 221, 154, delegate { OpenPage(pageUrl); });
        AddLabel("私有仓库访问", 18, 284, 610, 34, true);
        signedIn.Text = "允许使用本机 GitHub 登录检查更新（Git for Windows 或 GitHub CLI）";
        signedIn.Checked = UpdateCheck.UseSignedInAccount;
        Place(signedIn, 18, 329, 620, 32);
        signedIn.CheckedChanged += delegate {
            UpdateCheck.UseSignedInAccount = signedIn.Checked;
            StartCheck();
        };
        AddLabel("也可以单独保存此仓库的只读令牌。创建时选择 desk-audio-control 仓库，并将 Contents 设为 Read-only。",
                 19, 363, 612, 55, false);
        SetButton(access, "设置更新凭据…", 18, 421, 155, delegate { EditAccess(); });
        var create = new LinkLabel { Text = "在 GitHub 创建只读令牌", AutoSize = true };
        Place(create, 190, 428, 245, 25);
        create.LinkClicked += delegate { OpenPage(TokenCreationPage); };
    }

    public void Start()
    {
        timer.Interval = 5000;
        timer.Tick += delegate { timer.Interval = 60000; CheckIfDue(); };
        timer.Start();
    }

    void Place(Control control, int x, int y, int w, int h)
    {
        control.SetBounds(x, y, w, h);
        Controls.Add(control);
    }
    void AddLabel(string text, int x, int y, int w, int h, bool title)
    {
        var label = new Label { Text = text, TextAlign = ContentAlignment.MiddleLeft };
        if (title) label.Font = new Font(owner.Font.FontFamily, 12, FontStyle.Bold);
        Place(label, x, y, w, h);
    }
    void SetButton(Button button, string text, int x, int y, int w, Action action)
    {
        button.Text = text;
        button.UseVisualStyleBackColor = true;
        Place(button, x, y, w, 32);
        button.Click += delegate { action(); };
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
        status.Text = "正在向 GitHub 查询最新正式版…";
        try {
            UpdateResult result = await Task.Run(() => UpdateCheck.Check());
            if (IsDisposed || owner.IsDisposed) return;
            status.Text = result.Message;
            pageUrl = result.PageUrl ?? UpdateCheck.ReleasesPage;
            releases.Text = result.State == UpdateState.Available ? "查看新版本并下载" : "打开发布页";
            if (result.State == UpdateState.Available && automatic.Checked &&
                LocalSettings.Get("LastNotifiedUpdate") != result.Tag) {
                var tabs = Parent as TabControl;
                if (!owner.Visible || tabs == null || tabs.SelectedTab != this) notify(result.Tag);
                LocalSettings.Put("LastNotifiedUpdate", result.Tag);
            }
        } catch (Exception) {
            if (!IsDisposed) status.Text = "本次检查未完成，请稍后重试。";
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
        using (var dialog = new Form {
            Text = "更新凭据", Font = owner.Font, ClientSize = new Size(473, 189),
            FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false
        }) {
            var label = new Label { Text = UpdateCheck.HasSavedToken ? "已保存一枚令牌。输入新令牌可替换，也可移除。" : "粘贴此仓库的细粒度只读令牌。", AutoSize = false };
            label.SetBounds(16, 15, 438, 30);
            var input = new TextBox { UseSystemPasswordChar = true, MaxLength = 1024, AccessibleName = "GitHub 只读令牌" };
            input.SetBounds(16, 53, 438, 28);
            var hint = new Label { Text = "令牌由 Windows 加密保存在当前用户账户中。" };
            hint.SetBounds(16, 91, 438, 27);
            var clear = new Button { Text = "移除已存令牌", Enabled = UpdateCheck.HasSavedToken };
            clear.SetBounds(16, 137, 124, 31);
            var save = new Button { Text = "保存并检查", Enabled = false };
            save.SetBounds(219, 137, 122, 31);
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
            cancel.SetBounds(352, 137, 102, 31);
            input.TextChanged += delegate { save.Enabled = !String.IsNullOrWhiteSpace(input.Text); };
            clear.Click += delegate {
                try { UpdateCheck.ClearToken(); dialog.DialogResult = DialogResult.OK; }
                catch (Exception) { MessageBox.Show(dialog, "无法移除凭据。", "更新凭据"); }
            };
            save.Click += delegate {
                try { UpdateCheck.SaveToken(input.Text.Trim()); input.Clear(); dialog.DialogResult = DialogResult.OK; }
                catch (Exception) { MessageBox.Show(dialog, "保存失败，请检查令牌格式和当前用户的写入权限。", "更新凭据"); }
            };
            dialog.Controls.AddRange(new Control[] {label, input, hint, clear, save, cancel});
            dialog.AcceptButton = save; dialog.CancelButton = cancel;
            if (dialog.ShowDialog(owner) == DialogResult.OK) StartCheck();
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
