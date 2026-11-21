using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Accessibility;
using Microsoft.Win32;

public static class AudioTuning
{
    // The native effects page writes settings only when the user applies them.
    public static void Open(string endpointId, string bus)
    {
        Open(endpointId, bus, Rectangle.Empty);
    }

    public static void Open(string endpointId, string bus, Rectangle targetArea)
    {
        Guid id;
        int pos = endpointId == null ? -1 : endpointId.LastIndexOf('{');
        if (pos < 0 || !Guid.TryParse(endpointId.Substring(pos), out id))
            throw new ArgumentException("无法识别所选播放设备。");
        string keyName = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\"
                         + id.ToString("B") + @"\Properties";
        string adapter, endpointLabel;
        using (var key = Registry.LocalMachine.OpenSubKey(keyName))
        {
            if (key == null) throw new InvalidOperationException("找不到该播放设备的驱动信息。");
            adapter = key.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string;
            endpointLabel = key.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string;
        }
        if (String.IsNullOrWhiteSpace(adapter)) adapter = bus;
        if (String.IsNullOrWhiteSpace(adapter)) throw new InvalidOperationException("找不到该播放设备的驱动信息。");

        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "rundll32.exe"),
                                         "shell32.dll,Control_RunDLL mmsys.cpl,,0");
        start.UseShellExecute = false;
        using (var process = Process.Start(start))
        {
            if (process == null) throw new InvalidOperationException("无法打开 Windows 声音面板。");
            IntPtr main = WaitForWindow(process.Id, IntPtr.Zero);
            if (main == IntPtr.Zero) throw new InvalidOperationException("请关闭已打开的 Windows 声音窗口后重试。");
            CenterWindow(main, targetArea);
            IntPtr pane = FindWindowEx(main, IntPtr.Zero, "#32770", null);
            IntPtr list = GetDlgItem(pane, 1000);
            if (list == IntPtr.Zero) throw new InvalidOperationException("无法读取播放设备列表。");
            SelectEndpoint(list, adapter, bus, endpointLabel);
            IntPtr properties = GetDlgItem(pane, 1003);
            if (properties == IntPtr.Zero || !IsWindowEnabled(properties))
                throw new InvalidOperationException("Windows 未启用该设备的属性页。");
            PostMessage(properties, 0x00F5, IntPtr.Zero, IntPtr.Zero); // BM_CLICK
            IntPtr sheet = WaitForWindow(process.Id, main);
            if (sheet == IntPtr.Zero) throw new InvalidOperationException("无法定位播放设备的属性页。");
            CenterWindow(sheet, targetArea);
            if (!SelectEnhancements(GetDlgItem(sheet, 12320)))
            {
                var caption = new StringBuilder(250);
                GetWindowText(sheet, caption, caption.Capacity);
                SetWindowText(sheet, caption.ToString() + " · 此驱动未提供声音增强页");
            }
            int pid = process.Id;
            ThreadPool.QueueUserWorkItem(delegate
            {
                while (IsOwnedBy(sheet, pid)) Thread.Sleep(350);
                if (IsOwnedBy(main, pid) && IsWindowEnabled(main))
                    PostMessage(main, 0x10, IntPtr.Zero, IntPtr.Zero);
            });
        }
    }

    static bool AdapterMatches(string description, string name)
    {
        return !String.IsNullOrWhiteSpace(name) &&
            (description.Equals(name, StringComparison.OrdinalIgnoreCase) ||
             description.StartsWith(name + ",", StringComparison.OrdinalIgnoreCase) ||
             description.StartsWith(name + "，", StringComparison.OrdinalIgnoreCase));
    }

    static void SelectEndpoint(IntPtr list, string adapter, string liveAdapter, string endpointLabel)
    {
        var acc = Accessible(list);
        try
        {
            int adapterCount = 0, labelCount = 0, adapterIndex = 0, labelIndex = 0;
            for (int i = 1; i <= acc.accChildCount; i++)
            {
                string description;
                try { description = acc.get_accDescription(i); }
                catch (ArgumentException) { continue; } // The list can expose a trailing accessible scrollbar.
                catch (COMException) { continue; }
                if (description == null) continue;
                if (!AdapterMatches(description, adapter) && !AdapterMatches(description, liveAdapter)) continue;
                adapterCount++;
                adapterIndex = i;
                if (!String.IsNullOrEmpty(endpointLabel) &&
                    String.Equals(acc.get_accName(i), endpointLabel, StringComparison.OrdinalIgnoreCase))
                {
                    labelCount++;
                    labelIndex = i;
                }
            }
            if (adapterCount == 0) throw new InvalidOperationException("未在 Windows 声音面板中找到该设备。");
            if (adapterCount > 1 && labelCount != 1)
                throw new InvalidOperationException("多个播放端口使用相同名称，请在已打开的声音面板中选择。");
            int selected = labelCount == 1 ? labelIndex : adapterIndex;
            acc.accSelect(3, selected); // SELFLAG_TAKEFOCUS | SELFLAG_TAKESELECTION
        }
        finally { Marshal.ReleaseComObject(acc); }
    }

    static bool SelectEnhancements(IntPtr tabs)
    {
        var acc = Accessible(tabs);
        try
        {
            for (int i = 1; i <= acc.accChildCount; i++)
            {
                string name = acc.get_accName(i);
                if (name == "增强" || name == "增強" || String.Equals(name, "Enhancements", StringComparison.OrdinalIgnoreCase))
                {
                    acc.accDoDefaultAction(i);
                    return true;
                }
            }
        }
        finally { Marshal.ReleaseComObject(acc); }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowBounds { public int Left, Top, Right, Bottom; }

    static void CenterWindow(IntPtr hwnd, Rectangle area)
    {
        WindowBounds bounds;
        if (area.Width <= 0 || area.Height <= 0 || !GetWindowRect(hwnd, out bounds)) return;
        int x = area.Left + Math.Max(0, (area.Width - (bounds.Right - bounds.Left)) / 2);
        int y = area.Top + Math.Max(0, (area.Height - (bounds.Bottom - bounds.Top)) / 2);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0015); // NOSIZE | NOZORDER | NOACTIVATE
    }

    static IAccessible Accessible(IntPtr hwnd)
    {
        Guid iid = typeof(IAccessible).GUID;
        IAccessible result;
        int hr = AccessibleObjectFromWindow(hwnd, unchecked((uint)-4), ref iid, out result);
        Marshal.ThrowExceptionForHR(hr);
        if (result == null) throw new InvalidOperationException("Windows 声音面板暂时无法访问。");
        return result;
    }

    static bool IsOwnedBy(IntPtr hwnd, int pid)
    {
        uint owner;
        return GetWindowThreadProcessId(hwnd, out owner) != 0 && owner == (uint)pid;
    }

    static IntPtr WaitForWindow(int pid, IntPtr parent)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            IntPtr result = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
            {
                if (!IsOwnedBy(hwnd, pid) || !IsWindowVisible(hwnd) || hwnd == parent) return true;
                if (parent != IntPtr.Zero && GetWindow(hwnd, 4) != parent) return true;
                if (GetDlgItem(hwnd, 12320) == IntPtr.Zero) return true;
                if (parent == IntPtr.Zero && FindWindowEx(hwnd, IntPtr.Zero, "#32770", null) == IntPtr.Zero)
                    return true;
                result = hwnd;
                return false;
            }, IntPtr.Zero);
            if (result != IntPtr.Zero) return result;
            Thread.Sleep(80);
        } while (watch.ElapsedMilliseconds < 3500);
        return IntPtr.Zero;
    }

    delegate bool EnumWindow(IntPtr hwnd, IntPtr argument);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr argument);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out WindowBounds bounds);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern bool SetWindowText(IntPtr hwnd, string text);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string cls, string name);
    [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr parent, int id);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible accessible);
}
