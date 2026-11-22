using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

internal sealed class EqPreset
{
    public const int Rate=48000;
    public string Model { get; private set; }
    public override string ToString() { return Model; }
    public string Source { get { return "https://github.com/jaakkopasanen/AutoEq/tree/7ae0f56d53074872b028649617a22bbb4232feb7/results/oratory1990/over-ear/"+Uri.EscapeDataString(Model); } }
    string parameters;
    double preamp;
    readonly List<Band> bands=new List<Band>();
    static string Resource(string name)
    {
        using (var file=typeof(EqPreset).Assembly.GetManifestResourceStream(name))
        using (var reader=new StreamReader(file,Encoding.UTF8)) return reader.ReadToEnd().Replace("\r","");
    }
    internal static EqPreset[] Load()
    {
        var list=new List<EqPreset>();
        foreach (var section in Resource("DeskAudio.EQ.txt").Trim().Split(new[]{"\n\n"},StringSplitOptions.RemoveEmptyEntries)) {
            var lines=section.Split('\n');
            var item=new EqPreset { Model=lines[0].Trim('[',']'),parameters=String.Join("\n",lines.Skip(1)) };
            item.preamp=Number(lines[1].Split(' ')[1]);
            foreach (var row in lines.Skip(2)) {
                var parts=row.Split(' ');
                item.bands.Add(new Band(parts[3],Number(parts[5]),Number(parts[8]),Number(parts[11])));
            }
            list.Add(item);
        }
        return list.ToArray();
    }
    static double Number(string value) { return Double.Parse(value,CultureInfo.InvariantCulture); }
    static string Normalize(string value)
    {
        string clean=Regex.Replace(value??"",@"\b(headphones?|headset|speakers?|stereo|bluetooth)\b|耳机|扬声器|立体声","",RegexOptions.IgnoreCase);
        return Regex.Replace(clean.ToLowerInvariant(),"[^a-z0-9]","");
    }
    internal static EqPreset Match(IEnumerable<EqPreset> catalog,params string[] names)
    {
        var keys=new HashSet<string>(names.Select(Normalize));
        var matches=catalog.Where(p=>keys.Contains(Normalize(p.Model)) ||
            keys.Contains(Normalize(p.Model.Substring(p.Model.IndexOf(' ')+1)))).ToArray();
        return matches.Length==1 ? matches[0] : null;
    }
    public string Parameters { get { return parameters.Replace("\n",Environment.NewLine); } }
    public string Export()
    {
        return "# "+Model+" | oratory1990 measurements / AutoEq\r\n# "+Source+"\r\n"+
            Parameters+"\r\n\r\n# "+Resource("DeskAudio.EQ.License").Trim().Replace("\n","\r\n# ")+"\r\n";
    }

    // RBJ peaking and Q-based shelving filters used for the local A/B sample.
    sealed class Band
    {
        readonly double b0,b1,b2,a1,a2;
        public Band(string type,double hz,double gain,double q)
        {
            double w=2*Math.PI*hz/Rate, c=Math.Cos(w), a=Math.Pow(10,gain/40), alpha=Math.Sin(w)/(2*q), d;
            if (type=="PK") {
                d=1+alpha/a; b0=(1+alpha*a)/d; b1=-2*c/d; b2=(1-alpha*a)/d; a1=b1; a2=(1-alpha/a)/d;
            } else {
                if (type!="LSC" && type!="HSC") throw new InvalidDataException("不支持的 EQ 滤波器");
                double s=type=="LSC" ? 1 : -1, t=2*Math.Sqrt(a)*alpha, m=a-1, p=a+1;
                d=p+s*m*c+t; b0=a*(p-s*m*c+t)/d; b1=2*s*a*(m-s*p*c)/d;
                b2=a*(p-s*m*c-t)/d; a1=-2*s*(m+s*p*c)/d; a2=(p+s*m*c-t)/d;
            }
        }
        public void Filter(double[] samples)
        {
            double z1=0,z2=0;
            for(int i=0;i<samples.Length;i++) {
                double x=samples[i], y=b0*x+z1;
                z1=b1*x-a1*y+z2; z2=b2*x-a2*y; samples[i]=y;
            }
        }
    }

    public short[] Preview(bool equalized)
    {
        return MakePreview(equalized ? this : null);
    }
    public static short[] Reference() { return MakePreview(null); }
    static short[] MakePreview(EqPreset eq)
    {
        var random=new Random(2026);
        var sample=new double[Rate*2];
        double smooth=0;
        for(int i=0;i<sample.Length;i++) {
            double t=i/(double)Rate, noise=random.NextDouble()*2-1;
            smooth=.96*smooth+.04*noise;
            sample[i]=(.55*smooth+.08*noise+.17*Math.Sin(2*Math.PI*165*t)+.1*Math.Sin(2*Math.PI*660*t))*
                Math.Min(1,Math.Min(i,sample.Length-1-i)/(Rate*.05));
        }
        if (eq!=null) {
            double gain=Math.Pow(10,eq.preamp/20);
            for(int i=0;i<sample.Length;i++) sample[i]*=gain;
            foreach(var band in eq.bands) band.Filter(sample);
        }
        // Compare at the same RMS, with a second peak ceiling for both versions.
        double scale=Math.Min(.014/Math.Sqrt(sample.Average(x=>x*x)),.09/sample.Max(x=>Math.Abs(x)));
        return sample.Select(x=>(short)Math.Round(32767*x*scale)).ToArray();
    }
}
