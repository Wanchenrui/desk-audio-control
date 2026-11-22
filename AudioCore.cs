using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public sealed class AudioItem
{
    public string Id, Name, Bus;
    public int Flow, State, Channels;
    public bool HandsFreeProfile;
}

public static class AudioCore
{
    const uint Active = 1;
    const int NotFound = unchecked((int)0x80070490);
    static readonly Key FriendlyName = new Key("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
    static readonly Key InterfaceName = new Key("026E516E-B814-414B-83CD-856D6FEF4822", 2);
    static readonly Key DeviceFormat = new Key("F19F064D-082C-4E27-BC73-6882A1BB8E4C",0);

    [StructLayout(LayoutKind.Sequential)]
    struct Key
    {
        public Guid Format;
        public uint Property;
        public Key(string format, uint property) { Format = new Guid(format); Property = property; }
    }

    // The PROPVARIANT union occupies two pointer-sized slots on both x86 and x64.
    [StructLayout(LayoutKind.Sequential)]
    struct Variant
    {
        public ushort Vt, Reserved1, Reserved2, Reserved3;
        public IntPtr Value, Extra;
    }
    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref Variant value);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMEnumerator {}
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnum
    {
        void EnumAudioEndpoints(int flow, uint state, out ICollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICollection
    {
        void GetCount(out uint count);
        void Item(uint index, out IDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDevice
    {
        void Activate(ref Guid iid, uint clsctx, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        void OpenPropertyStore(uint access, out IStore store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out uint state);
    }
    [ComImport, Guid("1BE09788-6894-4089-8586-9A2A6C265AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEndpoint { void GetDataFlow(out int flow); }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IStore
    {
        void UnusedGetCount();
        void UnusedGetAt();
        void GetValue(ref Key key, out Variant value);
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyClient {}
    // IPolicyConfig is a Windows implementation interface, not a published SDK contract.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicy
    {
        void UnusedGetMixFormat();
        void UnusedGetDeviceFormat();
        void UnusedResetDeviceFormat();
        void UnusedSetDeviceFormat();
        void UnusedGetProcessingPeriod();
        void UnusedSetProcessingPeriod();
        void UnusedGetShareMode();
        void UnusedSetShareMode();
        void UnusedGetPropertyValue();
        void UnusedSetPropertyValue();
        void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        void SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool visible);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVolume
    {
        void UnusedRegisterControlChangeNotify();
        void UnusedUnregisterControlChangeNotify();
        void GetChannelCount(out uint count);
        void UnusedSetMasterVolumeLevel();
        void SetMasterVolumeLevelScalar(float level, IntPtr context);
        void UnusedGetMasterVolumeLevel();
        void GetMasterVolumeLevelScalar(out float level);
        void UnusedSetChannelVolumeLevel();
        void SetChannelVolumeLevelScalar(uint channel, float level, IntPtr context);
        void UnusedGetChannelVolumeLevel();
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISessionManager
    {
        void UnusedGetAudioSessionControl();
        void UnusedGetSimpleAudioVolume();
        void GetSessionEnumerator(out ISessionEnumerator sessions);
    }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, out ISessionControl control);
    }
    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISessionControl { void GetState(out int state); }
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISessionControl2
    {
        void State(); void DisplayName(); void SetDisplayName(); void Icon(); void SetIcon();
        void Grouping(); void SetGrouping(); void Register(); void Unregister();
        void Identifier(); void InstanceIdentifier(); void GetProcessId(out uint pid);
    }

    static void Release(object obj) { if (obj != null) Marshal.ReleaseComObject(obj); }
    static void CheckFlow(int flow)
    {
        if (flow != 0 && flow != 1) throw new ArgumentOutOfRangeException("flow");
    }
    static string ReadText(IStore store, Key key)
    {
        Variant value;
        store.GetValue(ref key, out value);
        try { return value.Vt == 31 ? Marshal.PtrToStringUni(value.Value) : ""; }
        finally { PropVariantClear(ref value); }
    }
    static AudioItem Describe(IDevice dev, int flow)
    {
        string id;
        uint state;
        dev.GetId(out id);
        dev.GetState(out state);
        IStore store;
        dev.OpenPropertyStore(0, out store);
        try
        {
            string name = ReadText(store, FriendlyName);
            return new AudioItem { Id = id, Name = String.IsNullOrEmpty(name) ? id : name,
                Bus = ReadText(store, InterfaceName), Flow = flow, State = (int)state, Channels = ReadChannels(store) };
        }
        finally { Release(store); }
    }
    static int ReadChannels(IStore store)
    {
        Key key = DeviceFormat;
        Variant value;
        try { store.GetValue(ref key,out value); }
        catch (COMException) { return 0; }
        try
        {
            // VT_BLOB contains a DWORD byte length and an aligned pointer to WAVEFORMATEX.
            return value.Vt == 65 && (uint)value.Value.ToInt64() >= 16 && value.Extra != IntPtr.Zero ?
                (ushort)Marshal.ReadInt16(value.Extra,2) : 0;
        }
        finally { PropVariantClear(ref value); }
    }
    public static List<AudioItem> List(int flow)
    {
        CheckFlow(flow);
        var list = new List<AudioItem>();
        var en = (IEnum)new MMEnumerator();
        ICollection collection = null;
        try
        {
            en.EnumAudioEndpoints(flow, Active, out collection);
            uint count;
            collection.GetCount(out count);
            for (uint n = 0; n < count; n++)
            {
                IDevice dev = null;
                try
                {
                    collection.Item(n, out dev);
                    var item = Describe(dev, flow);
                    if (item.State == Active) list.Add(item);
                }
                catch (COMException ex) { if (ex.ErrorCode != NotFound) throw; }
                finally { Release(dev); }
            }
            return list;
        }
        finally { Release(collection); Release(en); }
    }
    static string ReadDefault(IEnum en, int flow, int role)
    {
        IDevice dev = null;
        try
        {
            int status = en.GetDefaultAudioEndpoint(flow, role, out dev);
            if (status == NotFound) return null;
            Marshal.ThrowExceptionForHR(status);
            string id;
            dev.GetId(out id);
            return id;
        }
        finally { Release(dev); }
    }
    public static string Default(int flow, int role)
    {
        CheckFlow(flow);
        if (role < 0 || role > 2) throw new ArgumentOutOfRangeException("role");
        var en = (IEnum)new MMEnumerator();
        try { return ReadDefault(en, flow, role); }
        finally { Release(en); }
    }
    static bool Same(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

    public static int State(string id)
    {
        if (String.IsNullOrEmpty(id)) throw new ArgumentNullException("id");
        var en = (IEnum)new MMEnumerator();
        IDevice dev = null;
        try
        {
            en.GetDevice(id, out dev);
            uint state;
            dev.GetState(out state);
            return (int)state;
        }
        finally { Release(dev); Release(en); }
    }
    public static void SetVisible(string id, bool visible)
    {
        if (String.IsNullOrEmpty(id)) throw new ArgumentNullException("id");
        var policy = (IPolicy)new PolicyClient();
        try { policy.SetEndpointVisibility(id, visible); }
        finally { Release(policy); }
    }

    public static void Route(string id, int flow)
    {
        if (String.IsNullOrEmpty(id)) throw new ArgumentNullException("id");
        RouteRoles(new[] { id,id,id },flow,null);
    }

    internal static string[] Defaults(int flow)
    {
        return new[] { Default(flow,0), Default(flow,1), Default(flow,2) };
    }

    internal static void RouteRoles(string[] targets, int flow, string[] expected)
    {
        CheckFlow(flow);
        var en = (IEnum)new MMEnumerator();
        IPolicy policy = null;
        try
        {
            foreach (string target in System.Linq.Enumerable.Distinct(targets,StringComparer.OrdinalIgnoreCase))
            {
                IDevice dev = null;
                try
                {
                    en.GetDevice(target,out dev);
                    int direction; uint state;
                    ((IEndpoint)dev).GetDataFlow(out direction);
                    dev.GetState(out state);
                    if (direction != flow || state != Active) throw new InvalidOperationException("端口已断开或方向不匹配。");
                }
                finally { Release(dev); }
            }
            RouteTransaction.Apply(targets, role => ReadDefault(en,flow,role), (role,target) =>
            {
                if (policy == null) policy = (IPolicy)new PolicyClient();
                policy.SetDefaultEndpoint(target,role);
            }, expected);
        }
        finally { Release(policy); Release(en); }
    }

    static T WithService<TService, T>(string id, Func<TService, T> action, bool renderOnly = false)
    {
        if (String.IsNullOrEmpty(id)) throw new ArgumentNullException("id");
        var en = (IEnum)new MMEnumerator();
        IDevice dev = null;
        object service = null;
        try
        {
            en.GetDevice(id, out dev);
            if (renderOnly)
            {
                int flow;
                ((IEndpoint)dev).GetDataFlow(out flow);
                if (flow != 0) throw new NotSupportedException("Stereo balance requires a playback endpoint.");
            }
            Guid iid = typeof(TService).GUID;
            dev.Activate(ref iid, 23, IntPtr.Zero, out service);
            return action((TService)service);
        }
        finally { Release(service); Release(dev); Release(en); }
    }
    public static bool InUse(string id)
    {
        return ActiveSessions(id) != 0;
    }
    public static int ActiveSessions(string id,Action<uint> activeProcess = null)
    {
        return WithService(id, (ISessionManager manager) =>
        {
            ISessionEnumerator sessions;
            manager.GetSessionEnumerator(out sessions);
            try
            {
                int count;
                sessions.GetCount(out count);
                int active = 0;
                for (int i = 0; i < count; i++)
                {
                    ISessionControl control;
                    sessions.GetSession(i, out control);
                    try
                    {
                        int state;
                        control.GetState(out state);
                        if (state == 1)
                        {
                            active++;
                            if (activeProcess != null)
                            {
                                uint pid;
                                ((ISessionControl2)control).GetProcessId(out pid);
                                activeProcess(pid);
                            }
                        }
                    }
                    finally { Release(control); }
                }
                return active;
            }
            finally { Release(sessions); }
        });
    }
    // IAudioMeterInformation: instantaneous peak of the endpoint signal, 0..1.
    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMeter { void GetPeakValue(out float peak); }
    public static float Peak(string id)
    {
        return WithService(id, (IMeter m) => { float peak; m.GetPeakValue(out peak); return peak; });
    }
    static object ActivateOn(string id, Guid iid)
    {
        if (String.IsNullOrEmpty(id)) throw new ArgumentNullException("id");
        var en = (IEnum)new MMEnumerator();
        IDevice dev = null;
        try
        {
            en.GetDevice(id, out dev);
            object service;
            dev.Activate(ref iid, 23, IntPtr.Zero, out service);
            return service;
        }
        finally { Release(dev); Release(en); }
    }
    // Polls one endpoint's peak through a meter that stays activated, instead of rebuilding the COM chain on every read.
    // Use one instance per polling thread.
    public sealed class PeakMeter : IDisposable
    {
        readonly EndpointCache<IMeter> cache = new EndpointCache<IMeter>(
            endpoint => (IMeter)ActivateOn(endpoint, typeof(IMeter).GUID), meter => Release(meter));
        public float Read(string id)
        {
            return cache.Use(id, meter => { float peak; meter.GetPeakValue(out peak); return peak; });
        }
        public void Reset() { cache.Reset(); }
        public void Dispose() { cache.Dispose(); }
    }
    public static int Volume(string id)
    {
        return WithService(id, (IVolume v) => { float level; v.GetMasterVolumeLevelScalar(out level); return (int)Math.Round(level * 100); });
    }
    public static void SetVolume(string id, int level)
    {
        if (level < 0 || level > 100) throw new ArgumentOutOfRangeException("level");
        WithService(id, (IVolume v) => { v.SetMasterVolumeLevelScalar(level / 100.0f, IntPtr.Zero); return 0; });
    }
    public static bool Muted(string id)
    {
        return WithService(id, (IVolume v) => { bool muted; v.GetMute(out muted); return muted; });
    }
    public static void SetMute(string id, bool muted)
    {
        WithService(id, (IVolume v) => { v.SetMute(muted, IntPtr.Zero); return 0; });
    }

    struct Stereo
    {
        public float Left, Right, Master;
        public bool Muted;

        public int Balance
        {
            get
            {
                float peak = Math.Max(Left, Right);
                return peak <= 0 ? 0 : (int)Math.Round(100 * (Right - Left) / peak);
            }
        }
    }
    static Stereo ReadStereo(IVolume volume)
    {
        uint count;
        volume.GetChannelCount(out count);
        if (count != 2) throw new NotSupportedException("This endpoint does not expose a stereo pair.");
        var levels = new Stereo();
        volume.GetChannelVolumeLevelScalar(0, out levels.Left);
        volume.GetChannelVolumeLevelScalar(1, out levels.Right);
        volume.GetMasterVolumeLevelScalar(out levels.Master);
        volume.GetMute(out levels.Muted);
        return levels;
    }
    public static int Balance(string id)
    {
        return WithService(id, (IVolume v) => ReadStereo(v).Balance, true);
    }
    static void WriteStereo(IVolume volume, Stereo current, float left, float right)
    {
        // Restore the louder side to its existing ceiling before attenuating the other side.
        uint first = left >= right ? 0U : 1U;
        if (first == 0 && current.Left != left) volume.SetChannelVolumeLevelScalar(0, left, IntPtr.Zero);
        if (current.Right != right) volume.SetChannelVolumeLevelScalar(1, right, IntPtr.Zero);
        if (first == 1 && current.Left != left) volume.SetChannelVolumeLevelScalar(0, left, IntPtr.Zero);
    }
    public static void SetBalance(string id, int balance)
    {
        if (balance < -100 || balance > 100) throw new ArgumentOutOfRangeException("balance");
        WithService(id, (IVolume v) =>
        {
            var old = ReadStereo(v);
            if (old.Balance == balance) return 0;
            float peak = Math.Max(old.Left, old.Right);
            if (peak <= 0 || old.Master <= 0) throw new InvalidOperationException("Balance cannot be changed at zero volume.");
            // Windows normally reports the louder channel as the endpoint master.
            // Limit writes to devices with this observable relation.
            if (Math.Abs(peak - old.Master) > 0.0001f)
                throw new NotSupportedException("This driver exposes a different master and channel volume scale.");
            peak = Math.Min(peak, old.Master);
            float left = peak * (1f - Math.Max(0, balance) / 100f);
            float right = peak * (1f + Math.Min(0, balance) / 100f);
            try
            {
                WriteStereo(v, old, left, right);
                var result = ReadStereo(v);
                if (Math.Abs(result.Master - old.Master) > 0.001f || Math.Abs(result.Balance - balance) > 3 || result.Muted != old.Muted)
                    throw new NotSupportedException("The audio driver could not preserve independent channel levels.");
                return 0;
            }
            catch (Exception cause)
            {
                try
                {
                    var current = ReadStereo(v);
                    if (old.Muted && !current.Muted) v.SetMute(true, IntPtr.Zero);
                    WriteStereo(v, current, old.Left, old.Right);
                    var restored = ReadStereo(v);
                    if (restored.Master > old.Master + 0.0001f) v.SetMasterVolumeLevelScalar(old.Master, IntPtr.Zero);
                    restored = ReadStereo(v);
                    if (Math.Abs(restored.Master - old.Master) > 0.001f || restored.Balance != old.Balance || restored.Muted != old.Muted)
                        throw new InvalidOperationException("Audio driver did not restore the previous levels.");
                }
                catch (Exception rollback)
                {
                    throw new InvalidOperationException("Could not fully restore previous channel levels.", new AggregateException(cause, rollback));
                }
                throw;
            }
        }, true);
    }
}
