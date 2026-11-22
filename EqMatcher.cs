using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

internal sealed class EqCandidate
{
    public EqPreset Preset;
    public int Confidence;
    public string Reason;
}
internal static class EqMatcher
{
    static readonly Regex words=new Regex(@"[\p{L}\p{Nd}]+",RegexOptions.Compiled);
    static readonly Regex decorations=new Regex(@"(?<![a-z0-9])(?:"+
        String.Join("|",EqPreset.Resource("DeskAudio.EQ.Aliases").Split('\n')
        .Where(s=>s.Length>0 && s[0]!='#').OrderByDescending(s=>s.Length).Select(Regex.Escape))+@")(?![a-z0-9])",
        RegexOptions.IgnoreCase|RegexOptions.CultureInvariant|RegexOptions.Compiled);

    static readonly HashSet<string> generic=new HashSet<string>(new[]{"one","pro","air","plus","max","mini","lite","wireless","edition"});
    static string Words(string name)
    {
        return String.Join(" ",words.Matches((name??"").ToLowerInvariant()).Cast<Match>().Select(m=>m.Value));
    }
    internal static EqCandidate[] Rank(IEnumerable<EqPreset> catalog,params string[] names)
    {
        var raw=names.Select(Words).Where(s=>s.Length>0).ToArray();
        var clean=raw.Select(s=>Words(decorations.Replace(s," "))).Where(s=>s.Length>0).ToArray();
        var found=new List<EqCandidate>();
        foreach (var p in catalog) {
            string full=Words(p.Model);
            int separator=full.IndexOf(' ');
            string shortName=separator<0 ? "" : full.Substring(separator+1);
            string key=full.Replace(" ",""), shortKey=shortName.Replace(" ","");
            bool distinctive=shortKey.Length>=3 && shortKey.Any(Char.IsLetter) && shortName.Split(' ').Any(word=>!generic.Contains(word));
            int score=raw.Contains(full) ? 100 : clean.Any(n=>n.Replace(" ","")==key) ? 98 :
                distinctive && clean.Any(n=>n.Replace(" ","")==shortKey) ? 91 :
                clean.Any(n=>(" "+n+" ").Contains(" "+full+" ")) ? 78 :
                distinctive && clean.Any(n=>(" "+n+" ").Contains(" "+shortName+" ")) ? 65 : 0;
            if (score>0) found.Add(new EqCandidate { Preset=p,Confidence=score,
                Reason=score>=98 ? "完整厂牌和型号匹配" : score>=90 ? "型号匹配" : "名称包含型号，请确认具体版本" });
        }
        return found.OrderByDescending(c=>c.Confidence).ThenBy(c=>c.Preset.Model,StringComparer.Ordinal).Take(4).ToArray();
    }
    internal static EqPreset Unique(EqCandidate[] ranked)
    {
        return ranked.Length>0 && ranked[0].Confidence>=90 &&
            (ranked.Length==1 || ranked[0].Confidence-ranked[1].Confidence>=6) ? ranked[0].Preset : null;
    }
}
