using System;
using System.Globalization;
using System.Runtime.InteropServices;

internal static class BluetoothLink
{
    // Fixed tail consists of two SYSTEMTIMEs and WCHAR szName[248].
    [StructLayout(LayoutKind.Sequential, Size=560)]
    struct DeviceInfo
    {
        public uint Size;
        public ulong Address;
        public uint Class, Connected, Remembered, Authenticated;
    }
    [DllImport("bthprops.cpl")]
    static extern uint BluetoothGetDeviceInfo(IntPtr radio,ref DeviceInfo info);
    internal static bool? Read(string address)
    {
        ulong value;
        if (!UInt64.TryParse(address,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out value)) return null;
        var info=new DeviceInfo { Size=(uint)Marshal.SizeOf(typeof(DeviceInfo)), Address=value };
        try { return BluetoothGetDeviceInfo(IntPtr.Zero,ref info)==0 ? (bool?)(info.Connected!=0) : null; }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }
}
