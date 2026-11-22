using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;

internal static class UpdateInstall
{
    internal static bool IsAssetHost(Uri uri)
    {
        return uri != null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
            uri.UserInfo.Length == 0 && (uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com");
    }

    internal static void CopyDownload(Stream input,Stream output,long size,Action<int> progress,CancellationToken cancel)
    {
        if (size <= 0 || size > UpdatePackage.SizeLimit) throw new InvalidDataException("更新包大小无效。");
        long total = 0;
        int previous = -1, count;
        var buffer = new byte[65536];
        while ((count = input.Read(buffer,0,buffer.Length)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            total += count;
            if (total > size) throw new InvalidDataException("下载内容超过发布附件大小。");
            output.Write(buffer,0,count);
            int percent = (int)(total * 100 / size);
            if (percent != previous) { progress(percent); previous = percent; }
        }
        if (total != size) throw new EndOfStreamException("下载中断，请重试。");
    }

    internal static void Download(UpdateResult release,string path,Action<int> progress,CancellationToken cancel)
    {
        if ((int)ServicePointManager.SecurityProtocol != 0) ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var package = release.Package;
        var url = new Uri("https://api.github.com/repos/Wanchenrui/desk-audio-control/releases/assets/" + package.Id.ToString(CultureInfo.InvariantCulture));
        var token = release.Authenticated ? UpdateCheck.GetCredential() : null;
        for (int hop = 0; hop < 4; hop++)
        {
            cancel.ThrowIfCancellationRequested();
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "DeskAudio/" + UpdateCheck.InstalledVersion;
            request.AllowAutoRedirect = false;
            request.Accept = "application/octet-stream";
            request.Timeout = request.ReadWriteTimeout = 30000;
            if (hop == 0 && token != null) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
            using (cancel.Register(request.Abort))
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                int code = (int)response.StatusCode;
                if (code == 301 || code == 302 || code == 303 || code == 307 || code == 308)
                {
                    if (!Uri.TryCreate(response.Headers["Location"],UriKind.Absolute,out url) || !IsAssetHost(url))
                        throw new InvalidDataException("GitHub 返回了无法确认的附件地址。");
                    continue;
                }
                if (code != 200 || (response.ContentLength >= 0 && response.ContentLength != package.Size))
                    throw new InvalidDataException("GitHub 返回的附件大小不匹配。");
                using (var input = response.GetResponseStream())
                using (var output = new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    CopyDownload(input,output,package.Size,progress,cancel);
                    output.Flush(true);
                }
                return;
            }
        }
        throw new InvalidDataException("下载重定向次数过多。");
    }

    // The helper is built into the installed client. Materialize it under the user's data folder.
    static string Helper(string folder)
    {
        using (var resource = typeof(UpdateInstall).Assembly.GetManifestResourceStream("DeskAudio.Updater.exe"))
        {
            if (resource == null) throw new InvalidDataException("程序缺少更新组件。");
            string hash = UpdatePackage.Digest(resource);
            Directory.CreateDirectory(folder);
            string helper = Path.Combine(folder,hash + ".exe");
            if (!File.Exists(helper))
            {
                string temp = Path.Combine(folder,Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    resource.Position = 0;
                    using (var file = new FileStream(temp,FileMode.CreateNew)) resource.CopyTo(file);
                    try { File.Move(temp,helper); } catch (IOException) { if (!File.Exists(helper)) throw; }
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            using (var file = File.OpenRead(helper))
                if (UpdatePackage.Digest(file) != hash) throw new InvalidDataException("本地更新组件校验失败。");
            return helper;
        }
    }

    internal static void Prepare(UpdateResult release,Action<int> progress,CancellationToken cancel,bool tray)
    {
        Prepare(release,path => Download(release,path,progress,cancel),Path.Combine(AppConfig.Folder,"Updater"),cancel,tray);
    }

    internal static void Prepare(UpdateResult release,Action<string> download,string helperFolder,CancellationToken cancel,bool tray)
    {
        if (release == null || release.State != UpdateState.Available || release.Package == null ||
            release.Package.Version <= UpdateCheck.InstalledVersion) throw new InvalidOperationException("请重新检查更新。");
        Guid job = Guid.NewGuid();
        string target = Assembly.GetEntryAssembly().Location;
        string path = UpdatePackage.TempPath(target,job,".download");
        bool handedOff = false;
        try
        {
            download(path);
            release.Package.Verify(path);
            cancel.ThrowIfCancellationRequested();
            using (var ready = new EventWaitHandle(false,EventResetMode.ManualReset,UpdatePackage.Signal(job,"ready")))
            using (var self = Process.GetCurrentProcess())
            {
                string args = String.Format(CultureInfo.InvariantCulture,"{0} {1:N} {2} {3} {4} {5}",
                    self.Id,job,release.Package.Size,release.Package.Hash,release.Package.Version,tray ? "tray" : "panel");
                using (var helper = Process.Start(new ProcessStartInfo(Helper(helperFolder),args) { UseShellExecute = false, CreateNoWindow = true }))
                {
                    var watch = Stopwatch.StartNew();
                    while (!ready.WaitOne(100))
                    {
                        if (helper.HasExited || watch.ElapsedMilliseconds >= 15000)
                            throw new IOException("更新组件未能准备好，当前版本继续运行。");
                        cancel.ThrowIfCancellationRequested();
                    }
                    handedOff = true;
                }
            }
        }
        finally { if (!handedOff && File.Exists(path)) File.Delete(path); }
    }

    internal static void Started(string[] args)
    {
        if (args.Length < 2 || args[0] != "--updated") return;
        Guid job;
        if (!Guid.TryParseExact(args[1],"N",out job)) return;
        try { using (var started = EventWaitHandle.OpenExisting(UpdatePackage.Signal(job,"started"))) started.Set(); }
        catch (WaitHandleCannotBeOpenedException) { }
    }
}
