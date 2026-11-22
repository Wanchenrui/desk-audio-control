using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

// Separate WinExe so the installed executable is unlocked while it is being replaced.
internal static class Updater
{
    static Process Launch(string target,string args)
    {
        return Process.Start(new ProcessStartInfo(target,args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target) });
    }

    static void Replace(string source,string target,string backup)
    {
        var clock = Stopwatch.StartNew();
        for (;;)
        {
            try { File.Replace(source,target,backup); return; }
            catch (IOException) { if (clock.ElapsedMilliseconds >= 3500 || !File.Exists(source)) throw; Thread.Sleep(100); }
        }
    }

    static string Hash(string path) { using (var file = File.OpenRead(path)) return UpdatePackage.Digest(file); }

    internal static void Apply(string target,string path,string backup,Guid job,UpdatePackage package,bool tray)
    {
        package.Verify(path);
        if (package.Version <= UpdatePackage.ReadVersion(target)) throw new InvalidOperationException("安装版本已经变化。");
        using (var started = new EventWaitHandle(false,EventResetMode.ManualReset,UpdatePackage.Signal(job,"started")))
        {
            Replace(path,target,backup);
            try
            {
                using (var child = Launch(target,"--updated " + job.ToString("N") + (tray ? " --tray" : "")))
                {
                    var watch = Stopwatch.StartNew();
                    while (!started.WaitOne(100))
                    {
                        if (child.HasExited) throw new IOException("新版在完成初始化前已退出。");
                        if (watch.ElapsedMilliseconds >= 30000)
                        {
                            child.Kill();
                            if (!child.WaitForExit(5000)) throw new IOException("新版进程尚未退出。");
                            throw new IOException("新版启动超时。");
                        }
                    }
                }
            }
            catch
            {
                Replace(backup,target,null);
                throw;
            }
        }
        // An antivirus lock on the old backup must not turn a successful update into a rollback.
        try { File.Delete(backup); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [STAThread] public static int Main(string[] args)
    {
        return Run(args,message => MessageBox.Show(message,"DeskAudio 更新未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning));
    }

    internal static int Run(string[] args,Action<string> report)
    {
        int pid; long size; Guid job; Version version;
        if (args.Length != 6 || !Int32.TryParse(args[0],out pid) || pid <= 0 ||
            !Guid.TryParseExact(args[1],"N",out job) || !Int64.TryParse(args[2],NumberStyles.None,CultureInfo.InvariantCulture,out size) ||
            !System.Text.RegularExpressions.Regex.IsMatch(args[3],@"\A[0-9a-fA-F]{64}\z") ||
            !Version.TryParse(args[4],out version) || (args[5] != "tray" && args[5] != "panel")) return 2;

        bool stopped = false;
        string target = null, path = null, backup = null, original = null;
        bool tray = args[5] == "tray";
        try
        {
            var package = new UpdatePackage(0,size,args[3],version);
            bool created;
            using (var gate = new EventWaitHandle(false,EventResetMode.ManualReset,UpdatePackage.Handoff,out created))
            {
                if (!created) return 2;
                using (var ready = EventWaitHandle.OpenExisting(UpdatePackage.Signal(job,"ready")))
                using (var parent = Process.GetProcessById(pid))
                {
                    target = parent.MainModule.FileName;
                    if (UpdatePackage.ReadVersion(target) >= version) return 2;
                    original = Hash(target);
                    path = UpdatePackage.TempPath(target,job,".download");
                    backup = UpdatePackage.TempPath(target,job,".backup");
                    package.Verify(path);
                    ready.Set();
                    if (!parent.WaitForExit(30000)) throw new IOException("桌面音频未能退出。");
                    stopped = true;
                }
                if (Hash(target) != original) throw new IOException("安装目录中的程序已经改变。");
                Apply(target,path,backup,job,package,tray);
            }
            return 0;
        }
        catch (Exception error)
        {
            if (stopped)
            {
                string message = error.Message;
                try
                {
                    if (Hash(target) == original)
                    {
                        using (Launch(target,tray ? "--tray" : "")) { }
                        message += "\n已重新启动原版本。";
                    }
                    else if (File.Exists(backup)) message += "\n旧版备份：" + backup;
                }
                catch (Exception) { message += "\n请从安装目录启动桌面音频。"; }
                report(message);
            }
            return 1;
        }
        finally
        {
            try { if (path != null) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
