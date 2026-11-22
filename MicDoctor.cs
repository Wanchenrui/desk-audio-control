using System;
using System.Collections.Generic;
using System.Linq;

internal sealed class MicReport
{
    public double SpeechDb, NoiseDb, PeakDb, EchoCorrelation;
    public bool Silent, Low, Clipping, Noisy, EchoAssessed, EchoRisk;
    public int HumHz;
    public int GainChange
    {
        get {
            if (Clipping) return -10;
            return Low && !Silent && !Noisy && HumHz==0 && !EchoRisk && SpeechDb-NoiseDb>=8 ? 8 : 0;
        }
    }
    public string Describe()
    {
        var lines=new List<string>();
        lines.Add(String.Format("说话 {0:F1} dBFS　安静 {1:F1} dBFS　峰值 {2:F1} dBFS",SpeechDb,NoiseDb,PeakDb));
        if (Silent) lines.Add("几乎无输入。请检查静音、端口和连接后重测。");
        else if (Low) lines.Add("说话电平偏低。靠近话筒或提高输入音量后重测。");
        if (Clipping) lines.Add("检测到削波。请降低输入音量。");
        if (Noisy) lines.Add("安静段电平偏高。远离风扇，确认安静段无人说话后重测。");
        if (HumHz!=0) lines.Add("检测到 "+HumHz+" Hz 嗡声。请检查供电、屏蔽线和接口。");
        if (EchoRisk) lines.Add("输入随播放电平变化，存在回声风险。可改用耳机或拉开音箱与话筒的距离。");
        if (!EchoAssessed) lines.Add("播放电平变化不足，无法评估回声。");
        if (!Silent && !Low && !Noisy && !Clipping && !EchoRisk && HumHz==0) lines.Add("本次采样电平适中，未检出明显异常。");
        return String.Join(Environment.NewLine,lines);
    }
}

// Consume 50 ms at a time. Retain energy envelopes and line-frequency accumulators only.
internal sealed class MicAnalysis
{
    public const int Rate=16000, Block=800, QuietBlocks=30, TotalBlocks=100;
    readonly List<double> mic=new List<double>(), render=new List<double>();
    readonly double[] lineReal=new double[2], lineImag=new double[2], linePower=new double[2];
    static readonly double[][] cos=Basis(true), sin=Basis(false);
    double quietEnergy, maxPeak;
    int clipped;
    static double[][] Basis(bool cosine)
    {
        return new[]{50,60}.Select(hz=>Enumerable.Range(0,Rate/2).Select(i=>{
            double angle=2*Math.PI*hz*i/Rate;
            return cosine ? Math.Cos(angle) : Math.Sin(angle);
        }).ToArray()).ToArray();
    }
    public void Add(short[] data,float playback)
    {
        if (data.Length!=Block || mic.Count>=TotalBlocks) throw new ArgumentException("无效的采样块");
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
        mic.Add(Math.Sqrt(power/Block)); render.Add(playback);
    }
    static double Db(double amplitude) { return 20*Math.Log10(Math.Max(.00001,amplitude)); }
    public MicReport Finish()
    {
        if (mic.Count!=TotalBlocks) throw new InvalidOperationException("采样未完成，请重测");
        var result=new MicReport();
        double voice=mic.Skip(QuietBlocks).OrderBy(v=>v).ElementAt(55);
        result.NoiseDb=Db(Math.Sqrt(quietEnergy/(QuietBlocks*Block)));
        result.SpeechDb=Db(voice); result.PeakDb=Db(maxPeak);
        result.Silent=result.PeakDb < -70; result.Low=result.SpeechDb < -32;
        result.Clipping=clipped>=3; result.Noisy=result.NoiseDb > -42;
        int strongest=linePower[0]>linePower[1] ? 0 : 1;
        if (result.NoiseDb > -65 && linePower[strongest]>.38*quietEnergy) result.HumHz=strongest==0 ? 50 : 60;
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
            if (vy/n > .000001) result.EchoCorrelation=Math.Max(result.EchoCorrelation,(ab-a*b/n)/Math.Sqrt(vx*vy));
        }
        result.EchoRisk=result.EchoAssessed && result.EchoCorrelation>.82;
        return result;
    }
}
