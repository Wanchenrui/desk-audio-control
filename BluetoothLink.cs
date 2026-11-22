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
    [DllImport("bthprops.cpl",SetLastError=true)]
    static extern IntPtr BluetoothFindFirstRadio(ref uint size,out IntPtr radio);
    [DllImport("bthprops.cpl",SetLastError=true)]
    static extern bool BluetoothFindNextRadio(IntPtr search,out IntPtr radio);
    [DllImport("bthprops.cpl")] static extern bool BluetoothFindRadioClose(IntPtr search);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    static bool? ReadRadios(ulong address)
    {
        uint size=4; IntPtr radio;
        IntPtr search=BluetoothFindFirstRadio(ref size,out radio);
        if (search==IntPtr.Zero) return null;
        bool known=false;
        try {
            do {
                try {
                    var info=new DeviceInfo { Size=(uint)Marshal.SizeOf(typeof(DeviceInfo)), Address=address };
                    if (BluetoothGetDeviceInfo(radio,ref info)==0) {
                        if (info.Connected!=0) return true;
                        known=true;
                    }
                } finally { CloseHandle(radio); }
            } while (BluetoothFindNextRadio(search,out radio));
            // Capture the failed enumeration call before disposing the search handle.
            int enumerationError=Marshal.GetLastWin32Error();
            return known && enumerationError==259 ? (bool?)false : null;
        } finally { BluetoothFindRadioClose(search); }
    }
    internal static bool? Read(string address)
    {
        ulong value;
        if (!UInt64.TryParse(address,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out value)) return null;
        try { return ReadRadios(value); }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }
}
