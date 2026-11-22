using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

public sealed class SmartGroup
{
    public string Key, Name, Kind, Details;
    public bool Virtual, Infrastructure;
    public readonly Dictionary<string,int> Forms = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
    public List<AudioItem> Outputs = new List<AudioItem>();
    public List<AudioItem> Inputs = new List<AudioItem>();
    public List<string> SensedOutputs = new List<string>();
    public List<string> SensedInputs = new List<string>();
}

public static class AudioDiscovery
{
    internal sealed class Evidence
    {
        public AudioItem Item;
        public string Parent, Container, UsbDevice, Serial, Adapter, BluetoothAddress;
        public bool? Sensed;
        public int Form;
    }

    private static readonly StringComparer Compare = StringComparer.OrdinalIgnoreCase;
    private static readonly Regex UsbModel = new Regex(@"^USB\\(VID_[0-9A-F]{4}&PID_[0-9A-F]{4})", RegexOptions.IgnoreCase);
    private static readonly Regex BtAddress = new Regex(@"(?:\\DEV_|&0&)([0-9A-F]{12})(?:\\|_|$)", RegexOptions.IgnoreCase);
    private static readonly HashSet<string> SyntheticCollisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LiveCollisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static bool historyLoaded;
    private const string Props = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\";
    private const string EnumPath = @"SYSTEM\CurrentControlSet\Enum\";

    [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint node, string instance, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Device_ID_Size(out uint length, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint node, StringBuilder buffer, uint length, uint flags);

    private static string ParentId(string instance)
    {
        uint child, parent, size;
        if (String.IsNullOrEmpty(instance) || CM_Locate_DevNodeW(out child, instance, 0) != 0 ||
            CM_Get_Parent(out parent, child, 0) != 0 || CM_Get_Device_ID_Size(out size, parent, 0) != 0)
            return null;
        var buffer = new StringBuilder(checked((int)size + 1));
        return CM_Get_Device_IDW(parent, buffer, size + 1, 0) == 0 ? buffer.ToString() : null;
    }

    private static string Value(RegistryKey key, string name)
    { return key == null ? null : key.GetValue(name) as string; }

    private static string PropPath(AudioItem item)
    {
        if (item == null || String.IsNullOrEmpty(item.Id)) return null;
        int start = item.Id.LastIndexOf('{');
        Guid id;
        if (start < 0 || !Guid.TryParse(item.Id.Substring(start), out id)) return null;
        return Props + (item.Flow == 0 ? @"Render\" : @"Capture\") + id.ToString("B") + @"\Properties";
    }

    private static int ReadForm(RegistryKey props)
    {
        object value = props == null ? null : props.GetValue("{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},0");
        return value is int ? (int)value : -1;
    }

    public static int FormFactor(AudioItem item)
    {
        string path = PropPath(item);
        if (path == null) return -1;
        using (var props = Registry.LocalMachine.OpenSubKey(path)) return ReadForm(props);
    }

    private static Evidence Describe(AudioItem item)
    {
        var row = new Evidence { Item = item, Adapter = item.Bus, Form = -1 };
        string path = PropPath(item);
        string fallback = null;
        if (path != null)
        {
            using (var props = Registry.LocalMachine.OpenSubKey(path))
            {
                row.Form = ReadForm(props);
                fallback = Value(props, "{b3f8fa53-0004-438e-9003-51a46e139bfc},2");
                if (String.IsNullOrWhiteSpace(row.Adapter))
                    row.Adapter = Value(props, "{b3f8fa53-0004-438e-9003-51a46e139bfc},6");
            }
        }
        row.Parent = ParentId(@"SWD\MMDEVAPI\" + item.Id);
        if (String.IsNullOrEmpty(row.Parent) && fallback != null && fallback.StartsWith("{1}.", StringComparison.Ordinal))
            row.Parent = fallback.Substring(4);
        if (String.IsNullOrEmpty(row.Parent)) return row;
        using (var controller = Registry.LocalMachine.OpenSubKey(EnumPath + row.Parent))
            row.Container = ValidContainer(Value(controller, "ContainerID"));
        if (Bus(row).StartsWith("BTH", StringComparison.Ordinal) && row.Container == null)
        {
            string current = row.Parent;
            for (int depth = 0; depth < 4 && current != null && current.StartsWith("BTH", StringComparison.OrdinalIgnoreCase); depth++)
            {
                Match address = BtAddress.Match(current);
                if (address.Success) { row.BluetoothAddress = address.Groups[1].Value.ToUpperInvariant(); break; }
                current = ParentId(current);
            }
        }
        if (Family(row) == "板载音频" && row.Form != 7 && row.Form != 8)
            row.Sensed = JackSense.Read(item.Id);
        if (UsbModel.IsMatch(row.Parent))
        {
            // Find the whole USB device above a composite audio function (MI_XX).
            string current = row.Parent;
            for (int depth = 0; depth < 5 && current != null && UsbModel.IsMatch(current); depth++)
            {
                string[] parts = current.Split('\\');
                if (parts.Length == 3 && parts[1].IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    row.UsbDevice = current;
                    using (var usb = Registry.LocalMachine.OpenSubKey(EnumPath + current))
                    {
                        int capabilities = usb == null ? 0 : Convert.ToInt32(usb.GetValue("Capabilities", 0), CultureInfo.InvariantCulture);
                        // CM_DEVCAP_UNIQUEID distinguishes a bus-supplied serial from a generated port path.
                        if ((capabilities & 0x10) != 0) row.Serial = parts[2];
                        if (row.Container == null) row.Container = ValidContainer(Value(usb,"ContainerID"));
                    }
                    break;
                }
                current = ParentId(current);
            }
        }
        return row;
    }

    private static string ValidContainer(string value)
    {
        Guid id;
        if (!Guid.TryParse(value, out id) || id == Guid.Empty ||
            id == new Guid("00000000-0000-0000-ffff-ffffffffffff")) return null;
        return id.ToString("D").ToUpperInvariant();
    }

    private static string Token(string value)
    {
        using (var digest = SHA256.Create())
        {
            byte[] hash = digest.ComputeHash(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
            return BitConverter.ToString(hash, 0, 12).Replace("-", "");
        }
    }

    private static string Bus(Evidence row)
    {
        string parent = row.Parent ?? "";
        int split = parent.IndexOf('\\');
        return split < 0 ? "" : parent.Substring(0, split).ToUpperInvariant();
    }

    private static string Family(Evidence row)
    {
        string bus = Bus(row);
        if (bus == "ROOT" || bus == "SWD" || bus == "SW" || bus == "RDPBUS") return "虚拟音频";
        if (row.Form == 9) return "显示器音频";
        if (bus == "USB") return "USB";
        if (bus.StartsWith("BTH",StringComparison.Ordinal)) return "Bluetooth";
        if (bus == "HDAUDIO" || bus == "INTELAUDIO" || bus == "ACPI" || bus == "PCI") return "板载音频";
        return "其他音频";
    }

    private static string Physical(Evidence row)
    {
        string family = Family(row);
        if (family == "显示器音频") return "DISPLAY:" + row.Item.Id;
        // An analog codec and its S/PDIF transmitter have different user-facing uses.
        if (family == "板载音频" && (row.Form == 7 || row.Form == 8)) return "DIGITAL:" + row.Item.Id;
        if (family == "USB") return "USB:" + (row.UsbDevice ?? row.Container ?? row.Parent);
        if (family == "Bluetooth" && row.Container != null) return "BT:" + row.Container;
        if (family == "Bluetooth" && row.BluetoothAddress != null) return "BTADDR:" + row.BluetoothAddress;
        return family + ":" + (row.Parent ?? row.Item.Id);
    }

    private static string DisplayName(List<Evidence> rows, string family)
    {
        var first = rows[0];
        if (family == "显示器音频" || (family == "板载音频" && (first.Form == 7 || first.Form == 8)))
            return first.Item.Name;
        string label = rows.Select(r => r.Adapter).FirstOrDefault(n => !String.IsNullOrWhiteSpace(n)) ?? first.Item.Name;
        if (family == "Bluetooth")
            label = Regex.Replace(label, @"\s+(Stereo|Hands[- ]Free(?: AG Audio)?)$", "", RegexOptions.IgnoreCase);
        return label;
    }

    internal static List<SmartGroup> GroupRows(IEnumerable<Evidence> rows)
    {
        return GroupRowsCore(rows, SyntheticCollisions);
    }

    private static List<SmartGroup> GroupRowsCore(IEnumerable<Evidence> rows, HashSet<string> collisions)
    {
        List<List<Evidence>> physical = rows.GroupBy(Physical, Compare).Select(g => g.ToList()).ToList();
        var counts = new Dictionary<string, int>(Compare);
        foreach (var device in physical)
        {
            Match m = UsbModel.Match(device[0].Parent ?? "");
            if (!m.Success || Family(device[0]) != "USB") continue;
            string key = m.Groups[1].Value.ToUpperInvariant();
            int count; counts.TryGetValue(key, out count); counts[key] = count + 1;
            if (!String.IsNullOrEmpty(device[0].Serial))
            {
                string serialKey = key + ":S:" + Token(device[0].Serial);
                counts.TryGetValue(serialKey, out count); counts[serialKey] = count + 1;
            }
        }
        var groups = new List<SmartGroup>();
        lock (collisions)
        {
            foreach (var entry in counts) if (entry.Value > 1) collisions.Add(entry.Key);
            foreach (var device in physical)
            {
                var head = device[0]; string family = Family(head);
                string key = Physical(head);
                Match m = UsbModel.Match(head.Parent ?? "");
                if (m.Success && family == "USB")
                {
                    string model = m.Groups[1].Value.ToUpperInvariant();
                    // Serial-equipped devices survive a port move. Serial-less devices may
                    // share a model preference only while no simultaneous twin is known.
                    string serialKey = String.IsNullOrEmpty(head.Serial) ? null : model + ":S:" + Token(head.Serial);
                    if (serialKey != null && !collisions.Contains(serialKey)) key = "USB:" + serialKey;
                    else if (!collisions.Contains(model)) key = "USB:" + model;
                    else key = "USB:" + model + ":I:" + Token(head.Container ?? head.UsbDevice ?? head.Parent);
                }
                else key = family + ":" + Token(key);
                var group = new SmartGroup {
                    Key = key, Name = DisplayName(device, family), Kind = family,
                    Virtual = family == "虚拟音频", Infrastructure = family == "虚拟音频" || family == "板载音频" || family == "显示器音频"
                };
                foreach (var row in device) group.Forms[row.Item.Id] = row.Form;
                foreach (var item in device.Select(r => r.Item).OrderBy(i => i.Id, Compare))
                    (item.Flow == 0 ? group.Outputs : group.Inputs).Add(item);
                foreach (var sensed in device.Where(r => r.Sensed == true))
                    (sensed.Item.Flow == 0 ? group.SensedOutputs : group.SensedInputs).Add(sensed.Item.Id);
                group.Details = String.Format("{0} · {1} 路播放 / {2} 路录音", family, group.Outputs.Count, group.Inputs.Count);
                if (group.SensedOutputs.Count + group.SensedInputs.Count > 0) group.Details += " · 驱动检测到插头";
                groups.Add(group);
            }
        }
        foreach (var names in groups.GroupBy(g => g.Name, Compare).Where(g => g.Count() > 1))
            foreach (var group in names) group.Name += " · " + Token(group.Key).Substring(0,6);
        return groups.OrderBy(g => g.Infrastructure).ThenBy(g => g.Name, Compare).ThenBy(g=>g.Key,Compare).ToList();
    }

    public static List<SmartGroup> Scan()
    {
        var devices = AudioCore.List(0);
        devices.AddRange(AudioCore.List(1));
        var evidence = devices.Select(Describe).ToList();
        lock (LiveCollisions)
        {
            if (!historyLoaded)
            {
                WithHistory(delegate {
                    LiveCollisions.UnionWith(AppConfig.Store.Keys("AmbiguousUsbIdentities/"));
                });
                historyLoaded = true;
            }
            var known = new HashSet<string>(LiveCollisions, Compare);
            var groups = GroupRowsCore(evidence, LiveCollisions);
            if (known.Count != LiveCollisions.Count)
                WithHistory(delegate {
                    foreach (string key in LiveCollisions.Except(known, Compare)) AppConfig.Store.Put("AmbiguousUsbIdentities/" + key,"1");
                });
            return groups;
        }
    }

    private static void WithHistory(Action access)
    {
        try { access(); }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
        catch (System.IO.IOException) { }
    }
}

// The adapter-side topology connector exposes the same indices to both descriptions.
// IsConnected alone is not sufficient: Windows also reports true for unsensed jacks.
internal static class JackSense
{
    [ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface Enumerator { void List(); void Default(); void GetDevice([MarshalAs(UnmanagedType.LPWStr)]string id,out Device device); }
    [ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface Device { void Activate(ref Guid iid,uint context,IntPtr args,[MarshalAs(UnmanagedType.IUnknown)]out object instance); }
    [ComImport,Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface Topology { void GetCount(out uint count); void GetConnector(uint index,out Connector c); }
    [ComImport,Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface Connector { void Type(); void Flow(); void ConnectTo(); void Disconnect(); void IsConnected(); void GetConnectedTo(out Connector adapter); }
    [ComImport,Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface Part {
        void Name();void LocalId();void GlobalId();void Type();void Subtype();void Count();void At();void Incoming();void Outgoing();void Topology();
        void Activate(uint context,ref Guid iid,[MarshalAs(UnmanagedType.IUnknown)]out object instance);
    }
    [StructLayout(LayoutKind.Sequential)]
    struct Jack { public uint Channels,Color,Connection,GeoLocation,GenLocation,Port,Connected; }
    [StructLayout(LayoutKind.Sequential)]
    struct Jack2 { public uint State,Capabilities; }
    [ComImport,Guid("4509F757-2D46-4637-8E62-CE7DB944F57B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface Description { void Count(out uint count);void Item(uint index,out Jack jack); }
    [ComImport,Guid("478F3A9B-E0C9-4827-9228-6F5505FFE76A"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface Description2 { void Count(out uint count);void Item(uint index,out Jack2 jack); }

    private static void Release(object value) { if (value != null) Marshal.ReleaseComObject(value); }
    private static bool? Check(Connector connector)
    {
        Connector adapter=null; object d1=null,d2=null;
        try
        {
            connector.GetConnectedTo(out adapter); var part=(Part)adapter;
            Guid id1=typeof(Description).GUID,id2=typeof(Description2).GUID;
            part.Activate(23,ref id1,out d1); part.Activate(23,ref id2,out d2);
            var one=(Description)d1; var two=(Description2)d2;
            uint n,m; one.Count(out n); two.Count(out m); bool? sensed=null;
            for(uint i=0;i<Math.Min(n,m);i++)
            {
                Jack jack; Jack2 capabilities; one.Item(i,out jack); two.Item(i,out capabilities);
                if ((capabilities.Capabilities & 1)==0 || jack.Port != 0) continue;
                if (jack.Connection != 1 && jack.Connection != 2 && jack.Connection != 4 && jack.Connection != 7 &&
                    jack.Connection != 8 && jack.Connection != 9 && jack.Connection != 11) continue;
                if (jack.Connected != 0) return true;
                sensed=false;
            }
            return sensed;
        }
        catch(COMException){return null;}
        catch(InvalidCastException){return null;}
        finally { Release(d2);Release(d1);Release(adapter); }
    }
    internal static bool? Read(string endpointId)
    {
        Enumerator en=null; Device dev=null;object raw=null;
        try
        {
            en=(Enumerator)new AudioCore.MMEnumerator();en.GetDevice(endpointId,out dev);
            Guid id=typeof(Topology).GUID;dev.Activate(ref id,23,IntPtr.Zero,out raw);
            var topology=(Topology)raw;uint count;topology.GetCount(out count);bool? sensed=null;
            for(uint i=0;i<count;i++)
            {
                Connector c=null;
                try {
                    topology.GetConnector(i,out c); bool? state=Check(c);
                    if(state==true)return true;
                    if(state.HasValue)sensed=false;
                }
                finally {Release(c);}
            }
            return sensed;
        }
        catch(COMException){return null;}
        catch(InvalidCastException){return null;}
        finally {Release(raw);Release(dev);Release(en);}
    }
}
