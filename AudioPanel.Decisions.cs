using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

internal sealed partial class AudioPanel
{
    void UndoRoute(string source)
    {
        var last = journal.LatestUndo();
        if (last == null) throw new InvalidOperationException("没有可以撤销的自动切换。");
        var result = DecisionRecord.Create(source,last.Candidates,"恢复切换前的默认角色","undone");
        StopLanding();
        try { audio.Restore(last); }
        catch (Exception e)
        {
            journal.Add(DecisionRecord.Create(source,last.Candidates,e.Message,"undo-failed"));
            throw;
        }
        actionRevision++; quietUntil = clock.ElapsedMilliseconds + 30000; defaultOut = last.Before[1];
        expectedOutputs = last.Before;
        if (last.InputBefore != null) { expectedInputs = last.InputBefore; defaultIn = expectedInputs[1]; }
        pendingRoute = null;
        result.Scene = last.Scene; result.DeviceKey = last.DeviceKey; result.Related = last.Id;
        journal.Add(result);
        RollbackLevel(last.Target,last.Result=="prepared" ? last.Id : last.Related);
        bus.Publish(ScanReason.Refresh);
        ShowRoute(NameFor(defaultOut,0), defaultOut,false);
    }

    void ShowRoute(string name,string endpoint,bool canUndo)
    {
        if (RouteOverlay.FullScreen())
        {
            tray.ShowBalloonTip(1200,"桌面音频",name + (canUndo ? " · Ctrl + Alt + F9 撤销" : ""),ToolTipIcon.Info);
            return;
        }
        if (overlay != null) overlay.Dispose();
        int volume = -1;
        try { volume = AudioCore.Volume(endpoint); } catch (Exception) { }
        overlay = new RouteOverlay(name,volume,canUndo ? (Action)(() => Act(() => UndoRoute("屏幕提示"))) : null);
        overlay.Display();
    }

    // Scan reasons are stored by enum name; only the timeline translates them.
    static string SourceWord(string value)
    {
        switch (value)
        {
            case "Startup": return "启动";
            case "Added": return "设备接入";
            case "Removed": return "设备断开";
            case "State": return "端口状态变化";
            case "Default": return "默认设备变化";
            case "Property": return "设备属性变化";
            case "Poll": return "定时检查";
            case "Refresh": return "手动刷新";
            default: return value;
        }
    }
    static string SourceName(string value)
    {
        return String.Join("、",(value ?? "").Split(new[] { ", " },StringSplitOptions.RemoveEmptyEntries).Select(v => SourceWord(v)));
    }

    static string Outcome(string value)
    {
        switch (value)
        {
            case "tuned": return "声音调优";
            case "hint": return "仅提醒";
            case "offer": return "待确认的切换建议";
            case "scene": return "场景变化";
            case "connected": return "接入";
            case "removed": return "断开";
            case "corrected": return "手动纠正";
            case "input": return "输入变化";
            case "anomaly": return "设备异常";
            case "prepared": return "准备切换";
            case "applied": return "已自动切换";
            case "manual": return "手动切换";
            case "skipped": return "保留原设备";
            case "failed": return "切换失败";
            case "undone": return "已撤销";
            case "undo-failed": return "撤销失败";
            default: return value;
        }
    }

    void ShowDecisions()
    {
        using (var dialog = new Form { Text = "活动时间线", Font = Theme.Face(9f), ForeColor = Theme.Ink,
            BackColor = Theme.Window, StartPosition = FormStartPosition.CenterScreen,
            Size = new Size(P(940),P(420)), MinimumSize = new Size(P(580),P(300)) })
        {
            var list = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill,
                AccessibleName = "最近 30 天的活动时间线", ShowItemToolTips = true, HideSelection = false, BackColor = Theme.Surface, ForeColor = Theme.Ink };
            list.HandleCreated += delegate { Theme.Native(list, true); };
            // The system header keeps dark text in dark mode, so the header and the cells are drawn with the theme colors.
            list.OwnerDraw = true;
            list.DrawItem += delegate { };
            list.DrawColumnHeader += (o, h) => {
                using (var back = new SolidBrush(Theme.SurfaceHi)) h.Graphics.FillRectangle(back, h.Bounds);
                using (var line = new Pen(Theme.Border)) h.Graphics.DrawLine(line, h.Bounds.Left, h.Bounds.Bottom - 1, h.Bounds.Right, h.Bounds.Bottom - 1);
                TextRenderer.DrawText(h.Graphics, h.Header.Text, list.Font, new Rectangle(h.Bounds.X + P(6), h.Bounds.Y, Math.Max(0, h.Bounds.Width - P(8)), h.Bounds.Height), Theme.Ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            };
            list.DrawSubItem += (o, c) => {
                var box = c.Bounds;
                if (c.ColumnIndex == 0) box.Width = list.Columns[0].Width;
                using (var back = new SolidBrush(c.Item.Selected ? Theme.AccentSoft : Theme.Surface)) c.Graphics.FillRectangle(back, box);
                TextRenderer.DrawText(c.Graphics, c.SubItem.Text, list.Font, new Rectangle(box.X + P(6), box.Y, Math.Max(0, box.Width - P(8)), box.Height), Theme.Ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            };
            list.Columns.Add("时间",P(122)); list.Columns.Add("场景",P(60)); list.Columns.Add("结果",P(125)); list.Columns.Add("候选设备",P(150));
            list.Columns.Add("原因",P(228)); list.Columns.Add("触发源",P(105)); list.Columns.Add("耗时",P(70));
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = P(50), Padding = new Padding(P(5)) };
            var totals = new Label { Dock = DockStyle.Top, Height = P(36), Padding = new Padding(P(8)), ForeColor = Theme.Muted };
            Action refresh = delegate {
                var records = journal.Records();
                var week = records.Where(r => !r.Imported && r.Result == "applied" && r.Utc.ToUniversalTime() >= DateTime.UtcNow.AddDays(-7))
                    .Select(r => r.Id).ToList();
                var corrected = records.Where(r => !r.Imported && (r.Result == "undone" || r.Result == "corrected"))
                    .Select(r => r.Related).Intersect(week).Count();
                totals.Text = "近 7 天执行 " + week.Count + " 次，撤销或改选 " + corrected + " 次";
                string selected = list.SelectedItems.Count == 0 ? null : ((DecisionRecord)list.SelectedItems[0].Tag).Id;
                list.BeginUpdate();
                try {
                list.Items.Clear();
                foreach (var r in Enumerable.Reverse(records))
                    list.Items.Add(new ListViewItem(new[] { r.Utc.ToLocalTime().ToString("MM-dd HH:mm:ss"),
                        r.Scene ?? "",Outcome(r.Result),r.Candidates,r.Reason,SourceName(r.Source),r.LatencyMs.HasValue ? r.LatencyMs + " ms" : "" }) { Tag = r, ToolTipText = r.Reason }).Selected = r.Id == selected;
                } finally { list.EndUpdate(); }
            };
            Action<string,Action> add = (label, action) => {
                var button = new Button { Text = label, AutoSize = true, Height = P(30), UseVisualStyleBackColor = false, FlatStyle = FlatStyle.Flat, BackColor = Theme.SurfaceHi, ForeColor = Theme.Ink };
                button.Click += delegate {
                    try { action(); refresh(); }
                    catch (Exception e) { MessageBox.Show(dialog,e.Message,"决策记录"); }
                };
                bar.Controls.Add(button);
            };
            add("撤销自动切换", () => UndoRoute("决策记录"));
            add("查看 / 回放原因", delegate {
                if (list.SelectedItems.Count == 0) throw new InvalidOperationException("请先选择一条自动切换记录。");
                var record = list.SelectedItems[0].Tag as DecisionRecord;
                if (record.Evidence == null) { MessageBox.Show(dialog,record.Reason,"事件详情"); return; }
                string replay = record.Evidence.Explain();
                if (record.Evidence.Ranked != null)
                {
                    var e = record.Evidence;
                    replay += "\n\n活动播放会话 " + e.Sessions + " · 积极度 " + e.Sensitivity + "%";
                    foreach (var p in e.Ranked.Where(p => p != null))
                        replay += "\n" + p.Name + " · L" + p.Level + " · 偏好 " + p.Preference + " · 场景适配 " + p.SceneFit(e.Scene) +
                            (p.Recent ? " · 新接入" : "") + (p.Stereo ? " · 多声道" : "") + (p.RestoreStereo ? " · 避免蓝牙免提" : "") + " · 总分 " + p.Score(e) + (p.Presence==null ? "" : " · "+p.Presence);
                    if (e.ModelVersion>0) {
                        var best=e.Winner();
                        var incumbent=e.Ranked.FirstOrDefault(p=>p!=null && Same(p.Id,e.DefaultOutput));
                        if (best!=null && !Same(best.Id,e.DefaultOutput)) {
                            int lead=best.Score(e)-(incumbent==null ? 0 : incumbent.Score(e));
                            replay+="\n相对当前输出的模型概率 "+RouteFeedback.Probability(lead).ToString("P0")+
                                " · 胜出设备保留 "+best.Kept+" 次，纠正 "+best.Corrected+" 次，反馈修正 "+best.FeedbackOffset+" 分";
                        }
                        if (e.PresenceEvidence!=null) replay+="\n"+String.Join("\n",e.PresenceEvidence);
                    }
                    if (record.InputAfter != null) replay += "\n" + record.Reason;
                }
                if (record.ReasonCode != null) replay += "\n原因码 " + record.ReasonCode;
                var t = record.Timing;
                if (t != null) replay += "\n距通知（ms） · 开始扫描 " + t.ScanStartMs + " · 完成扫描 " + t.ScanEndMs +
                    " · 决策 " + t.DecisionMs + " · 写入 " + (t.WriteMs.HasValue ? t.WriteMs.ToString() : "—") +
                    " · 读回 " + (t.ReadbackMs.HasValue ? t.ReadbackMs.ToString() : "—");
                MessageBox.Show(dialog,replay,"离线回放");
            });
            add("导出…", delegate { using (var file = new SaveFileDialog { Filter = "JSON 文件|*.json", FileName = "DeskAudio-decisions.json" })
                if (file.ShowDialog(dialog) == DialogResult.OK) journal.Export(file.FileName); });
            add("导入…", delegate { using (var file = new OpenFileDialog { Filter = "JSON 文件|*.json" })
                if (file.ShowDialog(dialog) == DialogResult.OK) journal.Import(file.FileName); });
            add("清除记录…", delegate { if (MessageBox.Show(dialog,"清除本机决策记录和撤销历史？","决策记录",
                MessageBoxButtons.OKCancel) == DialogResult.OK) journal.Clear(); });
            dialog.Controls.Add(list); dialog.Controls.Add(totals); dialog.Controls.Add(bar);
            try { refresh(); }
            catch (Exception e) { list.Items.Add("记录无法读取 · " + e.Message); }
            bool queued = false;
            Action onChange = delegate {
                if (queued || dialog.IsDisposed || !dialog.IsHandleCreated) return;
                queued = true;
                dialog.BeginInvoke((Action)delegate {
                    queued = false;
                    if (dialog.IsDisposed || !dialog.Visible) return;
                    try { refresh(); } catch (Exception error) { Fail(error.Message); }
                });
            };
            journal.Changed += onChange;
            try { dialog.ShowDialog(Visible ? this : null); }
            finally { journal.Changed -= onChange; }
        }
    }
}
