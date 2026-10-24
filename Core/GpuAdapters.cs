using System.Runtime.InteropServices;

namespace GameHelper.Core;

/// <summary>一个 GPU 适配器，以及它在性能计数器里的 LUID 标识。</summary>
public sealed record GpuAdapter(
    uint LuidHigh,
    uint LuidLow,
    string Name,
    long DedicatedVideoMemoryBytes)
{
    /// <summary>性能计数器实例名里的 LUID 片段，形如 luid_0x00000000_0x0001bb3b。</summary>
    public string Key => $"luid_0x{LuidHigh:X8}_0x{LuidLow:X8}";

    public bool IsNvidia => Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

    public bool IsIntel => Name.Contains("Intel", StringComparison.OrdinalIgnoreCase);

    public double DedicatedVideoMb => DedicatedVideoMemoryBytes / (1024.0 * 1024.0);

    /// <summary>列表里显示用的短名，例如「NVIDIA RTX 3050」「Intel Iris Xe」。</summary>
    public string ShortName => Shorten(Name);

    private static string Shorten(string raw)
    {
        string s = raw
            .Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("GeForce", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Laptop GPU", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Graphics", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ")
            .Trim();

        return s.Length == 0 ? raw : s;
    }
}

/// <summary>
/// 枚举本机 GPU 适配器，用来把「进程占用的显存」归到具体哪块显卡上。
///
/// 为什么必须做这件事：
///   性能计数器 \GPU Process Memory 是按「适配器 LUID + 进程」组成的实例，
///   把所有实例加起来得到的是**全部显卡**的合计。在混合显卡笔记本上，
///   浏览器之类的后台程序通常跑在核显上，它们的占用会被算进去，
///   但关闭它们一个字节的独显显存也释放不出来。
///   不区分适配器的话，工具给出的「可回收多少显存」会虚高十几倍。
///
/// 实现上直接用 DXGI 的 IDXGIFactory1 枚举适配器读 LUID 与型号，
/// 再去比对计数器实例名里的 luid_ 片段 —— 这样就得到了准确的归属，
/// 而不是靠「哪个数字最接近 nvidia-smi」这种猜测。
/// </summary>
internal static class GpuAdapters
{
    private static IntPtr VtblSlot(IntPtr comObject, int slot) =>
        Marshal.ReadIntPtr(Marshal.ReadIntPtr(comObject), slot * IntPtr.Size);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetDesc1Fn(IntPtr self, IntPtr desc);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ReleaseFn(IntPtr self);

    // IDXGIFactory1
    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    /// <summary>DXGI_ADAPTER_DESC1 的大小（Description[128] + 若干字段）。</summary>
    private const int DescSize = 312;

    // DXGI_ADAPTER_DESC1 内各字段的偏移
    private const int OffDedicatedVideoMemory = 272;
    private const int OffAdapterLuid = 296;

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    /// <summary>枚举全部适配器。任何一步失败都返回空列表，调用方需要能接受这种情况。</summary>
    public static IReadOnlyList<GpuAdapter> Enumerate()
    {
        var result = new List<GpuAdapter>();

        IntPtr factory;
        try
        {
            // 静态只读字段不能直接按 ref 传出，取一份本地副本。
            Guid iid = IidFactory1;
            if (CreateDXGIFactory1(ref iid, out factory) != 0 || factory == IntPtr.Zero)
            {
                return result;
            }
        }
        catch
        {
            return result;
        }

        try
        {
            var enumAdapters = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Fn>(VtblSlot(factory, 12));

            for (uint i = 0; i < 32; i++)
            {
                IntPtr adapter;
                if (enumAdapters(factory, i, out adapter) != 0 || adapter == IntPtr.Zero)
                {
                    break;
                }

                try
                {
                    var getDesc = Marshal.GetDelegateForFunctionPointer<GetDesc1Fn>(VtblSlot(adapter, 10));
                    IntPtr buf = Marshal.AllocHGlobal(DescSize);
                    try
                    {
                        if (getDesc(adapter, buf) != 0)
                        {
                            continue;
                        }

                        string name = Marshal.PtrToStringUni(buf) ?? string.Empty;
                        long dedicated = Marshal.ReadInt64(buf, OffDedicatedVideoMemory);
                        uint low = unchecked((uint)Marshal.ReadInt32(buf, OffAdapterLuid));
                        uint high = unchecked((uint)Marshal.ReadInt32(buf, OffAdapterLuid + 4));

                        result.Add(new GpuAdapter(high, low, name, dedicated));
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                }
                catch
                {
                    // 单个适配器读失败不影响其它适配器
                }
                finally
                {
                    try
                    {
                        Marshal.GetDelegateForFunctionPointer<ReleaseFn>(VtblSlot(adapter, 2))(adapter);
                    }
                    catch
                    {
                        // 忽略
                    }
                }
            }
        }
        catch
        {
            // 枚举整体失败，返回已经拿到的部分
        }
        finally
        {
            try
            {
                Marshal.GetDelegateForFunctionPointer<ReleaseFn>(VtblSlot(factory, 2))(factory);
            }
            catch
            {
                // 忽略
            }
        }

        return result;
    }

    /// <summary>
    /// 挑出「游戏实际在用的那块显卡」。
    ///
    /// 优先选 NVIDIA：本工具服务的对象就是 NVIDIA 独显上的游戏。
    /// 没有 NVIDIA 时退回显存最大的那块（把虚拟显示器之类的适配器排除掉）。
    /// </summary>
    public static GpuAdapter? FindPrimary(IReadOnlyList<GpuAdapter> adapters)
    {
        if (adapters.Count == 0)
        {
            return null;
        }

        foreach (var a in adapters)
        {
            if (a.IsNvidia)
            {
                return a;
            }
        }

        // 虚拟显示适配器的专用显存通常是 0，天然排在后面。
        GpuAdapter best = adapters[0];
        foreach (var a in adapters)
        {
            if (a.DedicatedVideoMemoryBytes > best.DedicatedVideoMemoryBytes)
            {
                best = a;
            }
        }

        return best;
    }
}
