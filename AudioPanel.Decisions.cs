using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

internal sealed partial class AudioPanel
{
    void RecordManual(AudioItem port)
    {
        try { journal.Add(DecisionRecord.Create("手动",port.Name,"用户选择默认输出","manual")); }
        catch (Exception e) { Fail("切换成功，记录写入失败。 " + e.Message); }
    }

    void ConsiderRoute(ScanReason reasons, long since, List<SmartGroup> added)
    {
        string names = String.Join("、",added.Select(g => g.Name));
        var candidates = added.Where(g => Eligible(g,0) && SmartPolicy.Choices(g,0,false).Count > 0).ToList();
        var picks = candidates.Select(g => SmartPolicy.Pick(g,0,defaultOut)).Where(p => p != null).ToList();
        var evidence = new SwitchEvidence { Eligible = picks.Select(p => p.Id).ToArray(), DefaultOutput = defaultOut,
            Quiet = clock.ElapsedMilliseconds < quietUntil, CanRestore = true };
        try
        {
            string[] before = null;
            if (evidence.SkipReason() == null)
            {
                before = audio.Outputs();
                evidence.DefaultOutput = before[1];
                evidence.CanRestore = before.All(v => !String.IsNullOrEmpty(v));
                if (evidence.SkipReason() == null) evidence.InUse = CaptureOrCallActive(before[2]);
            }
            string reason = evidence.SkipReason();
            if (reason != null)
            {
                var skipped = DecisionRecord.Create(reasons.ToString(),names,reason,"skipped");
                skipped.Evidence = evidence;
                journal.Add(skipped);
                return;
            }
            var pick = picks[0];

            // Write the undo intent first. If the process exits between writes, undo still checks
            // all three current roles before restoring anything.
            var intent = DecisionRecord.Create(reasons.ToString(),names,"唯一符合用途的新增输出","prepared");
            intent.Evidence = evidence; intent.Before = before; intent.Target = pick.Id;
            journal.Add(intent);
            try { audio.RouteOutputs(new[] { pick.Id,pick.Id,pick.Id },before); }
            catch (Exception e)
            {
                var failure = DecisionRecord.Create(reasons.ToString(),names,e.Message,"failed");
                failure.Related = intent.Id;
                journal.Add(failure);
                throw;
            }
            defaultOut = pick.Id; quietUntil = clock.ElapsedMilliseconds + 30000;
            var done = DecisionRecord.Create(reasons.ToString(),names,intent.Reason,"applied");
            done.LatencyMs = (long)((System.Diagnostics.Stopwatch.GetTimestamp() - since) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            done.Evidence = evidence; done.Before = before; done.Target = pick.Id; done.Related = intent.Id;
            journal.Add(done);
            ShowRoute(pick.Name,pick.Id,true);
        }
        catch (Exception e) { Fail("自动切换 · " + e.Message); }
    }

    bool CaptureOrCallActive(string communicationOutput)
    {
        try
        {
            return groups.SelectMany(g => g.Inputs).Any(p => audio.InUse(p.Id)) ||
                audio.InUse(communicationOutput);
        }
        catch (Exception) { return true; }
    }

    void UndoRoute(string source)
    {
        var last = journal.LatestUndo();
        if (last == null) throw new InvalidOperationException("没有可以撤销的自动切换。");
        var result = DecisionRecord.Create(source,last.Candidates,"恢复切换前的三个默认角色","undone");
        try { audio.RouteOutputs(last.Before,new[] { last.Target,last.Target,last.Target }); }
        catch (Exception e)
        {
            journal.Add(DecisionRecord.Create(source,last.Candidates,e.Message,"undo-failed"));
            throw;
        }
        actionRevision++; quietUntil = clock.ElapsedMilliseconds + 30000; defaultOut = last.Before[1];
        result.Related = last.Id;
        journal.Add(result);
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

    static string Outcome(string value)
    {
        switch (value)
        {
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
        using (var dialog = new Form { Text = "决策记录", Font = Theme.Face(9f), ForeColor = Theme.Ink,
            BackColor = Theme.Window, StartPosition = FormStartPosition.CenterScreen,
            Size = new Size(P(900),P(420)), MinimumSize = new Size(P(580),P(300)) })
        {
            var list = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill,
                AccessibleName = "最近 30 天的决策记录", BackColor = Theme.Surface, ForeColor = Theme.Ink };
            list.Columns.Add("时间",P(120)); list.Columns.Add("结果",P(95)); list.Columns.Add("候选设备",P(155));
            list.Columns.Add("原因",P(260)); list.Columns.Add("触发源",P(145)); list.Columns.Add("耗时",P(75));
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = P(50), Padding = new Padding(P(5)) };
            Action refresh = delegate {
                list.Items.Clear();
                foreach (var r in Enumerable.Reverse(journal.Records()))
                    list.Items.Add(new ListViewItem(new[] { r.Utc.ToLocalTime().ToString("MM-dd HH:mm:ss"),
                        Outcome(r.Result),r.Candidates,r.Reason,r.Source,r.LatencyMs.HasValue ? r.LatencyMs + " ms" : "" }) { Tag = r });
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
            add("回放所选决策", delegate {
                if (list.SelectedItems.Count == 0) throw new InvalidOperationException("请先选择一条自动切换记录。");
                var record = list.SelectedItems[0].Tag as DecisionRecord;
                if (record == null || record.Evidence == null) throw new InvalidOperationException("该记录没有决策输入。");
                string replay = record.Evidence.SkipReason() ?? "唯一符合用途的新增输出";
                MessageBox.Show(dialog,replay,"离线回放");
            });
            add("导出…", delegate { using (var file = new SaveFileDialog { Filter = "JSON 文件|*.json", FileName = "DeskAudio-decisions.json" })
                if (file.ShowDialog(dialog) == DialogResult.OK) journal.Export(file.FileName); });
            add("导入…", delegate { using (var file = new OpenFileDialog { Filter = "JSON 文件|*.json" })
                if (file.ShowDialog(dialog) == DialogResult.OK) journal.Import(file.FileName); });
            add("清除记录…", delegate { if (MessageBox.Show(dialog,"清除本机决策记录和撤销历史？","决策记录",
                MessageBoxButtons.OKCancel) == DialogResult.OK) journal.Clear(); });
            dialog.Controls.Add(list); dialog.Controls.Add(bar);
            try { refresh(); }
            catch (Exception e) { list.Items.Add("记录无法读取 · " + e.Message); }
            dialog.ShowDialog(Visible ? this : null);
        }
    }
}
