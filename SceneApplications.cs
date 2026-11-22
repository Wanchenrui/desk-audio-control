using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

// Publish a complete, immutable classification table per observation, including retries on other threads.
internal sealed class SceneApplications
{
    readonly string path;
    readonly object sync=new object();
    static readonly Dictionary<string,string> Empty=new Dictionary<string,string>();
    Dictionary<string,string> current=Empty;
    DateTime modified;
    long length=-1;

    internal SceneApplications(string path) { this.path=path; }
    internal IDictionary<string,string> Read()
    {
        lock (sync) {
            try {
                var file=new FileInfo(path);
                long size=file.Length;
                DateTime stamp=file.LastWriteTimeUtc;
                if (stamp==modified && size==length) return current;
                if (size<=65536) {
                    var json=new JavaScriptSerializer { MaxJsonLength=65536,RecursionLimit=4 };
                    var raw=json.DeserializeObject(File.ReadAllText(path)) as Dictionary<string,object>;
                    if (raw!=null && raw.Count<=256) {
                        var next=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var pair in raw) {
                            string name=pair.Key.Trim();
                            if (name.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)) name=name.Substring(0,name.Length-4);
                            string category=pair.Value as string;
                            if (name.Length==0 || name.Length>128 || name.IndexOfAny(Path.GetInvalidFileNameChars())>=0 ||
                                !Categories.Contains(category) || next.ContainsKey(name)) return current;
                            next.Add(name,category);
                        }
                        current=next;
                    }
                }
                modified=stamp; length=size;
            } catch (FileNotFoundException) { Reset(); }
            catch (DirectoryNotFoundException) { Reset(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            return current;
        }
    }
    void Reset() { current=Empty; modified=default(DateTime); length=-1; }
    static readonly string[] Categories={"会议","音乐","影音","游戏","专注","录制","离开"};
    internal static string Category(string name,IDictionary<string,string> user,out bool knownMedia)
    {
        string builtin=BuiltIn(name.ToLowerInvariant()),category;
        bool protectedApp=builtin=="会议" || builtin=="录制" || builtin=="离开";
        if (protectedApp || !user.TryGetValue(name,out category)) category=builtin;
        // Only a known media executable may exempt shared playback from click-to-confirm.
        knownMedia=Media(builtin) && Media(category);
        return category;
    }
    static bool Media(string kind) { return kind=="音乐" || kind=="影音" || kind=="游戏"; }
    static string BuiltIn(string name)
    {
        switch (name) {
            case "teams": case "ms-teams": case "zoom": case "wemeetapp": case "webex": case "skype": return "会议";
            case "spotify": case "foobar2000": case "cloudmusic": case "qqmusic": case "musicbee": return "音乐";
            case "vlc": case "mpv": case "mpc-hc64": case "potplayermini64": return "影音";
            case "cs2": case "dota2": case "valorant-win64-shipping": case "minecraft.windows": case "league of legends": return "游戏";
            case "code": case "devenv": case "winword": case "onenote": case "obsidian": return "专注";
            case "obs64": case "audacity": case "reaper": case "soundrecorder": return "录制";
            case "lockapp": case "logonui": return "离开";
            default: return null;
        }
    }
}
