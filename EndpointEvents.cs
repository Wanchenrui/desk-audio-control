using System;
using System.Runtime.InteropServices;
using System.Threading;

public sealed class EndpointEvents : IDisposable
{
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEventsSource
    {
        void SkipEnum(); void SkipDefault(); void SkipGet();
        void RegisterEndpointNotificationCallback(ICallback callback);
        void UnregisterEndpointNotificationCallback(ICallback callback);
    }
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown),ComVisible(true)]
    public interface ICallback
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id,uint state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow,int role,[MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id,PropertyKey key);
    }
    [StructLayout(LayoutKind.Sequential)] public struct PropertyKey { public Guid category; public uint id; }
    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
    public sealed class Callback : ICallback
    {
        public int Pending;
        int Mark() { Interlocked.Exchange(ref Pending,1); return 0; }
        public int OnDeviceStateChanged(string id,uint state) { return Mark(); }
        public int OnDeviceAdded(string id) { return Mark(); }
        public int OnDeviceRemoved(string id) { return Mark(); }
        public int OnDefaultDeviceChanged(int flow,int role,string id) { return Mark(); }
        public int OnPropertyValueChanged(string id,PropertyKey key) { return Mark(); }
    }
    IEventsSource source;
    readonly Callback sink = new Callback();
    public bool Registered { get; private set; }
    public bool Changed { get { return Interlocked.Exchange(ref sink.Pending,0)!=0; } }
    public EndpointEvents()
    {
        try { source=(IEventsSource)new AudioCore.MMEnumerator(); source.RegisterEndpointNotificationCallback(sink); Registered=true; }
        catch(COMException) { }
    }
    public void Dispose()
    {
        if(source!=null) {
            try { if(Registered) source.UnregisterEndpointNotificationCallback(sink); } catch(COMException) {}
            Marshal.ReleaseComObject(source); source=null;
        }
    }
}
