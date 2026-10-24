using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GameHelper.Core;

/// <summary>某个进程持有的显存（M 字节）。</summary>
public readonly record struct ProcMemory(double LocalMb, double SharedMb);

/// <summary>
/// 计数器里的一个原始实例：某个进程在某块显卡上占用的显存。
///
/// 保留 AdapterKey 是关键 —— 同一进程在核显和独显上会各有一条记录，
/// 混在一起加总就会得出「关闭后台能回收几百 MB 独显显存」这种错得离谱的结论。
/// </summary>
public sealed record GpuMemInstance(int Pid, string AdapterKey, string InstanceName, double LocalMb, double SharedMb);

/// <summary>
/// 读取「每个进程占用了多少显存」。
///
/// 数据源是 PDH 计数器 <c>\GPU Process Memory(*)\Local Usage</c> 与
/// <c>\Shared Usage</c> —— 也就是任务管理器「详细信息」页里那两个
/// 「专用/共享 GPU 内存」列的同一来源。
///
/// 为什么直接 P/Invoke pdh.dll，而不用 System.Diagnostics.PerformanceCounter：
///   * 那是独立的 NuGet 包，会破坏「零依赖、可离线构建」这个前提；
///   * 我们要的只是「一个性能对象的全部实例」这一次批量读取，
///     PerformanceCounter 的逐实例 API 反而更绕。
///
/// 关于实例名：形如 pid_1234_luid_0x00000000_0x0000C4B2_phys_0。
/// LUID 会随驱动重载而变化，所以解析时只认 pid_ 前缀，不去匹配 LUID —— 
/// 这是 PowerShell 版本踩过的坑，这里沿用同样的做法。
/// </summary>
public sealed class GpuMemoryProbe : IDisposable
{
    private const uint PdhFmtDouble = 0x0000_0200;
    private const uint ErrorSuccess = 0;

    private static readonly Regex PidPattern = new(@"pid_(\d+)_", RegexOptions.Compiled);

    // 连 luid_ 前缀一起捕获。GpuAdapter.Key 也是这个形式（luid_0x..._0x...），
    // 两边必须完全同构才能比对得上 —— 少一个前缀就会导致所有实例都匹配失败。
    private static readonly Regex LuidPattern = new(@"(luid_0x[0-9a-fA-F]+_0x[0-9a-fA-F]+)", RegexOptions.Compiled);

    private IntPtr _query;
    private IntPtr _localCounter;
    private IntPtr _sharedCounter;
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>探针是否可用。不可用时占用列表会一直是空的。</summary>
    public bool Available { get; }

    /// <summary>不可用的原因，供界面显示。</summary>
    public string? LastError { get; private set; }

    public GpuMemoryProbe()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != ErrorSuccess)
        {
            LastError = "无法创建性能数据查询（PdhOpenQuery 失败）";
            return;
        }

        uint e1 = PdhAddEnglishCounterW(_query, @"\GPU Process Memory(*)\Local Usage", IntPtr.Zero, out _localCounter);
        if (e1 != ErrorSuccess)
        {
            _localCounter = IntPtr.Zero;
        }

        uint e2 = PdhAddEnglishCounterW(_query, @"\GPU Process Memory(*)\Shared Usage", IntPtr.Zero, out _sharedCounter);
        if (e2 != ErrorSuccess)
        {
            _sharedCounter = IntPtr.Zero;
        }

        if (_localCounter == IntPtr.Zero && _sharedCounter == IntPtr.Zero)
        {
            LastError = "系统未提供 GPU Process Memory 计数器（可能缺少显卡驱动或计数器已损坏）";
            CloseQuery();
            return;
        }

        // 这类「按进程」的计数器第一次采集拿不到有效值，需要采集两次。
        // 判断依据：首轮 PdhCollectQueryData 会返回 PDH_NO_DATA 或
        // 各实例 CStatus 非 0。
        PdhCollectQueryData(_query);
        Thread.Sleep(200);
        PdhCollectQueryData(_query);

        Available = true;
    }

    /// <summary>
    /// 读取当前所有「进程 × 显卡」的显存占用。
    ///
    /// 采集失败时返回空列表，而不是抛异常 —— 采样线程不能因为一次
    /// 计数器抖动就整个停掉。
    /// </summary>
    public List<GpuMemInstance> ReadRaw()
    {
        var result = new List<GpuMemInstance>();
        if (!Available || _disposed)
        {
            return result;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return result;
            }

            if (PdhCollectQueryData(_query) != ErrorSuccess)
            {
                return result;
            }

            var local = ReadArray(_localCounter);
            var shared = ReadArray(_sharedCounter);

            // 先把「本机显存」的每个实例建成一条记录，再把「共享显存」
            // 按同一个实例名补进去。两个计数器实例名一一对应。
            var byInstance = new Dictionary<string, GpuMemInstance>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in local)
            {
                var inst = Parse(kv.Key, kv.Value, 0);
                if (inst is not null)
                {
                    byInstance[kv.Key] = inst;
                }
            }

            foreach (var kv in shared)
            {
                var pid = ParsePid(kv.Key);
                var key = ParseAdapterKey(kv.Key);
                if (pid <= 0 || key is null)
                {
                    continue;
                }

                if (byInstance.TryGetValue(kv.Key, out var prev))
                {
                    byInstance[kv.Key] = prev with { SharedMb = kv.Value };
                }
                else
                {
                    byInstance[kv.Key] = new GpuMemInstance(pid, key, kv.Key, 0, kv.Value);
                }
            }

            result.AddRange(byInstance.Values);
        }

        return result;
    }

    private static GpuMemInstance? Parse(string instanceName, double localMb, double sharedMb)
    {
        int pid = ParsePid(instanceName);
        string? key = ParseAdapterKey(instanceName);
        if (pid <= 0 || key is null)
        {
            return null;
        }
        return new GpuMemInstance(pid, key, instanceName, localMb, sharedMb);
    }

    private static string? ParseAdapterKey(string instanceName)
    {
        var m = LuidPattern.Match(instanceName);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static int ParsePid(string instanceName)
    {
        var m = PidPattern.Match(instanceName);
        if (!m.Success)
        {
            return -1;
        }
        return int.TryParse(m.Groups[1].Value, out var pid) ? pid : -1;
    }

    /// <summary>
    /// 一次读取某个计数器的全部实例。返回 实例名 -> 数值(MB)。
    ///
    /// PDH 的两段式调用：先用空缓冲问出所需字节数（返回 PDH_MORE_DATA），
    /// 再自己分配缓冲并第二次调用。缓冲由我们负责释放。
    /// </summary>
    private static Dictionary<string, double> ReadArray(IntPtr counter)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (counter == IntPtr.Zero)
        {
            return map;
        }

        uint size = 0;
        uint count = 0;
        PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref size, ref count, IntPtr.Zero);
        if (size == 0 || count == 0)
        {
            return map;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint rc = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref size, ref count, buffer);
            if (rc != ErrorSuccess)
            {
                return map;
            }

            int itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
            for (uint i = 0; i < count; i++)
            {
                IntPtr p = IntPtr.Add(buffer, (int)i * itemSize);
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(p);
                if (item.szName == IntPtr.Zero)
                {
                    continue;
                }

                // CStatus != 0 表示这一项这一轮没采到有效值，跳过。
                if (item.FmtValue.CStatus != ErrorSuccess)
                {
                    continue;
                }

                string? name = Marshal.PtrToStringUni(item.szName);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                map[name] = item.FmtValue.doubleValue / (1024.0 * 1024.0);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return map;
    }

    private void CloseQuery()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
        _localCounter = IntPtr.Zero;
        _sharedCounter = IntPtr.Zero;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            CloseQuery();
        }
    }

    // ------------------------------------------------------------------
    // PDH 互操作
    // ------------------------------------------------------------------

    /// <summary>与 PDH_FMT_COUNTERVALUE 布局一致（4 字节状态 + 8 字节联合体，按 8 字节对齐）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double doubleValue;
    }

    /// <summary>与 PDH_FMT_COUNTERVALUE_ITEM_W 布局一致。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public IntPtr szName;
        public PdhFmtCounterValue FmtValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    /// <summary>
    /// 用「英文计数器名」添加计数器。中文系统上本地化名称是「GPU 进程内存」，
    /// 这个 API 会自己完成映射，所以不需要为不同语言系统准备两套名字。
    /// </summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, ref uint lpdwItemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr hQuery);
}
