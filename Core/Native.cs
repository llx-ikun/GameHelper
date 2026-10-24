using System.Runtime.InteropServices;

namespace GameHelper.Core;

/// <summary>
/// 少量 Win32 互操作。
///
/// 用 GlobalMemoryStatusEx 而不是 WMI 取物理内存：WMI 首次查询要起
/// WmiPrvSE 并加载提供程序，单次开销在百毫秒级；这个调用是微秒级，
/// 每秒采一次毫无压力。
/// </summary>
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>返回 (已用 MB, 总量 MB)。失败时返回 (0, 0)。</summary>
    public static (double UsedMb, double TotalMb) GetPhysicalMemory()
    {
        var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref st))
        {
            return (0, 0);
        }

        const double Mb = 1024.0 * 1024.0;
        double total = st.ullTotalPhys / Mb;
        double avail = st.ullAvailPhys / Mb;
        return (Math.Round(total - avail, 0), Math.Round(total, 0));
    }
}
