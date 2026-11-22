using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

// The main config is the commit point. Immutable, content-addressed fragments and
// a saved original allow recovery on either side of an interrupted File.Replace.
internal sealed class ApoBridge
{
    internal sealed class Entry
    {
        public string Guid { get; set; }
        public string Hash { get; set; }
        public string Model { get; set; }
        public int Strength { get; set; }
    }
    internal sealed class State
    {
        public byte[] Original { get; set; }
        public string OriginalHash { get; set; }
        public Entry[] Active { get; set; }
        public Entry[] Pending { get; set; }
    }
    readonly string folder,main,statePath;
    internal ApoBridge(string configFolder)
    {
        folder=Path.GetFullPath(configFolder);
        main=Path.Combine(folder,"config.txt");
        statePath=Path.Combine(folder,"DeskAudio-state.json");
    }
    internal static ApoBridge Detect()
    {
        string path=Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\EqualizerAPO","ConfigPath",null) as string;
        if (String.IsNullOrWhiteSpace(path)) path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"EqualizerAPO","config");
        return File.Exists(Path.Combine(path,"config.txt")) ? new ApoBridge(path) : null;
    }
    static string Hash(byte[] bytes)
    {
        using (var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");
    }
    static string EndpointGuid(string id)
    {
        Guid value;
        int start=id==null ? -1 : id.LastIndexOf('{');
        if (start<0 || !Guid.TryParseExact(id.Substring(start),"B",out value) || value==Guid.Empty)
            throw new InvalidOperationException("端口缺少可用于限定 EQ 的 GUID");
        return value.ToString("D");
    }
    static string Fragment(Entry e) { return "DeskAudio-"+e.Guid+"-"+e.Hash+".txt"; }
    static byte[] Config(byte[] original,Entry[] entries)
    {
        if (entries.Length==0) return original;
        var additions=new StringBuilder("\r\n");
        foreach (var e in entries.OrderBy(e=>e.Guid,StringComparer.Ordinal)) {
            additions.Append("Device: ").Append(e.Guid).Append("\r\nInclude: ").Append(Fragment(e)).Append("\r\n");
        }
        Encoding encoding=Encoding.ASCII;
        if (original.Take(2).SequenceEqual(new byte[]{0xff,0xfe})) encoding=Encoding.Unicode;
        if (original.Take(2).SequenceEqual(new byte[]{0xfe,0xff})) encoding=Encoding.BigEndianUnicode;
        return original.Concat(encoding.GetBytes(additions.ToString())).ToArray();
    }
    static void Validate(Entry[] entries)
    {
        if (entries!=null && entries.Length>128) throw new InvalidDataException("DeskAudio 最多可管理 128 个 EQ 端口，请先还原一个端口");
        if (entries==null || entries.Any(e=>e==null ||
            !System.Text.RegularExpressions.Regex.IsMatch(e.Hash??"",@"\A[0-9A-F]{64}\z") ||
            !System.Text.RegularExpressions.Regex.IsMatch(e.Guid??"",@"\A[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}\z")))
            throw new InvalidDataException("DeskAudio 的 EQ 备份无效");
    }
    State Load(byte[] current,out Entry[] owned)
    {
        owned=new Entry[0];
        if (!File.Exists(statePath)) return new State { Original=current,OriginalHash=Hash(current),Active=new Entry[0] };
        var s=JsonFile.Read<State>(statePath);
        if (s==null || s.Original==null || Hash(s.Original)!=s.OriginalHash) throw new InvalidDataException("原始 EQ 备份损坏");
        Validate(s.Active);
        owned=s.Active;
        if (s.Pending!=null) {
            Validate(s.Pending);
            owned=s.Active.Concat(s.Pending).ToArray();
            if (Config(s.Original,s.Pending).SequenceEqual(current)) s.Active=s.Pending;
        }
        if (!Config(s.Original,s.Active).SequenceEqual(current)) throw new IOException("APO 主配置已由其他程序修改，已保留现场与备份");
        return s;
    }
    internal string Applied(string endpoint,out bool managed)
    {
        managed=false;
        bool found=false;
        if (!File.Exists(main) || !File.Exists(statePath)) return null;
        string result=JsonFile.Locked(main,delegate {
            Entry[] owned;
            var entry=Load(File.ReadAllBytes(main),out owned).Active.FirstOrDefault(e=>e.Guid==EndpointGuid(endpoint));
            if (entry==null) return null;
            found=true;
            string path=Path.Combine(folder,Fragment(entry));
            if (!File.Exists(path)) return null;
            if (Hash(File.ReadAllBytes(path))!=entry.Hash) throw new IOException("DeskAudio 的 EQ 文件已被外部修改");
            return entry.Model+" · "+entry.Strength+"%";
        });
        managed=found; return result;
    }
    internal void Change(string endpoint,EqPlan plan,Action guard)
    {
        string id=EndpointGuid(endpoint);
        JsonFile.Locked(main,delegate {
            guard();
            if (!File.Exists(main)) throw new FileNotFoundException("未找到 Equalizer APO 的主配置");
            if (new FileInfo(main).Length>1024*1024) throw new IOException("APO 主配置超过可备份的大小");
            var current=File.ReadAllBytes(main);
            Entry[] previous;
            var s=Load(current,out previous);
            var entries=s.Active.Where(e=>e.Guid!=id).ToList();
            Entry next=null;
            byte[] body=null;
            if (plan!=null) {
                body=new UTF8Encoding(false).GetBytes("Stage: post-mix\r\nChannel: all\r\n"+plan.Filter.Export());
                next=new Entry { Guid=id,Hash=Hash(body),Strength=plan.Strength,Model=plan.Filter.Model };
                entries.Add(next);
            }
            foreach (var e in s.Active) {
                string path=Path.Combine(folder,Fragment(e));
                if (File.Exists(path) && Hash(File.ReadAllBytes(path))!=e.Hash)
                    throw new IOException("EQ 文件已由其他程序修改，已保留配置");
            }
            s.Pending=entries.ToArray();
            Validate(s.Pending);
            var desired=Config(s.Original,s.Pending);
            if (entries.Count==0 && previous.Length==0 && !File.Exists(statePath)) return true;
            // A prior interrupted transaction can own unused fragments. Reconcile them
            // before replacing the only ledger that records their names.
            RemoveUnused(previous,s.Active.Concat(s.Pending));
            JsonFile.Save(statePath,s);
            if (next!=null) {
                string path=Path.Combine(folder,Fragment(next));
                if (File.Exists(path)) {
                    if (Hash(File.ReadAllBytes(path))!=next.Hash) throw new IOException("EQ 文件名冲突");
                } else WriteAtomic(path,body,null);
            }
            guard();
            WriteAtomic(main,desired,current);
            RemoveUnused(previous,s.Pending);
            s.Active=s.Pending; s.Pending=null;
            JsonFile.Save(statePath,s);
            if (s.Active.Length==0) File.Delete(statePath);
            return true;
        });
    }
    void RemoveUnused(IEnumerable<Entry> owned,IEnumerable<Entry> keep)
    {
        var retained=new HashSet<string>(keep.Select(Fragment),StringComparer.Ordinal);
        foreach (var old in owned.Where(e=>!retained.Contains(Fragment(e)))) {
            string path=Path.Combine(folder,Fragment(old));
            if (File.Exists(path) && Hash(File.ReadAllBytes(path))==old.Hash) File.Delete(path);
        }
    }
    static void WriteAtomic(string destination,byte[] data,byte[] expected)
    {
        string temp=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            using (var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                file.Write(data,0,data.Length); file.Flush(true);
            }
            if (expected==null) { File.Move(temp,destination); return; }
            using (var file=new FileStream(destination,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete)) {
                using (var reader=new BinaryReader(file,Encoding.UTF8,true)) {
                    if (file.Length!=expected.Length || !reader.ReadBytes(expected.Length).SequenceEqual(expected))
                        throw new IOException("写入前 APO 主配置发生变化");
                }
                // Same-directory atomic replacement. The exact original is already in state.Original.
                File.Replace(temp,destination,null);
            }
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
