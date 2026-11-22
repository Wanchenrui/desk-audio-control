using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

internal static class SceneContext
{
    [StructLayout(LayoutKind.Sequential)] struct LastInput { public uint Size, Tick; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,System.Text.StringBuilder name,int length);

    // The privacy dashboard exposes use timestamps on supported Windows versions.
    // A missing usage record supplies no camera signal. Never open a capture stream.
    static bool CameraInUse(RegistryKey root)
    {
        if (root == null) return false;
        foreach (string name in root.GetSubKeyNames())
            using (var app = root.OpenSubKey(name))
            {
                if (app == null) continue;
                if (name == "NonPackaged") { if (CameraInUse(app)) return true; continue; }
                object start = app.GetValue("LastUsedTimeStart"), stop = app.GetValue("LastUsedTimeStop");
                if (start is long && stop is long && (long)start > (long)stop) return true;
            }
        return false;
    }

    static readonly SceneApplications applications=new SceneApplications(
        System.IO.Path.Combine(AppConfig.Folder,"scene-apps.json"));

    static string Category(uint pid,IDictionary<string,string> apps,out bool knownMedia)
    {
        knownMedia=false;
        if (pid==0) return null;
        try {
            using (var process=Process.GetProcessById((int)pid))
                return SceneApplications.Category(process.ProcessName,apps,out knownMedia);
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    public static SceneSignals Read(IEnumerable<AudioItem> inputs,IEnumerable<AudioItem> outputs,string[] roles,bool ignoreOwnStreams = false,bool collectPeaks = false)
    {
        return ReadWithRetry(()=>ReadOnce(inputs,outputs,roles,ignoreOwnStreams,collectPeaks));
    }

    internal static SceneSignals ReadWithRetry(Func<SceneSignals> observe)
    {
        for (int attempt=0;;attempt++) {
            try { return observe(); }
            catch (Exception error) {
                var com=error as COMException;
                // RPC_E_CALL_REJECTED and RPC_E_SERVERCALL_RETRYLATER can be transient.
                if (attempt==0 && com!=null && (com.ErrorCode==unchecked((int)0x80010001) ||
                    com.ErrorCode==unchecked((int)0x8001010A))) continue;
                return new SceneSignals { Hour=DateTime.Now.Hour,Unknown=true,
                    ReadError=error.GetType().Name+" 0x"+error.HResult.ToString("X8") };
            }
        }
    }

    static SceneSignals ReadOnce(IEnumerable<AudioItem> inputs,IEnumerable<AudioItem> outputs,string[] roles,
        bool ignoreOwnStreams,bool collectPeaks)
    {
        var result=new SceneSignals { Hour=DateTime.Now.Hour };
        uint pid;
        var window = GetForegroundWindow();
        GetWindowThreadProcessId(window,out pid);
        var windowClass = new System.Text.StringBuilder(64);
        GetClassName(window,windowClass,windowClass.Capacity);
        result.ControlForeground = pid == GetCurrentProcessId() || windowClass.ToString() == "Shell_TrayWnd" ||
            windowClass.ToString() == "NotifyIconOverflowWindow";
        var apps=applications.Read();
        bool foregroundMedia;
        result.Foreground=Category(pid,apps,out foregroundMedia);
        using (var camera = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam"))
            result.Camera = CameraInUse(camera);
        var input = new LastInput { Size = (uint)Marshal.SizeOf(typeof(LastInput)) };
        result.Away = window == IntPtr.Zero || result.Foreground == "离开" ||
            (GetLastInputInfo(ref input) && unchecked((uint)Environment.TickCount - input.Tick) >= 600000);
        uint self=GetCurrentProcessId();
        foreach (var p in inputs)
        {
            if (ignoreOwnStreams) AudioCore.ActiveSessions(p.Id,owner=>{ if (owner!=self) result.Capturing=true; });
            else result.Capturing=AudioCore.InUse(p.Id);
            if (result.Capturing) break;
        }
        var categories = new HashSet<string>();
        bool sawCommunication = false;
        foreach (var p in outputs)
        {
            bool comm = RouteTransaction.Equal(p.Id,roles[2]);
            sawCommunication |= comm;
            bool active=false;
            AudioCore.ActiveSessions(p.Id,sessionPid => {
                if (ignoreOwnStreams && sessionPid==self) return;
                result.Sessions++;
                active=true;
                bool knownMedia;
                string kind=Category(sessionPid,apps,out knownMedia);
                categories.Add(kind);
                // Unknown shared-role sessions can be browser calls. Dedicated communications
                // streams are protected regardless of the foreground application.
                if (kind == "会议" || (comm && !RouteTransaction.Equal(roles[1],p.Id))) result.Communicating = true;
                if (comm && !knownMedia) result.SharedBusy = true;
            });
            if (active && collectPeaks) {
                try { if (AudioCore.Peak(p.Id)>.001f) result.Audible.Add(p.Id); }
                catch (COMException) { }
                catch (InvalidCastException) { }
            }
        }
        result.Unknown = !sawCommunication;
        if (result.Foreground == null)
            result.Foreground = new[] { "录制","游戏","影音","音乐" }.FirstOrDefault(categories.Contains);
        return result;
    }
}
