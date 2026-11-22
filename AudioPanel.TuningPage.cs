using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed partial class AudioPanel
{
    sealed class TuneEndpoint
    {
        public SmartGroup Group;
        public AudioItem Port;
        public override string ToString() { return (Port.Name??"").IndexOf(Group.Name,StringComparison.OrdinalIgnoreCase)>=0 ? Port.Name : Group.Name+" · "+Port.Name; }
    }
    readonly string[] tuningTabs={"等响与柔着陆","麦克风医生","EQ 推荐"};
    readonly PillButton[] tuningNav=new PillButton[3];
    readonly Card[] tuneCards=new Card[3];
    readonly ComboBox outputBox=new ComboBox(), inputBox=new ComboBox(), modelBox=new ComboBox();
    Caption tuningTitle,portLabel,tuneFeedback,comfortState,doctorText,modelStatus;
    PillButton referenceButton,learnButton,forgetButton,restoreOutput,diagnoseButton,repairButton,restoreInput,listenA,listenB,exportEq,stopAudio,pickModel,portButton;
    ToggleSwitch enableLanding;
    EqTable filterList;
    FlatSlider strength,phoneCap;
    Caption strengthLabel,capLabel,apoStatus;
    PillButton applyEq,restoreEq,measureEq;
    TuningStream.BandReading dryBands;
    string dryEndpoint;
    int dryLevel,dryRate;
    EqPlan eqPlan;
    EqPreset plannedPreset;
    EqCandidate[] suggested=new EqCandidate[0];
    string eqMatchReason,apoApplied,apoError;
    ApoBridge apo;
    bool apoManaged;
    int tuneTab;
    Panel tunePage;
    bool filling;
    EqPreset[] presets;
    MicReport diagnosis;
    string diagnosedPort;
    int diagnosedLevel;
    CancellationTokenSource tuningJob;

    TuneEndpoint TuneOut { get { return outputBox.SelectedItem as TuneEndpoint; } }
    TuneEndpoint TuneIn { get { return inputBox.SelectedItem as TuneEndpoint; } }
    EqPreset Preset { get { return modelBox.SelectedItem as EqPreset; } }
    PillButton TuneButton(Control parent,string label,Action action)
    {
        var button=new PillButton { Text=label,AccessibleName=label,Kind=ButtonLook.Soft };
        button.Click+=delegate {
            try { action(); } catch (Exception e) { TuneMessage(e.Message,true); }
            RefreshTuneActions();
        };
        parent.Controls.Add(button); return button;
    }
    void TuneMessage(string message,bool error=false)
    {
        tuneFeedback.Text=message; tuneFeedback.Role=error ? Tone.Warn : Tone.Good;
    }
    static void AddBox(Control parent,ComboBox box,string name)
    {
        box.DropDownStyle=ComboBoxStyle.DropDownList; box.AccessibleName=name;
        box.Font=Theme.Face(10f); parent.Controls.Add(box);
    }
    void BuildTuning(Panel page)
    {
        tunePage=page; page.AutoScroll=true;
        tuningTitle=Cap(page,"声音调优",14f,true,Tone.Ink);
        for(int i=0;i<3;i++) {
            int tab=i; tuningNav[i]=TuneButton(page,tuningTabs[i],()=>SelectTuneTab(tab));
            tuneCards[i]=new Card(); page.Controls.Add(tuneCards[i]);
        }
        portLabel=Cap(page,"播放设备",10f,true,Tone.Ink);
        AddBox(page,outputBox,"调优播放端口"); AddBox(page,inputBox,"诊断录音端口");
        outputBox.Visible=inputBox.Visible=false; // The boxes keep the selection; portButton is what the user sees.
        portButton=TuneButton(page,"选择端口",ChoosePort);
        tuneFeedback=Cap(page,"",9f,false,Tone.Muted); tuneFeedback.Wrap=true;
        var landing=tuneCards[0];
        enableLanding=Switch(landing,"切换时匹配舒适音量并为耳机淡入",tuneEnabled,on=>{
            try {
                LocalSettings.Put("EnableTuning",on ? "1" : "0"); tuneEnabled=on;
                if (!on) { StopLanding(); learnPort=null; }
                TuneMessage(on ? "已启用，下次由 DeskAudio 切换输出时生效" : "已关闭自动调音，可单独还原上次修改");
            } catch (Exception e) { enableLanding.Set(tuneEnabled); TuneMessage(e.Message,true); }
        });
        comfortState=Cap(landing,"",10f,false,Tone.Ink); comfortState.Wrap=true;
        comfortState.TextChanged+=delegate { LayoutTuning(page); };
        referenceButton=TuneButton(landing,"播放参考音",()=>StartTuningJob(false,false));
        learnButton=TuneButton(landing,"记住当前音量",delegate {
            var p=TuneOut;
            if (ramp!=null || audio.Muted(p.Port.Id) || audio.Level(p.Port.Id)==0) throw new InvalidOperationException("请在淡入结束后选择非静音的舒适音量");
            RememberLevel(p.Group.Key,p.Port.Id,audio.Level(p.Port.Id)); learnPort=null; TuneMessage("已记录该设备的舒适音量");
        });
        forgetButton=TuneButton(landing,"清除舒适记录",delegate {
            levels.Forget(TuneOut.Group.Key); learnPort=null; TuneMessage("已清除该设备的舒适记录");
        });
        phoneCap=new FlatSlider { Minimum=1,Maximum=60,AccessibleName="耳机音量上限" };
        landing.Controls.Add(phoneCap); capLabel=Cap(landing,"",9f,false,Tone.Muted);
        phoneCap.ValueChanged+=delegate { if (TuneOut!=null) {
            LocalSettings.Put("HeadphoneCeiling/"+TuneOut.Group.Key,phoneCap.Value.ToString());
            capLabel.Text="耳机上限 "+phoneCap.Value+"%";
        } };
        restoreOutput=TuneButton(landing,"还原上次音量",()=>RestoreTuned(TuneOut));

        var mic=tuneCards[1];
        doctorText=Cap(mic,"",10f,false,Tone.Ink); doctorText.Wrap=true; doctorText.TopAligned=true;
        diagnoseButton=TuneButton(mic,"开始话筒检查",()=>StartTuningJob(true,false));
        repairButton=TuneButton(mic,"调整输入音量",ApplyMicFix);
        restoreInput=TuneButton(mic,"还原输入音量",()=>RestoreTuned(TuneIn));

        var eq=tuneCards[2];
        modelStatus=Cap(eq,"",9f,false,Tone.Muted); modelStatus.Wrap=true;
        AddBox(eq,modelBox,"测量预设型号"); modelBox.Visible=false; // Keeps the selection; ModelPicker is what the user sees.
        pickModel=TuneButton(eq,"选择实物型号…",ChooseModel);
        presets=EqPreset.Load();
        modelBox.Items.Add("选择实物型号…"); modelBox.Items.AddRange(presets);
        modelBox.SelectedIndexChanged+=delegate {
            if (filling) return;
            CancelTuningJob();
            try {
                if (TuneOut!=null) LocalSettings.Put("EqModel/"+TuneOut.Group.Key+"/"+TuneOut.Port.Id,Preset==null ? "-" : Preset.Model);
                eqMatchReason="手动选择";
            } catch (Exception error) { TuneMessage(error.Message,true); }
            RefreshTuneActions();
        };
        filterList=new EqTable();
        eq.Controls.Add(filterList);
        listenA=TuneButton(eq,"试听 A · 原声",()=>StartTuningJob(false,false));
        listenB=TuneButton(eq,"试听 B · EQ",()=>StartTuningJob(false,true));
        exportEq=TuneButton(eq,"导出参数…",ExportPreset);
        strength=new FlatSlider { AccessibleName="EQ 强度" }; eq.Controls.Add(strength);
        strengthLabel=Cap(eq,"",9f,true,Tone.Ink);
        strength.ValueChanged+=delegate {
            if (TuneOut==null) return;
            LocalSettings.Put("EqStrength/"+TuneOut.Group.Key,strength.Value.ToString());
            RefreshTuneActions();
        };
        apoStatus=Cap(eq,"",9f,false,Tone.Muted); apoStatus.Wrap=true;
        applyEq=TuneButton(eq,"应用到此端口",()=>ChangeEq(true));
        restoreEq=TuneButton(eq,"还原 EQ",()=>ChangeEq(false));
        measureEq=TuneButton(eq,"测量参考音",MeasureEq);
        stopAudio=TuneButton(page,"停止",CancelTuningJob);
        outputBox.SelectedIndexChanged+=delegate { if (!filling) { CancelTuningJob(); MatchPreset(); ReadApoStatus(); RefreshTuneActions(); } };
        inputBox.SelectedIndexChanged+=delegate { if (!filling) { CancelTuningJob(); diagnosis=null; RefreshTuneActions(); } };
        SelectTuneTab(0);
    }
    void SelectTuneTab(int tab)
    {
        if (tuneTab!=tab) CancelTuningJob();
        if (tab==2) ReadApoStatus();
        tuneTab=tab; if (tunePage!=null) LayoutTuning(tunePage);
        for(int i=0;i<3;i++) { tuneCards[i].Visible=i==tab; tuningNav[i].On=i==tab; }
        portLabel.Text=tab==1 ? "录音设备" : "播放设备";
        TuneMessage(""); RefreshTuneActions();
    }
    void LayoutTuning(Panel page)
    {
        // Stack the controls on the same scrollable canvas across all three views.
        int x=P(28), w=Math.Max(P(360),page.ClientSize.Width-2*x), m=P(18);
        var scroll=page.AutoScrollPosition;
        x+=scroll.X; int y=scroll.Y;
        tuningTitle.SetBounds(x,y+P(18),w,P(36));
        for(int i=0;i<3;i++) tuningNav[i].SetBounds(x+i*P(145),y+P(64),P(138),P(36));
        portLabel.SetBounds(x,y+P(120),P(84),P(30));
        foreach(var box in new[]{outputBox,inputBox}) box.SetBounds(x+P(88),y+P(120),w-P(88),P(32));
        portButton.SetBounds(x+P(88),y+P(120),w-P(88),P(32));
        int tableH=filterList.Wanted(10), eqH=P(130)+tableH+P(160);
        int iw=w-2*m;
        int cardTop=y+P(166), rowGap=P(16);
        enableLanding.SetBounds(m,P(16),iw,P(36));
        int comfortHeight=Theme.MeasureWrapped(comfortState.Text,Theme.Face(comfortState.Pt),iw)+P(2);
        comfortState.SetBounds(m,enableLanding.Bottom+rowGap,iw,comfortHeight);
        capLabel.SetBounds(m,comfortState.Bottom+rowGap,P(120),P(32));
        phoneCap.SetBounds(capLabel.Right+P(6),capLabel.Top,iw-capLabel.Width-P(6),capLabel.Height);
        var actions=new[]{referenceButton,learnButton,forgetButton,restoreOutput};
        int bw=Math.Min(P(140),(iw-3*P(8))/4);
        for (int i=0;i<actions.Length;i++) actions[i].SetBounds(m+i*(bw+P(8)),phoneCap.Bottom+rowGap,bw,P(36));
        tuneCards[0].SetBounds(x,cardTop,w,restoreOutput.Bottom+m);
        tuneCards[1].SetBounds(x,cardTop,w,P(274));
        tuneCards[2].SetBounds(x,cardTop,w,eqH);
        doctorText.SetBounds(m,P(24),iw,P(172));
        diagnoseButton.SetBounds(m,P(207),P(140),P(36)); repairButton.SetBounds(m+P(150),P(207),P(164),P(36));
        restoreInput.SetBounds(m+P(324),P(207),P(136),P(36));
        modelStatus.SetBounds(m,P(6),iw,P(40)); pickModel.SetBounds(m,P(48),iw,P(32));
        strengthLabel.SetBounds(m,P(84),P(104),P(34)); strength.SetBounds(m+P(110),P(84),iw-P(110),P(34));
        filterList.SetBounds(m,P(130),iw,tableH);
        int by=P(130)+tableH+P(8);
        apoStatus.SetBounds(m,by+P(40),iw,P(44));
        applyEq.SetBounds(m,by+P(90),P(150),P(36)); restoreEq.SetBounds(m+P(156),by+P(90),P(120),P(36));
        measureEq.SetBounds(m+P(282),by+P(90),P(136),P(36));
        listenA.SetBounds(m,by,P(132),P(36)); listenB.SetBounds(m+P(142),by,P(132),P(36));
        exportEq.SetBounds(m+P(284),by,P(132),P(36));
        int below=tuneCards[tuneTab].Bottom+P(7);
        tuneFeedback.SetBounds(x,below,w-P(100),tuneTab==2 ? P(40) : P(56));
        stopAudio.SetBounds(x+w-P(86),below+P(9),P(86),P(34));
    }
    void RefreshTuningPage()
    {
        if (presets==null || rail.Index!=TunePageIndex) return;
        filling=true;
        try {
            var priorOut=TuneOut; var priorIn=TuneIn;
            FillPorts(outputBox,0,defaultOut); FillPorts(inputBox,1,defaultIn);
            if (!SameEndpoint(priorOut,TuneOut)) { CancelTuningJob(); MatchPreset(); }
            if (!SameEndpoint(priorIn,TuneIn)) { CancelTuningJob(); diagnosis=null; }
        } finally { filling=false; }
        if (tuneTab==2) ReadApoStatus();
        RefreshTuneActions();
    }
    static bool SameEndpoint(TuneEndpoint a,TuneEndpoint b)
    {
        return a==null || b==null ? a==b : Same(a.Port.Id,b.Port.Id) && a.Group.Key==b.Group.Key;
    }
    void FillPorts(ComboBox box,int flow,string current)
    {
        var entries=groups.Where(g=>!g.Virtual).SelectMany(g=>(flow==0 ? g.Outputs : g.Inputs)
            .Select(p=>new TuneEndpoint { Group=g,Port=p })).ToArray();
        if (entries.Select(p=>p.Port.Id+p.Group.Key+p.Port.MixRate+p.ToString()).SequenceEqual(box.Items.Cast<TuneEndpoint>().Select(p=>p.Port.Id+p.Group.Key+p.Port.MixRate+p.ToString()))) return;
        var old=box.SelectedItem as TuneEndpoint;
        box.Items.Clear(); box.Items.AddRange(entries);
        box.SelectedItem=entries.FirstOrDefault(p=>SameEndpoint(p,old)) ?? entries.FirstOrDefault(p=>Same(p.Port.Id,current)) ?? entries.FirstOrDefault();
    }
    void MatchPreset()
    {
        var p=TuneOut;
        var saved=p==null ? null : LocalSettings.Get("EqModel/"+p.Group.Key+"/"+p.Port.Id);
        var legacy=p==null ? null : LocalSettings.Get("EqModel/"+p.Group.Key);
        suggested=p==null ? new EqCandidate[0] : EqMatcher.Rank(presets,p.Group.Name,p.Port.Name,p.Port.Bus);
        var match=saved==null ? EqMatcher.Unique(suggested) : presets.FirstOrDefault(eq=>eq.Model==saved);
        eqMatchReason=saved==null && match!=null ? suggested[0].Reason : "该端口已选型号";
        if (saved==null && match==null && legacy!=null) {
            // A pre-v1.5 choice identified the DAC, not necessarily the current headphone.
            var oldModel=presets.FirstOrDefault(eq=>eq.Model==legacy);
            if (oldModel!=null) suggested=new[]{new EqCandidate { Preset=oldModel,Reason="旧设备记录，请确认" }}.Concat(suggested).ToArray();
        }
        if (strength!=null) strength.Set(p==null ? 100 : ReadNumber("EqStrength/"+p.Group.Key,100,0,100));
        bool old=filling; filling=true;
        try { modelBox.SelectedIndex=match==null ? 0 : Array.IndexOf(presets,match)+1; }
        finally { filling=old; }
    }
    void RefreshTuneActions()
    {
        if (stopAudio==null) return;
        bool idle=tuningJob==null;
        var output=TuneOut; var input=TuneIn;
        stopAudio.Visible=!idle;
        referenceButton.Enabled=learnButton.Enabled=idle && output!=null;
        int[] history=output==null ? new int[0] : levels.History(output.Group.Key);
        comfortState.Text=(history.Length==0 ? "尚无舒适音量记录。" :
            "已记录 "+history.Length+" 次，舒适中位值 "+Landing.Percentile(history,.5)+"%。")+
            "\n用同一参考音找到舒适响度，再记住当前音量。启用后也会学习稳定的手动音量调整。\n耳机初次切换上限 35%，积累足够的同一时段记录后按高分位限制，最高 60%。";
        forgetButton.Enabled=idle && history.Length>0;
        restoreOutput.Enabled=idle && output!=null && levels.Saved(output.Port.Id)!=null;
        diagnoseButton.Enabled=idle && input!=null;
        doctorText.Text=diagnosis==null ? "先保持安静，出现说话提示后，以平常音量说话 3.5 秒。\n\n检查电平、削波、底噪、50/60 Hz 嗡声和回声风险。采样仅在内存中分析。"
            : diagnosis.Describe();
        repairButton.Enabled=idle && input!=null && diagnosis!=null && diagnosedPort==input.Port.Id && diagnosis.GainDb!=0;
        repairButton.Text=repairButton.Enabled ? "输入音量 "+diagnosis.GainDb.ToString("+0.#;-0.#")+" dB" : "调整输入音量";
        restoreInput.Enabled=idle && input!=null && levels.Saved(input.Port.Id)!=null;
        phoneCap.Enabled=output!=null && idle;
        phoneCap.Set(output==null ? 60 : ReadNumber("HeadphoneCeiling/"+output.Group.Key,60,1,60));
        capLabel.Text="耳机上限 "+phoneCap.Value+"%";
        int rate=output==null || output.Port.MixRate<=0 ? EqPreset.Rate : output.Port.MixRate;
        bool supported=Preset!=null && EqPlanner.Supported(Preset,rate);
        if (!supported) eqPlan=null;
        else if (Preset!=plannedPreset || eqPlan==null || eqPlan.Strength!=strength.Value || eqPlan.Rate!=rate)
            eqPlan=EqPlanner.Plan(Preset,strength.Value,rate);
        plannedPreset=Preset;
        strength.Enabled=idle && supported; strengthLabel.Text="强度 "+strength.Value+"%";
        listenA.Enabled=listenB.Enabled=idle && eqPlan!=null && output!=null && apoApplied==null && apoError==null;
        exportEq.Enabled=eqPlan!=null;
        applyEq.Enabled=idle && eqPlan!=null && apo!=null && apoError==null && output!=null && output.Port.MixRate>0;
        restoreEq.Enabled=idle && apo!=null && apoError==null && apoManaged;
        bool matches=eqPlan!=null && apoApplied==eqPlan.Filter.Model+" · "+eqPlan.Strength+"%";
        measureEq.Enabled=idle && output!=null && apo!=null && apoError==null && supported &&
            (apoApplied==null || (matches && dryBands!=null && dryEndpoint==output.Port.Id));
        measureEq.Text=apoApplied==null ? "测量应用前" : "测量应用后";
        apoStatus.Text=apoError ?? (apo==null ? "未找到 Equalizer APO，可试听或导出。" :
            (apoApplied==null ? "尚未应用。" : "已写入配置 · "+apoApplied+"。")+" 更换耳机请确认型号。端口需在 APO 中注册，独占播放会绕过系统效果。");
        var shownPort=tuneTab==1 ? input : output;
        portButton.Enabled=idle && shownPort!=null;
        portButton.Text=shownPort==null ? (tuneTab==1 ? "没有检测到录音设备" : "没有检测到播放设备") : shownPort+" · 更换…";
        portButton.AccessibleName=(tuneTab==1 ? "录音端口" : "播放端口")+(shownPort==null ? "，无可用端口" : "，当前 "+shownPort+"，点击更换");
        pickModel.Enabled=idle;
        pickModel.Text=Preset==null ? "选择实物型号…" : Preset.Model+" · 更换…";
        pickModel.AccessibleName=Preset==null ? "选择实物型号" : "已选型号 "+Preset.Model+"，点击更换";
        filterList.Bind(eqPlan==null ? null : eqPlan.Filter);
        modelStatus.Text=Preset==null ? (suggested.Length==0 ? "请选择实物型号。" : "请确认型号 · "+suggested[0].Preset.Model+" · "+suggested[0].Reason) :
            eqPlan==null ? "当前端口采样率不足以应用该预设" : eqMatchReason+" · "+rate+" Hz · 预设频响余量 "+eqPlan.HeadroomDb.ToString("F1")+" dB";
    }
    async void MeasureEq()
    {
        if (tuningJob!=null || ramp!=null || TuneOut==null) return;
        var output=TuneOut; var plan=eqPlan; string id=output.Port.Id;
        var job=new CancellationTokenSource(); tuningJob=job; pendingRoute=null;
        try {
            ReadApoStatus();
            if (apoError!=null || apo==null || plan==null) throw new InvalidOperationException(apoError ?? "请先选择可以应用的预设");
            string applied=apoApplied;
            int level=audio.Level(id), rate=output.Port.MixRate;
            if (audio.Muted(id) || level==0) throw new InvalidOperationException("端口静音或音量为零");
            if (applied!=null && (dryBands==null || dryEndpoint!=id || dryLevel!=level || dryRate!=rate ||
                applied!=plan.Filter.Model+" · "+plan.Strength+"%"))
                throw new InvalidOperationException("请在同一端口、音量和采样率下先测量应用前的参考音");
            RefreshTuneActions(); TuneMessage("正在播放并测量低电平参考音…");
            var reading=await Task.Run(()=>TuningStream.MeasureBands(id,audio.TuningGuard,job.Token));
            job.Token.ThrowIfCancellationRequested(); ReadApoStatus();
            if (apoError!=null || apoApplied!=applied || audio.Level(id)!=level || audio.Muted(id) ||
                TuneOut==null || TuneOut.Port.MixRate!=rate || !Same(TuneOut.Port.Id,id))
                throw new InvalidOperationException("测量期间端口、音量或配置已变化，请重测");
            if (applied==null) {
                dryBands=reading; dryEndpoint=id; dryLevel=level; dryRate=rate;
                TuneMessage("已记录应用前的频带能量。应用 EQ 后可测量同一参考音。");
            } else TuneMessage(dryBands.Compare(reading,plan));
        } catch (OperationCanceledException) { if (!IsDisposed) TuneMessage("已停止"); }
        catch (Exception error) { if (!IsDisposed) TuneMessage(error.Message,true); }
        finally { tuningJob=null; job.Dispose(); if (!IsDisposed) RefreshTuneActions(); }
    }
    void ReadApoStatus()
    {
        apoApplied=null; apoError=null; apoManaged=false;
        try {
            apo=ApoBridge.Detect();
            if (apo!=null && TuneOut!=null) apoApplied=apo.Applied(TuneOut.Port.Id,out apoManaged);
        } catch (Exception error) { apoError=error.Message; }
    }
    void ChangeEq(bool apply)
    {
        var output=TuneOut;
        if (output==null || apo==null || (apply && eqPlan==null)) return;
        try {
            apo.Change(output.Port.Id,apply ? eqPlan : null,RequireTuningIdle);
            string result=apply ? eqPlan.Filter.Model+" · "+eqPlan.Strength+"%" : null;
            LocalSettings.Put("EqApplied/"+output.Port.Id,result);
            Activity("EQ",output.Group.Name,apply ? "已写入端口配置 · "+result : "已还原端口配置","tuned");
            TuneMessage(apply ? "已将预设写入 Equalizer APO" : "已还原 EQ 配置");
        } finally { ReadApoStatus(); }
    }
    void RestoreTuned(TuneEndpoint port)
    {
        if (port==null) return;
        RequireTuningIdle(); StopLanding(); learnPort=null;
        var tx=levels.Saved(port.Port.Id);
        if (tx!=null && levels.Restore(port.Port.Id,tx.Owner)) { diagnosis=null; TuneMessage("已还原 "+port.Group.Name+" 的音量"); }
    }
    void RequireTuningIdle()
    {
        string reason=audio.TuningGuard();
        if (reason!=null) throw new InvalidOperationException(reason);
    }
    void ApplyMicFix()
    {
        var p=TuneIn; RequireTuningIdle();
        if (p==null || diagnosis==null || p.Port.Id!=diagnosedPort || audio.Level(p.Port.Id)!=diagnosedLevel || audio.Muted(p.Port.Id))
            throw new InvalidOperationException("输入或音量已变化，请重新检查");
        int target=MicAdvisor.Target(diagnosis,diagnosedLevel,DeviceGain(p.Port.Id));
        if (target==diagnosedLevel) throw new InvalidOperationException("已到音量边界，请调整话筒距离后重测");
        string owner=Guid.NewGuid().ToString("N");
        try { levels.Begin(p.Port.Id,owner,target,diagnosedLevel); } catch { RollbackLevel(p.Port.Id,owner); throw; }
        diagnosis=null; TuneMessage("输入音量已调整至 "+target+"%，可重新检查或还原");
        Activity("麦克风医生",p.Group.Name,"按诊断调整输入音量","tuned");
    }
    void CancelTuningJob() { if (tuningJob!=null) tuningJob.Cancel(); }
    async void StartTuningJob(bool mic,bool filtered)
    {
        if (tuningJob!=null) return;
        var job=new CancellationTokenSource(); tuningJob=job; pendingRoute=null;
        try {
            var port=mic ? TuneIn : TuneOut;
            if (port==null) throw new InvalidOperationException("请先选择端口");
            string id=port.Port.Id;
            if (audio.Muted(id)) throw new InvalidOperationException("端口处于静音状态，请先检查静音设置");
            if (ramp!=null) throw new InvalidOperationException("请等淡入结束后再开始");
            RefreshTuneActions();
            if (mic) {
                diagnosis=null; diagnosedLevel=audio.Level(id); string playback=audio.Default(0,1);
                TuneMessage("请保持安静，正在检查输入…");
                IProgress<int> progress=new Progress<int>(n=>{
                    if (tuningJob==job && !job.IsCancellationRequested && !IsDisposed)
                        TuneMessage(n<30 ? "请保持安静…" : "请以平常音量说话… "+n+"%");
                });
                var report=await Task.Run(()=>TuningStream.Diagnose(id,playback,audio.TuningGuard,progress.Report,job.Token));
                job.Token.ThrowIfCancellationRequested();
                diagnosedPort=id; diagnosis=report; TuneMessage("检查完成");
            } else {
                var preset=eqPlan==null ? null : eqPlan.Filter;
                bool reference=tuneTab==0;
                if (!reference && preset==null) throw new InvalidOperationException("请先选择测量预设");
                TuneMessage(reference ? "正在播放 2 秒参考音" : filtered ? "正在试听 B · 推荐 EQ" : "正在试听 A · 原声");
                await Task.Run(()=>TuningStream.Audition(id,reference ? EqPreset.Reference() : preset.Preview(filtered),audio.TuningGuard,job.Token));
                job.Token.ThrowIfCancellationRequested(); TuneMessage("试听结束");
            }
        } catch (OperationCanceledException) { if (!IsDisposed) TuneMessage("已停止"); }
        catch (Exception e) { if (!IsDisposed) TuneMessage("未完成 · "+e.Message,true); }
        finally { tuningJob=null; job.Dispose(); if (!IsDisposed) { quietUntil=clock.ElapsedMilliseconds+30000; RefreshTuneActions(); } }
    }
    void ChoosePort()
    {
        var box=tuneTab==1 ? inputBox : outputBox;
        var items=box.Items.Cast<TuneEndpoint>().ToList();
        if (items.Count==0) return;
        // The hidden box keeps the selection, so its change handlers still cancel jobs and refresh the page.
        ThemedMenu.Choose(portButton,items.Select(p=>p.ToString()).ToList(),box.SelectedIndex,n=>box.SelectedIndex=n);
    }
    void ChooseModel()
    {
        var candidates=suggested.Select(c=>c.Preset).Distinct().ToArray();
        var ordered=candidates.Concat(presets.Except(candidates)).ToArray();
        using (var picker=new ModelPicker(ordered,Preset)) {
            if (picker.ShowDialog(this)!=DialogResult.OK) return;
            CancelTuningJob();
            // The hidden box still owns the selection, so saving and matching run through its handler.
            if (TuneOut!=null) LocalSettings.Put("EqModel/"+TuneOut.Group.Key+"/"+TuneOut.Port.Id,picker.Cleared ? "-" : picker.Picked.Model);
            modelBox.SelectedIndex=picker.Cleared || picker.Picked==null ? 0 : Array.IndexOf(presets,picker.Picked)+1;
        }
    }
    void ExportPreset()
    {
        if (eqPlan==null) return;
        using (var dialog=new SaveFileDialog { Filter="Equalizer APO 参数|*.txt",FileName=Preset.Model+" ParametricEQ.txt" })
            if (dialog.ShowDialog(this)==DialogResult.OK) { File.WriteAllText(dialog.FileName,eqPlan.Filter.Export()); TuneMessage("已导出预设，可在外部均衡器中加载"); }
    }
}
