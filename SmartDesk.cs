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
