using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

internal static class JsonFile
{
    // The same Windows account can run a tray instance in more than one desktop session.
    public static T Locked<T>(string path,Func<T> operation)
    {
        string name;
        using (var hash = SHA256.Create())
            name = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))).Replace("-","");
        using (var mutex = new Mutex(false,@"Global\DeskAudio.Files." + name))
        {
            try { if (!mutex.WaitOne(3000)) throw new IOException("本地数据文件正在使用。"); }
            catch (AbandonedMutexException) { }
            try { return operation(); } finally { mutex.ReleaseMutex(); }
        }
    }

    public static T Read<T>(string path)
    {
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("文件过大。");
        return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
    }

    public static void Save<T>(string path, T value)
    {
        SaveJson(path,new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Serialize(value));
    }
    internal static void SaveJson(string path,string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal sealed class SettingsFile
{
    public sealed class Document
    {
        public int Version { get; set; }
        public Dictionary<string,string> Values { get; set; }
    }

    readonly object sync = new object();
    readonly string path;
    Dictionary<string,string> values;
    public string Warning { get; private set; }

    // A machine that has never run DeskAudio starts with the automatic features on. Upgrades keep their own switches.
    public static Dictionary<string,string> FreshDefaults()
    {
        return new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) { { "PreferNewOutput","1" }, { "EnableTuning","1" } };
    }

    public SettingsFile(string folder, Func<Dictionary<string,string>> legacy)
    {
        path = Path.Combine(folder, "settings.json");
        try
        {
            values = JsonFile.Locked(path,delegate {
                if (File.Exists(path)) return LoadValues();
                var initial = new Dictionary<string,string>(legacy(), StringComparer.OrdinalIgnoreCase);
                Persist(initial);
                return initial;
            });
        }
        catch (Exception e)
        {
            values = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            Warning = "配置未加载，已进入手动模式。 " + e.Message;
        }
    }

    Dictionary<string,string> LoadValues()
    {
        var document = JsonFile.Read<Document>(path);
        if (document == null || document.Version != 1 || document.Values == null)
            throw new InvalidDataException("配置文件版本或内容无法识别。");
        return new Dictionary<string,string>(document.Values,StringComparer.OrdinalIgnoreCase);
    }

    public string Get(string key)
    {
        lock (sync) { string value; return values.TryGetValue(key,out value) ? value : null; }
    }

    public string[] Keys(string prefix)
    {
        lock (sync) return values.Keys.Where(k => k.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Substring(prefix.Length)).ToArray();
    }

    // A volume transaction checkpoints intent and readback around a hardware write.
    // Keep the same reentrant file mutex across those checkpoints, including other desktops.
    public T WithFreshValues<T>(Func<T> operation)
    {
        lock (sync)
        {
            if (Warning!=null) throw new IOException(Warning);
            return JsonFile.Locked(path,delegate { values=LoadValues(); return operation(); });
        }
    }

    public void Put(string key, string value)
    {
        lock (sync)
        {
            if (Warning != null) throw new IOException(Warning);
            values = JsonFile.Locked(path,delegate {
                var next = LoadValues();
                string old;
                if (next.TryGetValue(key,out old) && old == value) return next;
                if (value == null) next.Remove(key); else next[key] = value;
                Persist(next);
                return next;
            });
        }
    }

    void Persist(Dictionary<string,string> next) { JsonFile.Save(path,new Document { Version = 1, Values = next }); }
}

internal static class AppConfig
{
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskAudioControl");
    public static readonly SettingsFile Store = new SettingsFile(Folder, Migrate);
    static Dictionary<string,string> Migrate()
    {
        var values = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        using (var root = Registry.CurrentUser.OpenSubKey(@"Software\DeskAudioControl"))
        {
            if (root == null) return SettingsFile.FreshDefaults();
            foreach (string name in new[] { "PreferNewOutput", "BeforeInput", "AutomaticUpdates", "UpdateStatusVersion",
                "UpdateStatus", "UpdateAttemptUtc", "LastNotifiedUpdate", "UseSignedInGitHub" })
            {
                var value = root.GetValue(name);
                if (value != null) values[name] = Convert.ToString(value,CultureInfo.InvariantCulture);
            }
            foreach (string section in new[] { "DeviceRoles","AmbiguousUsbIdentities" })
                using (var sub = root.OpenSubKey(section))
                    if (sub != null)
                        foreach (string name in sub.GetValueNames())
                            values[section + "/" + name] = Convert.ToString(sub.GetValue(name),CultureInfo.InvariantCulture);
        }
        return values;
    }
}
