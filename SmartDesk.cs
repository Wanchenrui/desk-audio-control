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
    public const string Run=@"Software\Microsoft\Windows\CurrentVersion\Run", Name="DeskAudioControl";
    public static string Get(string name) { return AppConfig.Store.Get(name); }
    public static void Put(string name,string value) { AppConfig.Store.Put(name,value); }
    public static bool Startup {
        get { using(var k=Registry.CurrentUser.OpenSubKey(Run)) return k!=null && k.GetValue(Name)!=null; }
        set { using(var k=Registry.CurrentUser.CreateSubKey(Run)) { if(value) k.SetValue(Name,"\""+Application.ExecutablePath+"\" --tray"); else k.DeleteValue(Name,false); } }
    }
    public static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),Name); } }
    public static void Log(string message) { try { Directory.CreateDirectory(Folder); File.WriteAllText(Path.Combine(Folder,"session.txt"),DateTime.Now.ToString("s")+" "+message,Encoding.UTF8); } catch(IOException) {} catch(UnauthorizedAccessException) {} }
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
        if (args.Length < 2 || args[0] != "--updated")
        {
            EventWaitHandle handoff;
            if (EventWaitHandle.TryOpenExisting(UpdatePackage.Handoff,out handoff)) { handoff.Dispose(); return; }
        }
        bool hidden=args.Contains("--tray");
        bool first;
        using(var mutex=new Mutex(true,@"Local\DeskAudioControl.Panel",out first))
        using(var show=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\DeskAudioControl.Show")){
            if (args.Length>0 && args[0]=="--reset-usb-identity") {
                try {
                    if (!first) throw new InvalidOperationException("请先退出托盘中的 DeskAudio，再重置 USB 型号识别。");
                    int count=AppConfig.Store.ResetUsbIdentity(args.Length==2 ? args[1] : null);
                    MessageBox.Show(count==0 ? "该型号没有歧义记录。" : "已清除该型号的歧义记录。下次启动将按当前连接重新识别。",
                        "USB 型号识别",MessageBoxButtons.OK,MessageBoxIcon.Information);
                } catch (Exception e) {
                    Environment.ExitCode=1;
                    MessageBox.Show(e.Message,"USB 型号识别",MessageBoxButtons.OK,MessageBoxIcon.Error);
                }
                return;
            }
            if(!first){if(!hidden)show.Set();return;}
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            try{using(var form=new AudioPanel(hidden,show)){
                if(!hidden)form.Open();
                form.BeginInvoke((Action)(() => UpdateInstall.Started(args)));
                Application.Run();
            }}
            catch(Exception e){LocalSettings.Log(e.ToString());if(!hidden)MessageBox.Show(e.Message,"桌面音频",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        }
    }
}
