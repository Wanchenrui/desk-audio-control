using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;

internal static class TuningTest
{
    static int count;
    static void Check(bool ok,string title) { if (!ok) throw new Exception(title); count++; }
    static void Throws(Action act,string title)
    {
        try { act(); } catch (InvalidOperationException) { Check(true,title); return; }
        throw new Exception(title);
    }
    public static void Main()
    {
        var blank = new int[0]; int[] source={35,40,45}, target={20,30,40};
        Check(Landing.Plan(50,80,source,target,false)==40,"transfer offset from learned anchors");
        Check(Landing.Plan(95,80,source,target,true)<=40,"headset historical cap");
        Check(Landing.Plan(20,83,blank,blank,true)==35,"cold headset ceiling");
        Check(Landing.Plan(80,0,source,target,true)==0,"zero target stays silent");
        Check(Landing.Plan(40,73,blank,blank,false)==73,"untrained speaker preserves gain");
        Check(Landing.Plan(40,12,blank,blank,true)==12,"cold start never raises level");
        var anchors=new[]{new GainPoint(20,-32),new GainPoint(40,-16),new GainPoint(100,0)};
        Check(Landing.FromDb(-24,anchors)==30,"interpolate native gain between measured endpoint levels");
        Check(Landing.FromDb(-60,anchors)==20 && Landing.FromDb(8,anchors)==100,"clamp gain outside known range");
        var folder=Path.Combine(Path.GetTempPath(),"DeskAudio.Tune."+Guid.NewGuid().ToString("N"));
        try {
            var settings=new SettingsFile(folder,()=>new Dictionary<string,string>());
            var levels=new Dictionary<string,int> { {"port",80} };
            var tuning=new VolumeTuning(settings,id=>levels[id],(id,v)=>levels[id]=v);
            tuning.Learn("group",30); tuning.Learn("group",35);
            Check(tuning.History("group").SequenceEqual(new[]{30,35}),"stable per-device comfort");
            tuning.Learn("db",40,-18); Check(tuning.Anchors("db")[0].Db==-18,"device-native gain persists with comfort level");
            Check(tuning.History("other").Length==0,"independent histories");
            var tx=tuning.Begin("port","route",10);
            Check(levels["port"]==10,"attenuate before route");
            tuning.Step(tx,23);
            Check(levels["port"]==23,"ramp checkpoint");
            var restarted=new VolumeTuning(new SettingsFile(folder,null),id=>levels[id],(id,v)=>levels[id]=v);
            Check(restarted.Restore("port","route") && levels["port"]==80,"restart restores original level");
            tx=tuning.Begin("port","next",12);
            levels["port"]=44;
            Throws(()=>tuning.Step(tx,20),"manual adjustment stops ramp");
            Throws(()=>tuning.Restore("port","next"),"undo respects user edit");
            Check(levels["port"]==44,"external level preserved");
            tx=tuning.Begin("port","one",15); restarted.Begin("port","two",15);
            Throws(()=>tuning.Step(tx,16),"other desktop owns an identical current level");
            Check(!tuning.Restore("port","one") && levels["port"]==15,"stale desktop cannot undo newer ramp");
            tuning.Forget("group"); Check(tuning.History("group").Length==0,"forget calibration");
        } finally { Directory.Delete(folder,true); }

        var silent=Analyze(0,0,0,false);
        Check(silent.Silent && silent.GainChange==0,"silence never increases gain");
        Check(Analyze(.002,.015,0,false).Low,"low voice");
        Check(Analyze(.002,1.6,0,false).Clipping,"clipped voice");
        Check(Analyze(.09,.22,0,false).Noisy,"noisy quiet phase");
        Check(Analyze(.06,.18,50,false).HumHz==50,"50 Hz hum");
        Check(Analyze(.06,.18,60,false).HumHz==60,"60 Hz hum");
        Check(Analyze(.06,.18,0,false).HumHz==0,"other tones not hum");
        Check(Analyze(.03,.3,0,true).EchoRisk,"correlated render envelope");
        Check(!silent.EchoAssessed,"no output activity is inconclusive");

        var catalog=EqPreset.Load();
        var hd=EqPreset.Match(catalog,"耳机 (Sennheiser HD 600 Stereo)");
        Check(hd!=null && hd.Model=="Sennheiser HD 600","measured model match");
        Check(EqPreset.Match(catalog,"HD 6000 USB")==null,"model number boundary");
        Check(EqPreset.Match(catalog,"USB DAC")==null,"unknown stays unmatched");
        Check(hd.Export().Contains("Preamp:") && hd.Export().Contains("Filter 1:"),"Equalizer APO export");
        var a=hd.Preview(false); var b=hd.Preview(true);
        Check(a.Length==b.Length && !a.SequenceEqual(b),"A/B uses actual filter");
        Check(a.SequenceEqual(EqPreset.Reference()),"comfort reference is the flat A sample");
        Check(catalog.Length==16 && catalog.All(p=>p.Export().Contains("Filter 10:") && p.Export().Contains("oratory1990")),"complete measured catalog with attribution");
        Check(a.Max(v=>Math.Abs((int)v))<4000 && b.Max(v=>Math.Abs((int)v))<4000,"bounded audition");
        Console.WriteLine(count+" P3 tuning checks passed.");
    }

    static MicReport Analyze(double quiet,double voice,int hum,bool echo)
    {
        var analysis=new MicAnalysis();
        for(int block=0;block<100;block++)
        {
            var samples=new short[MicAnalysis.Block];
            double render=.05+.65*(.5+.5*Math.Sin(block*.67))*(.65+.35*Math.Cos(block*.33));
            double amplitude=echo ? render*.3 : block<30 ? quiet : voice;
            double hz=block<30 && hum!=0 ? hum : 400;
            for(int i=0;i<samples.Length;i++) {
                double x=amplitude*Math.Sin((block*samples.Length+i)*2*Math.PI*hz/MicAnalysis.Rate);
                samples[i]=(short)(Math.Max(-1,Math.Min(1,x))*32767);
            }
            analysis.Add(samples, echo ? (float)render : 0);
        }
        return analysis.Finish();
    }
}
