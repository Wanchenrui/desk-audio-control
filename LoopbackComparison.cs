using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

internal sealed partial class TuningStream
{
    internal sealed class BandReading
    {
        public double[] Power,Hz;
        internal string Compare(BandReading after,EqPlan plan)
        {
            double peak=Power.Max();
            var bands=Enumerable.Range(0,Power.Length).Where(i=>Hz[i]>=30 && Hz[i]<=20000 &&
                Power[i]>peak*.00001 && after.Power[i]>0).ToArray();
            if (bands.Length<12) return "有效频带不足，请在空闲端口重新测量";
            var observed=bands.Select(i=>10*Math.Log10(after.Power[i]/Power[i])).ToArray();
            double rms=Math.Sqrt(observed.Select((v,k)=>Math.Pow(v-plan.Expected(Hz[bands[k]]),2)).Average());
            double change=Math.Sqrt(observed.Average(v=>v*v));
            return String.Format("{0} 个频带 · 环回前后变化 RMS {1:F1} dB · 相对规划频响偏差 RMS {2:F1} dB。{3}",
                bands.Length,change,rms,change<.3 ? "该取样点未观察到明显变化。" : "读数取自系统环回。");
        }
    }
    static short[] ProbeTone()
    {
        const int rate=EqPreset.Rate;
        // Repeat the same low-level broadband tile so frame alignment is unimportant.
        var random=new Random(3381); var tile=new short[rate/4];
        for (int i=0;i<tile.Length;i++) tile[i]=(short)random.Next(-750,751);
        var tone=new short[rate*4];
        for (int i=0;i<tone.Length;i++) {
            double envelope=Math.Min(1,Math.Min(i,tone.Length-i-1)/(rate*.05));
            tone[i]=(short)(envelope*tile[i%tile.Length]);
        }
        return tone;
    }
    internal static BandReading MeasureBands(string endpoint,Func<string> guard,CancellationToken cancel)
    {
        uint self;
        using (var process=Process.GetCurrentProcess()) self=(uint)process.Id;
        Func<string> exclusive=delegate {
            string blocked=guard();
            if (blocked!=null) return blocked;
            bool other=false;
            AudioCore.ActiveSessions(endpoint,pid=>{ if (pid!=self) other=true; });
            return other ? "请暂停此端口上其他播放后再测" : null;
        };
        Check(exclusive,cancel);
        using (var stop=CancellationTokenSource.CreateLinkedTokenSource(cancel))
        using (var stream=new TuningStream(endpoint,EqPreset.Rate,typeof(ICapture),0x20000)) {
            var fft=new MicSpectrum(EqPreset.Rate);
            var sum=new double[fft.Centers.Length];
            var block=new short[EqPreset.Rate/20];
            var capture=(ICapture)stream.service;
            int used=0,blocks=0;
            stream.Start();
            var play=Task.Run(()=>Audition(endpoint,ProbeTone(),exclusive,stop.Token));
            try {
                var clock=Stopwatch.StartNew(); long nextGuard=0;
                while (blocks<46) {
                    cancel.ThrowIfCancellationRequested();
                    if (play.IsFaulted || play.IsCanceled) play.GetAwaiter().GetResult();
                    if (clock.ElapsedMilliseconds>5600) throw new TimeoutException("环回未取得足够的参考音");
                    if (clock.ElapsedMilliseconds>=nextGuard) { Check(exclusive,cancel); nextGuard=clock.ElapsedMilliseconds+300; }
                    uint packet; capture.GetNextPacketSize(out packet);
                    if (packet==0) { Thread.Sleep(5); continue; }
                    IntPtr data; uint frames,flags; ulong pos,qpc;
                    capture.GetBuffer(out data,out frames,out flags,out pos,out qpc);
                    try {
                        if ((flags&1)!=0 && blocks>0) throw new InvalidOperationException("环回采样中断，请重测");
                        for (int offset=0;offset<frames && blocks<46;) {
                            int n=Math.Min(block.Length-used,(int)frames-offset);
                            if ((flags&2)!=0) Array.Clear(block,used,n);
                            else Marshal.Copy(IntPtr.Add(data,offset*2),block,used,n);
                            used+=n; offset+=n;
                            if (used<block.Length) continue;
                            used=0;
                            if (blocks==0 && !block.Any(x=>Math.Abs((int)x)>2)) continue;
                            if (++blocks<=6) continue;
                            var energy=fft.Energy(block);
                            for (int i=0;i<sum.Length;i++) sum[i]+=energy[i];
                        }
                    } finally { capture.ReleaseBuffer(frames); }
                }
                return new BandReading { Power=sum,Hz=fft.Centers };
            } finally {
                stop.Cancel(); Array.Clear(block,0,block.Length);
                try { play.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            }
        }
    }
}
