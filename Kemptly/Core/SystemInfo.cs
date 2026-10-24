using Microsoft.Win32;

namespace Kemptly.Core;

public static class SystemInfo
{
    public static string OsName
    {
        get
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var name = k?.GetValue("ProductName") as string ?? "Windows";
            var build = k?.GetValue("CurrentBuildNumber") as string ?? "";
            var ubr = k?.GetValue("UBR");
            var display = k?.GetValue("DisplayVersion") as string;
            // ProductName still says "Windows 10" on Windows 11.
            if (int.TryParse(build, out var b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
            return $"{name} {display} (build {build}.{ubr})";
        }
    }

    public static string CpuName
    {
        get
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Unknown CPU";
        }
    }

    public static (ulong Total, ulong Available, uint Load) Memory()
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref m);
        return (m.ullTotalPhys, m.ullAvailPhys, m.dwMemoryLoad);
    }

    static long _lastIdle, _lastKernel, _lastUser;

    /// <summary>Total CPU usage since the previous call, in percent.</summary>
    public static double CpuUsage()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        long di = idle - _lastIdle, dk = kernel - _lastKernel, du = user - _lastUser;
        _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
        long total = dk + du; // kernel time includes idle time
        return total <= 0 ? 0 : Math.Clamp((total - di) * 100.0 / total, 0, 100);
    }
}
