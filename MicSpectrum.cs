using System;

// Hann-windowed 50 ms frames. Keep 24 logarithmic band energies and a zero-crossing
// rate; the FFT work arrays are cleared after each frame.
internal sealed class MicSpectrum
{
    const int Bands=24;
    readonly int N;
    readonly double[] real,imag,window;
    readonly int[] band;
    internal readonly double[] Centers=new double[Bands];
    internal MicSpectrum(int rate=MicAnalysis.Rate)
    {
        window=new double[rate/20];
        int size=1;
        while (size<window.Length) size*=2;
        N=size; real=new double[N]; imag=new double[N]; band=new int[N/2+1];
        double ratio=Math.Log(rate/2.0/30);
        for (int k=0;k<Bands;k++) Centers[k]=30*Math.Exp((k+.5)*ratio/Bands);
        for (int k=1;k<band.Length;k++) band[k]=Math.Min(Bands-1,Math.Max(0,(int)(Bands*Math.Log(k*rate/(N*30.0))/ratio)));
        for (int i=0;i<window.Length;i++) window[i]=.5-.5*Math.Cos(2*Math.PI*i/(window.Length-1));
    }
    internal double[] Energy(short[] samples)
    {
        var energy=new double[Bands];
        for (int i=0,j=0;i<N;i++) {
            real[j]=i<samples.Length ? samples[i]*window[i]/32768.0 : 0;
            int bit=N>>1;
            while (bit!=0 && (j&bit)!=0) { j^=bit; bit>>=1; }
            j^=bit;
        }
        for (int width=2;width<=N;width*=2) {
            double angle=-2*Math.PI/width, cr=Math.Cos(angle), ci=Math.Sin(angle);
            for (int start=0;start<N;start+=width) {
                double wr=1, wi=0;
                for (int i=start;i<start+width/2;i++) {
                    int j=i+width/2;
                    double tr=wr*real[j]-wi*imag[j], ti=wr*imag[j]+wi*real[j];
                    real[j]=real[i]-tr; imag[j]=imag[i]-ti;
                    real[i]+=tr; imag[i]+=ti;
                    double next=wr*cr-wi*ci; wi=wr*ci+wi*cr; wr=next;
                }
            }
        }
        for (int k=1;k<band.Length;k++) energy[band[k]]+=real[k]*real[k]+imag[k]*imag[k];
        Array.Clear(real,0,N); Array.Clear(imag,0,N);
        return energy;
    }
}
