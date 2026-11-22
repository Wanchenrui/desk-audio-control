using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

// Short, user-initiated shared-mode WASAPI streams bound to an explicit endpoint.
internal sealed partial class TuningStream : IDisposable
{
    [StructLayout(LayoutKind.Sequential,Pack=2)]
    struct WaveFormat
    {
        public ushort Tag, Channels;
        public uint Rate, BytesPerSecond;
        public ushort Align, Bits, Extra;
    }
    [ComImport,Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IClient
    {
        void Initialize(int share,uint flags,long duration,long period,ref WaveFormat format,IntPtr session);
        void GetBufferSize(out uint size); void Latency(); void GetCurrentPadding(out uint frames);
        void Supported(); void Mix(); void Period(); void Start(); void Stop(); void Reset(); void SetEvent();
        void GetService(ref Guid id,[MarshalAs(UnmanagedType.IUnknown)] out object service);
    }
    [ComImport,Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICapture
    {
        void GetBuffer(out IntPtr data,out uint frames,out uint flags,out ulong position,out ulong qpc);
        void ReleaseBuffer(uint frames); void GetNextPacketSize(out uint frames);
    }
    [ComImport,Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IRender
    {
        void GetBuffer(uint frames,out IntPtr data); void ReleaseBuffer(uint frames,uint flags);
    }
    IClient client;
    object service;
    bool started;
    TuningStream(string endpoint,int rate,Type kind,uint extraFlags=0)
    {
        try {
            client=(IClient)AudioCore.ActivateOn(endpoint,typeof(IClient).GUID);
            var format=new WaveFormat { Tag=1,Channels=1,Rate=(uint)rate,BytesPerSecond=(uint)rate*2,Align=2,Bits=16 };
            // AUTOCONVERTPCM | SRC_DEFAULT_QUALITY allows the engine to use its native mix format.
            client.Initialize(0,0x88000000 | extraFlags,1000000,0,ref format,IntPtr.Zero);
            Guid iid=kind.GUID; client.GetService(ref iid,out service);
        } catch { Dispose(); throw; }
    }
    void Start() { client.Start(); started=true; }
    public void Dispose()
    {
        try { if (started) client.Stop(); } catch (COMException) { }
        finally {
            started=false;
            if (service!=null) { Marshal.ReleaseComObject(service); service=null; }
            if (client!=null) { Marshal.ReleaseComObject(client); client=null; }
        }
    }
    static void Check(Func<string> guard,CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        string reason=guard();
        if (reason!=null) throw new InvalidOperationException(reason);
    }
    public static MicReport Diagnose(string input,string output,Func<string> guard,Action<int> progress,CancellationToken cancel)
    {
        Check(guard,cancel);
        using (var stream=new TuningStream(input,MicAnalysis.Rate,typeof(ICapture)))
            return ReadInput(stream,output,guard,progress,cancel);
    }
    static MicReport ReadInput(TuningStream stream,string output,Func<string> guard,Action<int> progress,CancellationToken cancel)
    {
        using (var meter=new AudioCore.PeakMeter())
        {
            var capture=(ICapture)stream.service;
            var analysis=new MicAnalysis();
            var block=new short[MicAnalysis.Block];
            int used=0, blocks=0;
            var watch=Stopwatch.StartNew();
            long nextCheck=500;
            stream.Start();
            while (blocks<MicAnalysis.TotalBlocks) {
                cancel.ThrowIfCancellationRequested();
                if (watch.ElapsedMilliseconds>9000) throw new TimeoutException("输入端口未及时提供完整采样");
                if (watch.ElapsedMilliseconds>=nextCheck) { Check(guard,cancel); nextCheck=watch.ElapsedMilliseconds+500; }
                uint size; capture.GetNextPacketSize(out size);
                if (size==0) { Thread.Sleep(6); continue; }
                IntPtr data; uint frames,flags; ulong position,qpc;
                capture.GetBuffer(out data,out frames,out flags,out position,out qpc);
                if (frames==0) continue;
                try {
                    if ((flags & 1)!=0 && blocks>0) throw new InvalidOperationException("采样中断，请重测");
                    for (int offset=0;offset<(int)frames && blocks<MicAnalysis.TotalBlocks;) {
                        int n=Math.Min(block.Length-used,(int)frames-offset);
                        if ((flags & 2)!=0) Array.Clear(block,used,n);
                        else Marshal.Copy(IntPtr.Add(data,offset*2),block,used,n);
                        offset+=n; used+=n;
                        if (used==block.Length) {
                            float reference=-1;
                            try { if (output!=null) reference=meter.Read(output); } catch (COMException) { }
                            analysis.Add(block,reference); used=0; blocks++;
                            if (blocks%5==0) progress(blocks);
                        }
                    }
                } finally { capture.ReleaseBuffer(frames); }
            }
            Array.Clear(block,0,block.Length);
            return analysis.Finish();
        }
    }
    public static void Audition(string output,short[] sample,Func<string> guard,CancellationToken cancel)
    {
        Check(guard,cancel);
        using (var stream=new TuningStream(output,EqPreset.Rate,typeof(IRender))) {
            var render=(IRender)stream.service;
            uint capacity; stream.client.GetBufferSize(out capacity);
            int offset=0;
            var watch=Stopwatch.StartNew(); long nextCheck=400;
            while (true) {
                cancel.ThrowIfCancellationRequested();
                if (watch.ElapsedMilliseconds>6000) throw new TimeoutException("试听端口响应超时");
                if (watch.ElapsedMilliseconds>=nextCheck) { Check(guard,cancel); nextCheck=watch.ElapsedMilliseconds+400; }
                uint padding; stream.client.GetCurrentPadding(out padding);
                if (offset==sample.Length && padding==0) return;
                int n=Math.Min(sample.Length-offset,(int)(capacity-padding));
                if (n==0) { Thread.Sleep(8); continue; }
                IntPtr data; render.GetBuffer((uint)n,out data);
                try { Marshal.Copy(sample,offset,data,n); } finally { render.ReleaseBuffer((uint)n,0); }
                offset+=n; if (!stream.started) stream.Start();
            }
        }
    }
}
