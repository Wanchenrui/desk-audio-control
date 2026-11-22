using System;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

// Optional Windows transport check. No physical microphone is started; the render stream is
// digital silence and the capture path reads the loopback pin of an idle non-default S/PDIF.
internal sealed partial class TuningStream
{
    internal static void CheckWindowsBridge()
    {
        var render=AudioCore.List(0).FirstOrDefault(p=>AudioDiscovery.FormFactor(p)==8 &&
            !AudioCore.Defaults(0).Contains(p.Id) && !AudioCore.InUse(p.Id));
        if (render==null) throw new InvalidOperationException("No idle non-default S/PDIF endpoint for the silence fixture.");
        var input=AudioCore.List(1).FirstOrDefault(p=>!AudioCore.InUse(p.Id));
        if (input==null) throw new InvalidOperationException("No idle capture endpoint for format negotiation.");
        using (new TuningStream(input.Id,MicAnalysis.Rate,typeof(ICapture))) { }
        using (var self=Process.GetCurrentProcess())
        using (var stop=new CancellationTokenSource())
        using (var stream=new TuningStream(render.Id,MicAnalysis.Rate,typeof(ICapture),0x20000)) {
            Func<string> guard=delegate {
                bool busy=AudioCore.Defaults(0).Contains(render.Id);
                AudioCore.ActiveSessions(render.Id,pid=>{ if (pid!=self.Id) busy=true; });
                return busy ? "Fixture output is now in use." : null;
            };
            int blocks=0;
            var playing=Task.Run(()=>Audition(render.Id,new short[EqPreset.Rate*11/2],guard,stop.Token));
            try {
                var report=ReadInput(stream,render.Id,guard,n=>blocks=n,stop.Token);
                if (!report.Silent || blocks!=100) throw new Exception("Expected five seconds of digital silence.");
                Console.WriteLine("Native WASAPI input negotiation, explicit render and 5 s loopback capture passed (100 blocks, silent).");
            } finally {
                stop.Cancel();
                try { playing.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            }
        }
    }
}
