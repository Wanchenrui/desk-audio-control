using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

public enum UpdateState { Current, Available, NeedsAccess, NoRelease, Failed }

public sealed class UpdateResult
{
    public readonly UpdateState State;
    public readonly string Message, Tag, PageUrl, AssetUrl;

    internal UpdateResult(UpdateState state, string message, string tag = null, string asset = null)
    {
        State = state;
        Message = message;
        Tag = tag;
        PageUrl = tag == null ? UpdateCheck.ReleasesPage : UpdateCheck.RepositoryUrl + "/releases/tag/" + Uri.EscapeDataString(tag);
        AssetUrl = asset;
    }
}

public static class UpdateCheck
{
    internal const string RepositoryUrl = "https://github.com/Wanchenrui/desk-audio-control";
    public const string ReleasesPage = RepositoryUrl + "/releases/latest";
    const string Api = "https://api.github.com/repos/Wanchenrui/desk-audio-control/releases";
    const string Settings = @"Software\DeskAudioControl";
    const string SecretName = "UpdateAccess";
    const int ResponseLimit = 2 * 1024 * 1024;
    static readonly Regex StableTag = new Regex(
        @"\Av?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:\+[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*)?\z",
        RegexOptions.CultureInvariant);

    public static Version InstalledVersion
    {
        get
        {
            Version version = typeof(UpdateCheck).Assembly.GetName().Version;
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }
    }

    public static bool HasSavedToken
    {
        get { using (var key = Registry.CurrentUser.OpenSubKey(Settings)) return key != null && key.GetValue(SecretName) is byte[]; }
    }

    public static bool UseSignedInAccount
    {
        get { return AppConfig.Store.Get("UseSignedInGitHub") == "1"; }
        set { AppConfig.Store.Put("UseSignedInGitHub", value ? "1" : "0"); }
    }

    static bool ValidToken(string token)
    {
        return !String.IsNullOrEmpty(token) && token.Length <= 4096 && token.All(c => c >= 33 && c <= 126);
    }

    public static void SaveToken(string token)
    {
        token = (token ?? "").Trim();
        if (!ValidToken(token)) throw new ArgumentException("请输入有效的 GitHub 访问令牌。");
        byte[] plain = Encoding.UTF8.GetBytes(token);
        try
        {
            byte[] protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            using (var key = Registry.CurrentUser.CreateSubKey(Settings))
                key.SetValue(SecretName, protectedBytes, RegistryValueKind.Binary);
        }
        finally { Array.Clear(plain, 0, plain.Length); }
    }

    public static void ClearToken()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(Settings, true))
            if (key != null) key.DeleteValue(SecretName, false);
    }

    static string SavedToken()
    {
        byte[] encrypted;
        using (var key = Registry.CurrentUser.OpenSubKey(Settings))
            encrypted = key == null ? null : key.GetValue(SecretName) as byte[];
        if (encrypted == null) return null;
        byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); }
        finally { Array.Clear(plain, 0, plain.Length); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeCredential
    {
        public uint Flags, Type;
        public IntPtr Target, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME Written;
        public uint Bytes;
        public IntPtr Blob;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredReadW", SetLastError = true)]
    static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr credential);

    static string GitForWindowsToken()
    {
        // GCM's default Windows credential store. Read only the github.com HTTPS entry.
        IntPtr address;
        if (!CredRead("git:https://github.com", 1, 0, out address)) return null;
        try
        {
            var data = (NativeCredential)Marshal.PtrToStructure(address, typeof(NativeCredential));
            if (data.Blob == IntPtr.Zero || data.Bytes < 2 || data.Bytes > 8192 || data.Bytes % 2 != 0) return null;
            string value = Marshal.PtrToStringUni(data.Blob, (int)data.Bytes / 2).TrimEnd('\0');
            return ValidToken(value) ? value : null;
        }
        finally { CredFree(address); }
    }

    static string GitHubCliToken()
    {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe");
        if (!File.Exists(exe)) return null;
        var start = new ProcessStartInfo(exe, "auth token --hostname github.com") {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(exe)
        };
        start.EnvironmentVariables["GH_PROMPT_DISABLED"] = "1";
        start.EnvironmentVariables["GH_NO_UPDATE_NOTIFIER"] = "1";
        try
        {
            using (var process = Process.Start(start))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(); } catch (InvalidOperationException) {}
                    return null;
                }
                if (process.ExitCode != 0 || !output.Wait(500) || !errors.Wait(500)) return null;
                string value = output.Result.Trim();
                return ValidToken(value) ? value : null;
            }
        }
        catch (Win32Exception) { return null; }
    }

    internal static string ChooseCredential(string explicitToken, Func<string> stored, Func<bool> useAccount, Func<string> account)
    {
        if (!String.IsNullOrWhiteSpace(explicitToken)) return explicitToken.Trim();
        return stored() ?? (useAccount() ? account() : null);
    }

    static string GetCredential()
    {
        return ChooseCredential(Environment.GetEnvironmentVariable("DESKAUDIO_GITHUB_TOKEN"),
            SavedToken, () => UseSignedInAccount, () => GitForWindowsToken() ?? GitHubCliToken());
    }

    internal sealed class Reply
    {
        public readonly int Code;
        public readonly string Body;
        public readonly bool RateLimited;
        public Reply(int code, string body = null, bool limited = false) { Code = code; Body = body; RateLimited = limited; }
    }

    static Reply Request(bool list, string token)
    {
        // Older .NET Framework applications may default to TLS 1.0 instead of the system protocols.
        if ((int)ServicePointManager.SecurityProtocol != 0)
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var req = (HttpWebRequest)WebRequest.Create(Api + (list ? "?per_page=1" : "/latest"));
        req.UserAgent = "DeskAudio/" + InstalledVersion;
        req.Accept = "application/vnd.github+json";
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        req.AllowAutoRedirect = false;
        req.Timeout = req.ReadWriteTimeout = 9000;
        if (token != null) req.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
        HttpWebResponse response;
        try { response = (HttpWebResponse)req.GetResponse(); }
        catch (WebException ex)
        {
            response = ex.Response as HttpWebResponse;
            if (response == null) throw;
        }
        using (response)
        {
            int code = (int)response.StatusCode;
            bool rateLimited = code == 429 || code == 403 &&
                (response.Headers["X-RateLimit-Remaining"] == "0" || response.Headers["Retry-After"] != null);
            if (code != 200 || list) return new Reply(code, null, rateLimited);
            if (response.ContentLength > ResponseLimit) throw new InvalidDataException();
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                var body = new StringBuilder();
                var block = new char[4096];
                int count;
                while ((count = reader.Read(block, 0, block.Length)) != 0)
                {
                    if (body.Length + count > ResponseLimit) throw new InvalidDataException();
                    body.Append(block, 0, count);
                }
                return new Reply(code, body.ToString());
            }
        }
    }

    internal sealed class ReleaseData
    {
        public string tag_name { get; set; }
        public bool? draft { get; set; }
        public bool? prerelease { get; set; }
        public ReleaseAsset[] assets { get; set; }
    }
    internal sealed class ReleaseAsset
    {
        public string name { get; set; }
        public string state { get; set; }
        public long size { get; set; }
    }

    static UpdateResult ReadRelease(string body, Version installed)
    {
        ReleaseData release = null;
        try { release = new JavaScriptSerializer().Deserialize<ReleaseData>(body ?? ""); }
        catch (ArgumentException) {}
        catch (InvalidOperationException) {}
        if (release == null || release.draft != false || release.prerelease != false)
            return new UpdateResult(UpdateState.Failed, "GitHub 返回的正式发行信息无法识别。");
        var match = StableTag.Match(release.tag_name ?? "");
        int major, minor, patch;
        if (!match.Success || !Int32.TryParse(match.Groups[1].Value, out major) ||
            !Int32.TryParse(match.Groups[2].Value, out minor) || !Int32.TryParse(match.Groups[3].Value, out patch))
            return new UpdateResult(UpdateState.Failed, "发行标签不符合三段式稳定版本号。");
        const string name = "DeskAudio.exe";
        if (release.assets == null || !release.assets.Any(a => a != null && a.name == name && a.state == "uploaded" && a.size > 0))
            return new UpdateResult(UpdateState.Failed, "该版本尚未提供可下载的 DeskAudio.exe。", release.tag_name);
        string asset = RepositoryUrl + "/releases/download/" + Uri.EscapeDataString(release.tag_name) + "/" + name;
        var latest = new Version(major, minor, patch);
        bool newer = latest > installed;
        string message = newer ? "发现新版本 " + release.tag_name + "。" :
            (latest == installed ? "已是最新版本 " + release.tag_name + "。" : "已安装版本高于 GitHub 的最新正式版。");
        return new UpdateResult(newer ? UpdateState.Available : UpdateState.Current, message, release.tag_name, asset);
    }

    internal static UpdateResult CheckCore(Version installed, Func<bool,string,Reply> fetch, Func<string> credential)
    {
        Reply reply = fetch(false, null);
        if (reply.Code == 200) return ReadRelease(reply.Body, installed);
        if (reply.Code == 404 || reply.Code == 401 || reply.Code == 403 || reply.Code == 429)
        {
            string token = credential();
            if (token != null)
            {
                if (!ValidToken(token)) return new UpdateResult(UpdateState.NeedsAccess, "请检查更新令牌的格式。");
                reply = fetch(false, token);
                if (reply.Code == 200) return ReadRelease(reply.Body, installed);
            }
            // Private resources and an absent Release can both yield 404. Probe the releases
            // collection with the same credential to confirm read permission before deciding.
            if (reply.Code == 404)
            {
                reply = fetch(true, token);
                if (reply.Code == 200)
                    return new UpdateResult(UpdateState.NoRelease, "此仓库尚无已发布的稳定版本。");
            }
            if (reply.RateLimited)
                return new UpdateResult(UpdateState.Failed, "GitHub 已限制请求频率，请稍后重试。");
            if (reply.Code == 401 || reply.Code == 403 || reply.Code == 404)
                return new UpdateResult(UpdateState.NeedsAccess, token == null ?
                    "请在更新页勾选“使用本机 GitHub 登录”，或设置此仓库的只读令牌。" : "GitHub 无法授权读取此仓库，请检查账号、令牌有效期和仓库内容读取权限。");
        }
        return new UpdateResult(UpdateState.Failed, "GitHub 暂时无法完成检查（HTTP " + reply.Code.ToString(CultureInfo.InvariantCulture) + "）。");
    }

    public static UpdateResult Check()
    {
        try { return CheckCore(InstalledVersion, Request, GetCredential); }
        catch (CryptographicException) { return new UpdateResult(UpdateState.NeedsAccess, "此用户无法解密已保存的更新令牌，请重新设置。"); }
        catch (Exception) { return new UpdateResult(UpdateState.Failed, "更新检查未完成，请检查网络连接后重试。"); }
    }
}
