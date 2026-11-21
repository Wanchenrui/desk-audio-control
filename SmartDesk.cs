using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class LocalSettings
{
    public const string Key=@"Software\DeskAudioControl", Run=@"Software\Microsoft\Windows\CurrentVersion\Run", Name="DeskAudioControl";
    public static string Get(string name) { using(var k=Registry.CurrentUser.OpenSubKey(Key)) return k==null?null:k.GetValue(name) as string; }
    public static void Put(string name,string value) { using(var k=Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name,value); }
    public static bool Startup {
        get { using(var k=Registry.CurrentUser.OpenSubKey(Run)) return k!=null && k.GetValue(Name)!=null; }
        set { using(var k=Registry.CurrentUser.CreateSubKey(Run)) { if(value) k.SetValue(Name,"\""+Application.ExecutablePath+"\" --tray"); else k.DeleteValue(Name,false); } }
    }
    public static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),Name); } }
    public static void Log(string message) { try { Directory.CreateDirectory(Folder); File.WriteAllText(Path.Combine(Folder,"session.txt"),DateTime.Now.ToString("s")+" "+message,Encoding.UTF8); } catch(IOException) {} catch(UnauthorizedAccessException) {} }
}

internal sealed class PortChoice
{
    public AudioItem Device;
    public override string ToString() { return Device.Name; }
}
// A jack identifies the socket, since a passive analog headset supplies no model ID.
// Promote a newly sensed plug for the duration of this connection.
internal sealed class PlugChanges
{
    HashSet<string> previous;
    readonly HashSet<string> inserted=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public void Observe(List<SmartGroup> devices)
    {
        var current=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var g in devices){
            current.UnionWith(g.SensedOutputs);current.UnionWith(g.SensedInputs);
            if(g.Kind=="显示器音频")current.UnionWith(g.Outputs.Select(p=>p.Id));
        }
        inserted.IntersectWith(current);
        if(previous!=null)inserted.UnionWith(current.Where(id=>!previous.Contains(id)));
        previous=current;
    }
    public bool Recent(SmartGroup g,int flow){return (flow==0?g.Outputs:g.Inputs).Any(p=>inserted.Contains(p.Id));}
}
internal sealed class AudioPanel : Form
{
    [DllImport("user32.dll",SetLastError=true)] static extern bool RegisterHotKey(IntPtr h,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h,int id);
    readonly EventWaitHandle openEvent;
    readonly EndpointEvents events = new EndpointEvents();
    readonly PlugChanges plugs = new PlugChanges();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ListView hardware = new ListView();
    readonly CheckBox showAll = new CheckBox();
    readonly Label counts=new Label(), status=new Label(), shortcuts=new Label(), detail=new Label();
    readonly ComboBox role=new ComboBox();
    readonly string[] modes={"auto","speaker","headphones","microphone","duplex","show","hide"};
    readonly string[] modeText={"自动识别","音箱（使用输出）","耳机（使用输出）","话筒（使用输入）","耳麦（输入和输出）","加入日常设备","收进其他接口"};
    readonly ComboBox[] ports={new ComboBox(),new ComboBox()};
    readonly TrackBar[] volume={new TrackBar(),new TrackBar()};
    readonly Label[] level={new Label(),new Label()};
    readonly Button[] route=new Button[2], mute=new Button[2];
    Button fx,balance;
    List<SmartGroup> groups=new List<SmartGroup>();
    HashSet<string> previousOutputs;
    string defaultOut,defaultIn,listSignature,portSignature;
    bool busy,closing,hotOut,hotMic,preferNew;
    long nextScan,nextView;
    readonly Stopwatch clock=Stopwatch.StartNew();
    readonly Color teal=Color.FromArgb(0,99,93);
    readonly Color textColor=Color.FromArgb(29,49,62);
    bool tooltipEditing;

    public AudioPanel(bool hidden,EventWaitHandle open)
    {
        openEvent=open; preferNew=LocalSettings.Get("PreferNewOutput")=="1";
        Text="桌面音频"; Font=new Font("Microsoft YaHei UI",9); ForeColor=textColor; Icon=DeskIcon.Create();
        AutoScaleDimensions=new SizeF(96,96); AutoScaleMode=AutoScaleMode.Dpi;
        ClientSize=new Size(678,575); FormBorderStyle=FormBorderStyle.FixedSingle; MaximizeBox=false; StartPosition=FormStartPosition.Manual;
        Screen target=Screen.AllScreens.FirstOrDefault(s=>!s.Primary)??Screen.PrimaryScreen;
        var area=target.WorkingArea; Location=new Point(Math.Max(area.X,area.X+(area.Width-Width)/2),Math.Max(area.Y,area.Y+(area.Height-Height)/2));
        var tabs=new TabControl { Dock=DockStyle.Fill,Padding=new Point(18,7) };
        var main=new TabPage("设备") { BackColor=Color.White }; var prefs=new TabPage("切换与设置") { BackColor=Color.White };
        tabs.TabPages.Add(main); tabs.TabPages.Add(prefs); Controls.Add(tabs);
        BuildMain(main); BuildSettings(prefs);
        var menu=new ContextMenuStrip();
        menu.Opening+=(s,e)=> {
            menu.Items.Clear(); RefreshDevices(false);
            menu.Items.Add("打开音频面板",null,(o,a)=>Open());
            var picks=OutputCycle();
            if(picks.Count>0) menu.Items.Add(new ToolStripSeparator());
            foreach(var p in picks) { AudioItem item=p; var mi=menu.Items.Add("切到 "+p.Name,null,(o,a)=>Act(()=>SetDefault(item)));
                ((ToolStripMenuItem)mi).Checked=Same(p.Id,defaultOut); }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("默认话筒静音 / 取消静音",null,(o,a)=>Act(ToggleDefaultMic));
            menu.Items.Add("退出",null,(o,a)=>{closing=true;Close();});
        };
        tray.ContextMenuStrip=menu; tray.Icon=Icon; tray.Text="桌面音频"; tray.Visible=true;
        tray.MouseClick+=(s,e)=>{if(e.Button==MouseButtons.Left) Open();};
        IntPtr window=Handle;
        hotOut=RegisterHotKey(window,1,0x4003,(uint)Keys.F8); hotMic=RegisterHotKey(window,2,0x4003,(uint)Keys.F7);
        shortcuts.Text="切换输出    Ctrl + Alt + F8"+(hotOut?"":"（被占用）")+"       默认话筒静音    Ctrl + Alt + F7"+(hotMic?"":"（被占用）");
        RefreshDevices(false);
        LocalSettings.Log("hotkey-output="+hotOut+" hotkey-mic="+hotMic+" endpoint-events="+events.Registered+" groups="+groups.Count+" daily="+groups.Count(Primary)+" cycle="+OutputCycle().Count+" status="+status.Text);
        timer.Interval=425; timer.Tick+=(s,e)=>{
            if(openEvent.WaitOne(0)) Open();
            long now=clock.ElapsedMilliseconds;
            if(events.Changed || now>=nextScan) RefreshDevices(true);
            else if(Visible && now>=nextView && !tooltipEditing) { nextView=now+1800; RefreshLevels(); }
        }; timer.Start();
        FormClosing+=(s,e)=>{ if(!closing && e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();} };
        FormClosed+=(s,e)=>{timer.Stop(); events.Dispose(); UnregisterHotKey(window,1);UnregisterHotKey(window,2);tray.Visible=false;tray.Dispose();Application.ExitThread();};
    }
    public void Open() { Show(); WindowState=FormWindowState.Normal; Activate(); RefreshDevices(false); }
    static bool Same(string a,string b){return string.Equals(a,b,StringComparison.OrdinalIgnoreCase);}
    static void Add(Control p,Control c,int x,int y,int w,int h){c.SetBounds(x,y,w,h);p.Controls.Add(c);}
    Label LabelAt(Control p,string s,int x,int y,int w,int h,bool bold=false){
        var c=new Label {Text=s,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true}; if(bold)c.Font=new Font(Font.FontFamily,12,FontStyle.Bold); Add(p,c,x,y,w,h);return c;}
    Button ButtonAt(Control p,string s,int x,int y,int w,Action fn){var b=new Button{Text=s,UseVisualStyleBackColor=true};Add(p,b,x,y,w,29);b.Click+=(o,e)=>Act(fn);return b;}
    void BuildMain(Control page)
    {
        counts.Font=new Font(Font.FontFamily,12,FontStyle.Bold);counts.TextAlign=ContentAlignment.MiddleLeft;Add(page,counts,16,8,397,32);
        showAll.Text="显示其他接口";Add(page,showAll,525,10,130,27);
        showAll.CheckedChanged+=(s,e)=>{listSignature=null;RefreshDevices(false);};
        hardware.View=View.Details; hardware.FullRowSelect=true;hardware.MultiSelect=false;hardware.HideSelection=false;
        hardware.Columns.Add("设备",265);hardware.Columns.Add("识别用途",143);hardware.Columns.Add("Windows 默认",188);
        Add(page,hardware,16,49,635,167);hardware.SelectedIndexChanged+=(s,e)=>{if(!busy) ShowSelection();};
        detail.TextAlign=ContentAlignment.MiddleLeft;detail.AutoEllipsis=true; Add(page,detail,17,222,405,34);
        role.DropDownStyle=ComboBoxStyle.DropDownList;role.Items.AddRange(modeText);Add(page,role,428,226,222,30);
        role.SelectionChangeCommitted+=(s,e)=>Act(()=>{var g=Selected(); if(g!=null){SmartPolicy.SetMode(g,modes[role.SelectedIndex]);listSignature=null;portSignature=null;RefreshDevices(false);}});
        for(int f=0;f<2;f++)
        {
            int flow=f, y=266+f*107;
            LabelAt(page,f==0?"播放":"录音",16,y,60,30,true);
            ports[f].DropDownStyle=ComboBoxStyle.DropDownList; Add(page,ports[f],82,y+2,335,28);
            ports[f].SelectionChangeCommitted+=(s,e)=>Act(()=>{var g=Selected();var p=Chosen(flow);if(g!=null && p!=null) SmartPolicy.Remember(g,flow,p.Id);RefreshLevels();},false);
            route[f]=ButtonAt(page,"设为默认",432,y,102,()=>SetDefault(Chosen(flow)));
            mute[f]=ButtonAt(page,"静音",544,y,105,()=>Toggle(Chosen(flow)));
            volume[f].Minimum=0;volume[f].Maximum=100;volume[f].TickStyle=TickStyle.None;volume[f].AccessibleName=(f==0?"播放":"录音")+"音量";
            Add(page,volume[f],80,y+39,183,32);Add(page,level[f],275,y+42,58,22);
            volume[f].MouseDown+=(s,e)=>tooltipEditing=true;volume[f].MouseUp+=(s,e)=>tooltipEditing=false;
            volume[f].Scroll+=(s,e)=>{ if(!busy) Act(()=>{var p=Chosen(flow);if(p!=null){int n=volume[flow].Value;AudioCore.SetVolume(p.Id,n);level[flow].Text=n+" %";}},false);};
            if(f==0){balance=ButtonAt(page,"左右平衡…",432,y+37,102,AdjustBalance);fx=ButtonAt(page,"声音增强…",544,y+37,105,Enhancements);}
            var line=new Label {BackColor=Color.FromArgb(220,228,229)};Add(page,line,16,y+91,635,1);
        }
        status.AutoEllipsis=true; status.ForeColor=teal;status.TextAlign=ContentAlignment.MiddleLeft;Add(page,status,16,477,636,44);
    }
    void BuildSettings(Control p)
    {
        LabelAt(p,"快捷键与接入行为",18,14,600,33,true); Add(p,shortcuts,20,64,634,37);
        var auto=new CheckBox {Text="接入一个新的可用播放设备时，自动将它设为默认输出",Checked=preferNew};
        Add(p,auto,18,107,620,31);auto.CheckedChanged+=(s,e)=>{preferNew=auto.Checked;LocalSettings.Put("PreferNewOutput",preferNew?"1":"0");};
        var startup=new CheckBox {Text="登录 Windows 后启用托盘和快捷键",Checked=LocalSettings.Startup};
        Add(p,startup,18,146,600,31);startup.CheckedChanged+=(s,e)=>Act(()=>LocalSettings.Startup=startup.Checked,false);
        LabelAt(p,"设备识别",18,209,600,33,true);
        LabelAt(p,"新接入的 USB 和蓝牙音频设备会自动出现。双向声卡会列出播放和录音；可在设备右侧选择实际用途，同一设备下次接入时会沿用。",18,248,619,59);
        LabelAt(p,"板载、显示器和软件线路可在“显示其他接口”中查看。选择“加入日常设备”即可将它纳入快捷切换。",18,314,619,52);
        ButtonAt(p,"应用音量和输出",18,386,152,()=>Process.Start("ms-settings:apps-volume"));
        ButtonAt(p,"Windows 声音设置",180,386,158,()=>Process.Start("ms-settings:sound"));
        ButtonAt(p,"恢复原默认话筒并退出…",18,453,218,Restore);
    }
    void Restore()
    {
        if(MessageBox.Show(this,"恢复安装前的默认输入，并关闭桌面音频的接入自动切换、登录启动和快捷键？","恢复设置",MessageBoxButtons.OKCancel,MessageBoxIcon.Question)!=DialogResult.OK)return;
        string id=LocalSettings.Get("BeforeInput");
        if(string.IsNullOrEmpty(id)) throw new InvalidOperationException("尚未记录安装前的默认输入。");
        AudioCore.Route(id,1);LocalSettings.Put("PreferNewOutput","0");LocalSettings.Startup=false;closing=true;Close();
    }
    SmartGroup Selected() {return hardware.SelectedItems.Count==1 ? hardware.SelectedItems[0].Tag as SmartGroup:null;}
    AudioItem Chosen(int flow) {var p=ports[flow].SelectedItem as PortChoice;return p==null?null:p.Device;}
    bool Eligible(SmartGroup g,int flow){return SmartPolicy.PrimaryForFlow(g,groups,flow,flow==0?defaultOut:defaultIn) ||
        (SmartPolicy.Mode(g)=="auto" && !g.Virtual && plugs.Recent(g,flow));}
    bool Primary(SmartGroup g){return Eligible(g,0)||Eligible(g,1);}
    static bool ContainsId(IEnumerable<AudioItem> ps,string id){return ps.Any(p=>Same(p.Id,id));}
    string Marks(SmartGroup g) {return string.Join("、",new[]{ContainsId(g.Outputs,defaultOut)?"输出":null,ContainsId(g.Inputs,defaultIn)?"输入":null}.Where(x=>x!=null));}
    void RefreshDevices(bool allowRoute)
    {
        if(busy || IsDisposed) return;
        busy=true;
        try{
            groups=AudioDiscovery.Scan(); plugs.Observe(groups); defaultOut=AudioCore.Default(0,1); defaultIn=AudioCore.Default(1,1);
            var outputGroups=groups.Where(g=>!g.Virtual && Eligible(g,0) && SmartPolicy.Choices(g,0,false).Count>0 && (!g.Infrastructure || plugs.Recent(g,0) || SmartPolicy.Mode(g)!="auto")).ToList();
            if(allowRoute && preferNew && previousOutputs!=null){
                var added=outputGroups.Where(g=>!previousOutputs.Contains(g.Key)).ToList();
                if(added.Count==1) {var next=SmartPolicy.Pick(added[0],0,defaultOut);if(next!=null&&!Same(next.Id,defaultOut)){AudioCore.Route(next.Id,0);defaultOut=next.Id;}}
            }
            previousOutputs=new HashSet<string>(outputGroups.Select(g=>g.Key),StringComparer.OrdinalIgnoreCase);
            var ordered=groups.OrderBy(g=>!Primary(g)).ThenBy(g=>g.Name,StringComparer.CurrentCultureIgnoreCase).ToList();
            var shown=ordered.Where(g=>Primary(g)||showAll.Checked).ToList();
            counts.Text="日常设备  "+ordered.Count(Primary)+(showAll.Checked?"    ·    全部 "+ordered.Count:"");
            var sig=string.Join("|",shown.Select(g=>g.Key+";"+g.Name+";"+SmartPolicy.RoleLabel(g)+";"+Marks(g)));
            string selected=Selected()==null?null:Selected().Key;
            if(sig!=listSignature) {
                listSignature=sig;hardware.BeginUpdate();hardware.Items.Clear();
                foreach(var g in shown) {
                    var row=new ListViewItem(new[]{g.Name,SmartPolicy.RoleLabel(g),Marks(g)}){Tag=g};
                    hardware.Items.Add(row);
                }
                var rows=hardware.Items.Cast<ListViewItem>().ToList();
                var pick=rows.FirstOrDefault(r=>Same(((SmartGroup)r.Tag).Key,selected))??rows.FirstOrDefault(r=>ContainsId(((SmartGroup)r.Tag).Outputs,defaultOut))??rows.FirstOrDefault();
                if(pick!=null)pick.Selected=true;hardware.EndUpdate();portSignature=null;
            } else {
                for(int i=0;i<shown.Count;i++)hardware.Items[i].Tag=shown[i];
            }
            ShowSelection();
            string o=NameFor(defaultOut,0),m=NameFor(defaultIn,1);
            status.Text="默认输出   "+o+"      /      默认输入   "+m;
            string tip="桌面音频 · "+o;tray.Text=tip.Length>62?tip.Substring(0,62):tip;
        }catch(Exception e){status.Text="设备状态暂时不可用 · "+e.Message;LocalSettings.Log(e.ToString());}
        finally{busy=false;nextScan=clock.ElapsedMilliseconds+(events.Registered?10000:2500);}
    }
    string NameFor(string id,int flow){var g=groups.FirstOrDefault(x=>ContainsId(flow==0?x.Outputs:x.Inputs,id));return g==null?"无":g.Name;}
    void ShowSelection()
    {
        var g=Selected();role.Enabled=g!=null;role.SelectedIndex=g==null?-1:Array.IndexOf(modes,SmartPolicy.Mode(g));
        detail.Text=g==null?"等待设备连接":g.Details;
        var choices=new List<AudioItem>[2];
        for(int f=0;f<2;f++)choices[f]=g==null?new List<AudioItem>():SmartPolicy.Choices(g,f,showAll.Checked);
        string sig=(g==null?"":g.Key)+"|"+string.Join("|",choices.SelectMany(x=>x).Select(x=>x.Id));
        if(sig!=portSignature) {
            portSignature=sig;
            for(int f=0;f<2;f++){
                string old=Chosen(f)==null?null:Chosen(f).Id;
                var pick=g==null?null:SmartPolicy.Pick(g,f,f==0?defaultOut:defaultIn);
                ports[f].Items.Clear(); foreach(var item in choices[f])ports[f].Items.Add(new PortChoice{Device=item});
                int i=choices[f].FindIndex(d=>Same(d.Id,old));
                if(i<0 && pick!=null)i=choices[f].FindIndex(d=>Same(d.Id,pick.Id));
                ports[f].SelectedIndex=i>=0?i:(choices[f].Count==0?-1:0);
            }
        }
        RefreshLevels();
    }
    void RefreshLevels()
    {
        for(int f=0;f<2;f++){
            var p=Chosen(f);bool ready=p!=null;
            mute[f].Enabled=route[f].Enabled=volume[f].Enabled=ports[f].Enabled=ready;
            if(f==0){balance.Enabled=fx.Enabled=ready;}
            if(!ready){level[f].Text="—";mute[f].Text="静音";route[f].Text="设为默认";volume[f].Value=0;continue;}
            try{int n=AudioCore.Volume(p.Id);if(!volume[f].Capture)volume[f].Value=n;level[f].Text=n+" %";
                mute[f].Text=AudioCore.Muted(p.Id)?"取消静音":"静音";route[f].Text=Same(p.Id,f==0?defaultOut:defaultIn)?"当前默认":"设为默认";}
            catch(COMException){level[f].Text="离线";}
        }
    }
    List<AudioItem> OutputCycle(){
        return groups.Where(g=>Eligible(g,0)).OrderBy(g=>g.Name,StringComparer.CurrentCultureIgnoreCase)
            .Where(g=>SmartPolicy.Choices(g,0,false).Count>0).Select(g=>SmartPolicy.Pick(g,0,defaultOut)).Where(p=>p!=null).ToList();
    }
    void Cycle(){RefreshDevices(false);var options=OutputCycle();if(options.Count==0)throw new InvalidOperationException("当前没有可切换的日常播放设备。");
        var current=groups.FirstOrDefault(g=>ContainsId(g.Outputs,defaultOut));
        int idx=current==null?-1:options.FindIndex(p=>ContainsId(current.Outputs,p.Id));SetDefault(options[(idx+1)%options.Count]);}
    void SetDefault(AudioItem p){if(p==null)return;AudioCore.Route(p.Id,p.Flow);Notify((p.Flow==0?"默认输出":"默认输入")+" · "+p.Name);}
    void Toggle(AudioItem p){if(p==null)return;bool b=!AudioCore.Muted(p.Id);AudioCore.SetMute(p.Id,b);Notify(p.Name+(b?" 已静音":" 已取消静音"));}
    void ToggleDefaultMic(){string id=AudioCore.Default(1,1);if(id==null)throw new InvalidOperationException("未找到默认录音设备。");Toggle(AudioCore.List(1).FirstOrDefault(p=>Same(p.Id,id)));}
    void Notify(string s){if(!Visible)tray.ShowBalloonTip(1000,"桌面音频",s,ToolTipIcon.Info);}
    void Act(Action fn,bool refresh=true){try{fn();if(refresh && !IsDisposed)RefreshDevices(false);}catch(Exception e){
        if(Visible)MessageBox.Show(this,e.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);
        else tray.ShowBalloonTip(2000,"桌面音频",e.Message,ToolTipIcon.Warning);}}
    void Enhancements(){
        var p=Chosen(0);if(p==null)return;
        Rectangle area=Screen.FromHandle(Handle).WorkingArea;
        var thread=new Thread(()=>{try{AudioTuning.Open(p.Id,p.Bus,area);}catch(Exception e){if(!IsDisposed)BeginInvoke((Action)(()=>MessageBox.Show(this,e.Message,"声音增强")));}});
        thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();
    }
    void AdjustBalance(){
        var p=Chosen(0);if(p==null)return;int old=AudioCore.Balance(p.Id);
        using(var d=new Form{Text=p.Name+" · 左右平衡",Font=Font,ClientSize=new Size(410,150),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false}){
            var label=LabelAt(d,"",16,8,365,30);
            var s=new TrackBar{Minimum=-100,Maximum=100,TickFrequency=25,Value=old};Add(d,s,12,43,383,45);
            Action read=()=>label.Text=s.Value==0?"居中":(s.Value<0?"偏左  ":"偏右  ")+Math.Abs(s.Value)+" %";read();s.ValueChanged+=(o,e)=>read();
            ButtonAt(d,"居中",14,101,85,()=>s.Value=0);ButtonAt(d,"应用",308,101,85,()=>{AudioCore.SetBalance(p.Id,s.Value);d.Close();});d.ShowDialog(this);}
    }
    protected override void WndProc(ref Message msg){if(msg.Msg==0x0312){if(msg.WParam.ToInt32()==1)Act(Cycle);if(msg.WParam.ToInt32()==2)Act(ToggleDefaultMic);}base.WndProc(ref msg);}
}

internal static class DeskIcon
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    public static Icon Create(){using(var b=new Bitmap(32,32))using(var g=Graphics.FromImage(b))using(var pen=new Pen(Color.White,2.6f)){
        g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using(var brush=new SolidBrush(Color.FromArgb(0,99,93)))g.FillEllipse(brush,0,0,31,31);
        g.DrawLine(pen,9,13,9,19);g.DrawLine(pen,15,7,15,25);g.DrawLine(pen,21,11,21,21);
        var h=b.GetHicon();try{using(var ico=Icon.FromHandle(h))return (Icon)ico.Clone();}finally{DestroyIcon(h);}
    }}
}
internal static class Program
{
    [STAThread] static void Main(string[] args){
        bool hidden=args.Contains("--tray");
        bool first;
        using(var mutex=new Mutex(true,@"Local\DeskAudioControl.Panel",out first))
        using(var show=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\DeskAudioControl.Show")){
            if(!first){if(!hidden)show.Set();return;}
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            try{using(var form=new AudioPanel(hidden,show)){if(!hidden)form.Open();Application.Run();}}
            catch(Exception e){LocalSettings.Log(e.ToString());if(!hidden)MessageBox.Show(e.Message,"桌面音频",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        }
    }
}
