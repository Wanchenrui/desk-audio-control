using System;
using System.Linq;

internal static class RouteTransaction
{
    internal static bool Equal(string a,string b) { return String.Equals(a,b,StringComparison.OrdinalIgnoreCase); }

    public static string[] Apply(string[] targets, Func<int,string> read, Action<int,string> write, string[] expected, Action writesComplete = null)
    {
        if (targets == null || targets.Length != 3 || targets.Any(String.IsNullOrEmpty))
            throw new InvalidOperationException("缺少可恢复的默认端口。");
        var old = Enumerable.Range(0,3).Select(read).ToArray();
        if (expected != null && !old.SequenceEqual(expected,StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("默认设备已变化，已保留当前选择。");
        try
        {
            for (int role = 0; role < 3; role++)
                if (!Equal(old[role],targets[role])) write(role,targets[role]);
            if (writesComplete != null) writesComplete();
            for (int role = 0; role < 3; role++)
                if (!Equal(read(role),targets[role])) throw new InvalidOperationException("Windows 未应用默认端口。");
            return old;
        }
        catch (Exception cause)
        {
            bool restored = true;
            for (int role = 0; role < 3; role++)
            {
                try
                {
                    if (Equal(read(role),old[role])) continue;
                    if (old[role] == null) { restored = false; continue; }
                    write(role,old[role]);
                    if (!Equal(read(role),old[role])) restored = false;
                }
                catch (Exception) { restored = false; }
            }
            if (!restored) throw new InvalidOperationException("Windows 无法还原所有默认端口。",cause);
            throw;
        }
    }
}
