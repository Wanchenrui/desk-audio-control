using System;
using System.Collections.Generic;
using System.Linq;

internal sealed class MicReport
{
    public double SpeechDb, NoiseDb, PeakDb, EchoCorrelation;
    public bool Silent, Low, Clipping, Noisy, EchoAssessed, EchoRisk;
    public bool NoSpeech, QuietContaminated, Rumble, Hiss, NarrowBand;
    public int HumHz, VoiceBlocks, CleanQuietBlocks, EchoDelayMs;
    public double GainDb { get { return MicAdvisor.Adjustment(this); } }
    public int GainChange
    {
        get {
            return Math.Sign(GainDb)*(int)Math.Ceiling(Math.Abs(GainDb));
        }
    }
    public string Describe()
    {
        var lines=new List<string>();
        lines.Add(String.Format("说话 {0:F1} dBFS　安静 {1:F1} dBFS　峰值 {2:F1} dBFS",SpeechDb,NoiseDb,PeakDb));
        if (Silent) lines.Add("几乎无输入。请检查静音、端口和连接后重测。");
        else if (NoSpeech) lines.Add("说话段未检测到足够语音，请重新检查。");
        else if (Low) lines.Add("说话电平偏低。靠近话筒或提高输入音量后重测。");
        if (Clipping) lines.Add("检测到削波。请降低输入音量。");
        if (QuietContaminated) lines.Add("安静段混入了疑似语音，请保持安静后重测。");
        if (Rumble && HumHz==0) lines.Add("安静段以低频隆隆声为主，请检查桌面振动或风声。");
        if (Hiss) lines.Add("安静段有持续高频嘶声。");
        if (NarrowBand) lines.Add("语音高频成分持续偏少，可能使用了窄带输入。");
        if (Noisy && HumHz==0 && !QuietContaminated && !Rumble && !Hiss) lines.Add("安静段电平偏高。远离风扇，确认安静段无人说话后重测。");
        if (HumHz!=0) lines.Add("检测到 "+HumHz+" Hz 嗡声。请检查供电、屏蔽线和接口。");
        if (EchoRisk) lines.Add("输入随播放电平变化，估计延迟约 "+EchoDelayMs+" ms。可改用耳机复测。");
        if (!EchoAssessed) lines.Add("播放电平变化不足，无法评估回声。");
        if (!Silent && !NoSpeech && !QuietContaminated && !Low && !Noisy && !Clipping && !EchoRisk && HumHz==0 && !NarrowBand && !Hiss && !Rumble) lines.Add("本次采样电平适中，未检出明显异常。");
        lines.Add("有效语音 "+VoiceBlocks+" 块，安静 "+CleanQuietBlocks+" 块 · "+(VoiceBlocks>=15 && !QuietContaminated ? "电平判断可信度较高" : "电平判断证据有限"));
        return String.Join(Environment.NewLine,lines);
    }
}

// Consume 50 ms at a time. Retain energy envelopes and line-frequency accumulators only.
internal sealed class MicAnalysis
{
    public const int Rate=32000, Block=1600, QuietBlocks=30, TotalBlocks=100;
    readonly List<double> mic=new List<double>(), render=new List<double>();
    readonly double[] lineReal=new double[2], lineImag=new double[2], linePower=new double[2];
    static readonly double[][] cos=Basis(true), sin=Basis(false);
    double quietEnergy, maxPeak;
    int clipped;
    readonly MicSpectrum spectrum=new MicSpectrum();
    readonly List<double[]> bands=new List<double[]>();
    readonly List<double> crossings=new List<double>();
    public int QuietExtensions { get; private set; }
    public bool QuietPhase { get { return mic.Count<QuietBlocks+QuietExtensions; } }
    public bool Complete { get { return mic.Count>=TotalBlocks+QuietExtensions; } }
    bool SpeechShape(int i)
    {
        double total=bands[i].Sum();
        return total>0 && Share(i,120,5000)>.50 && crossings[i]>.004 && crossings[i]<.36;
    }
    double Share(int i,double low,double high)
    {
        double total=0, part=0;
        for (int j=0;j<bands[i].Length;j++) {
            total+=bands[i][j];
            if (spectrum.Centers[j]>=low && spectrum.Centers[j]<high) part+=bands[i][j];
        }
        return total==0 ? 0 : part/total;
    }
    // The live UI can extend the quiet prompt by up to one second.
    public void ExtendQuietIfNeeded()
    {
        if (mic.Count!=QuietBlocks+QuietExtensions || QuietExtensions>=20) return;
        double floor=QuietFloor();
        if (Enumerable.Range(0,mic.Count).Count(i=>Contaminated(i,floor))>QuietExtensions+2) QuietExtensions++;
    }
    double QuietFloor()
    {
        var values=mic.Take(QuietBlocks+QuietExtensions).OrderBy(v=>v).ToArray();
        return values[values.Length/5];
    }
    bool Contaminated(int i,double floor)
    {
        // Stationary broad noise and mains hum alone must not trigger a speech retry.
        bool wideSpeech=bands[i].Count(v=>v>bands[i].Max()*.06)>=4 && Share(i,150,3500)>.75;
        return SpeechShape(i) && mic[i]>.004 && (mic[i]>floor*2 || wideSpeech);
    }
    static double[][] Basis(bool cosine)
    {
        return new[]{50,60}.Select(hz=>Enumerable.Range(0,Rate/2).Select(i=>{
            double angle=2*Math.PI*hz*i/Rate;
            return cosine ? Math.Cos(angle) : Math.Sin(angle);
        }).ToArray()).ToArray();
    }
    public void Add(short[] data,float playback)
    {
        if (data.Length!=Block || Complete) throw new ArgumentException("无效的采样块");
        int block=mic.Count;
        double power=0;
        for (int i=0;i<Block;i++) {
            double x=data[i]/32768.0;
            power+=x*x; maxPeak=Math.Max(maxPeak,Math.Abs(x));
            if (Math.Abs(x)>=.995) clipped++;
            if (block>=QuietBlocks) continue;
            int at=(block*Block+i)%(Rate/2);
            for (int f=0;f<2;f++) { lineReal[f]+=x*cos[f][at]; lineImag[f]+=x*sin[f][at]; }
        }
        if (block<QuietBlocks) {
            quietEnergy+=power;
            if ((block+1)%10==0) for (int f=0;f<2;f++) {
                linePower[f]+=2*(lineReal[f]*lineReal[f]+lineImag[f]*lineImag[f])/(Rate/2);
                lineReal[f]=lineImag[f]=0;
            }
        }
        int changes=0;
        for (int i=1;i<Block;i++) if ((data[i]<0)!=(data[i-1]<0)) changes++;
        crossings.Add(changes/(double)Block);
        bands.Add(spectrum.Energy(data));
        mic.Add(Math.Sqrt(power/Block)); render.Add(playback);
    }
    static double Db(double amplitude) { return 20*Math.Log10(Math.Max(.00001,amplitude)); }
    public MicReport Finish()
    {
        if (!Complete) throw new InvalidOperationException("采样未完成，请重测");
        var result=new MicReport();
        int quietEnd=QuietBlocks+QuietExtensions;
        double floor=QuietFloor();
        var clean=Enumerable.Range(0,quietEnd).Where(i=>!Contaminated(i,floor)).ToArray();
        result.CleanQuietBlocks=clean.Length;
        result.QuietContaminated=quietEnd-clean.Length>=3;
        double noise=clean.Length==0 ? floor : Math.Sqrt(clean.Average(i=>mic[i]*mic[i]));
        var voices=Enumerable.Range(quietEnd,TotalBlocks-QuietBlocks)
            .Where(i=>SpeechShape(i) && mic[i]>Math.Max(.0004,noise*1.6)).ToArray();
        result.VoiceBlocks=voices.Length; result.NoSpeech=voices.Length<4;
        double voice=result.NoSpeech ? 0 : voices.Select(i=>mic[i]).OrderBy(v=>v).ElementAt((voices.Length-1)*4/5);
        result.NoiseDb=Db(noise); result.SpeechDb=Db(voice); result.PeakDb=Db(maxPeak);
        result.Silent=result.PeakDb < -70; result.Low=!result.NoSpeech && result.SpeechDb < -32;
        result.Clipping=clipped>=3; result.Noisy=!result.QuietContaminated && result.NoiseDb > -42;
        if (clean.Length>=10 && result.Noisy) {
            result.Rumble=clean.Average(i=>Share(i,0,115))>.65;
            result.Hiss=clean.Average(i=>Share(i,5000,20000))>.55;
        }
        result.NarrowBand=voices.Length>=15 && voices.Average(i=>Share(i,1600,3900))>.03 &&
            voices.Average(i=>Share(i,4100,15000))<.001;
        int strongest=linePower[0]>linePower[1] ? 0 : 1;
        if (!result.QuietContaminated && result.NoiseDb > -65 && linePower[strongest]>.38*quietEnergy) result.HumHz=strongest==0 ? 50 : 60;
        // Look for correlated playback preceding the microphone envelope by 0–250 ms.
        // A missing meter is represented by -1, never interpreted as evidence of echo.
        for (int lag=0;lag<=5;lag++) {
            double a=0,b=0,aa=0,bb=0,ab=0; int n=0;
            for (int j=lag;j<mic.Count;j++) {
                double x=render[j-lag], y=mic[j];
                if (x<0) continue;
                n++; a+=x; b+=y; aa+=x*x; bb+=y*y; ab+=x*y;
            }
            if (n<60) continue;
            double vx=aa-a*a/n, vy=bb-b*b/n;
            if (vx/n < .0001) continue;
            result.EchoAssessed=true;
            if (vy/n > .000001) {
                double correlation=(ab-a*b/n)/Math.Sqrt(vx*vy);
                if (correlation>result.EchoCorrelation) { result.EchoCorrelation=correlation; result.EchoDelayMs=lag*50; }
            }
        }
        result.EchoRisk=result.EchoAssessed && result.EchoCorrelation>.82;
        return result;
    }
}
