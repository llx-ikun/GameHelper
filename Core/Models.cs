namespace GameHelper.Core;

/// <summary>一次 nvidia-smi 查询得到的显卡即时状态。</summary>
public sealed record GpuInfo(
    string Name,
    double TempC,
    double UtilPct,
    double MemUsedMb,
    double MemTotalMb,
    double GpuClockMhz,
    double MemClockMhz,
    double PowerW)
{
    public static readonly GpuInfo Empty = new(string.Empty, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>是否读到了有效数据（显存总量 &gt; 0）。</summary>
    public bool HasData => MemTotalMb > 0;

    /// <summary>已用显存百分比。</summary>
    public double Pct => MemTotalMb > 0 ? 100.0 * MemUsedMb / MemTotalMb : 0;

    public double FreeMb => Math.Max(0, MemTotalMb - MemUsedMb);
}

/// <summary>显存占用者的类别。类别决定它能不能被结束。</summary>
public enum ProcClass
{
    /// <summary>普通后台程序。可以结束以回收显存。</summary>
    Other,

    /// <summary>游戏自身。外部无法释放，也绝不去碰。</summary>
    Game,

    /// <summary>Windows 与桌面组件。结束会出问题。</summary>
    System,
}

/// <summary>某个进程当前持有的显存。</summary>
public sealed record ProcVram(
    int Pid,
    string Name,
    double LocalMb,
    double SharedMb,
    ProcClass Class,
    string AdapterKey = "",
    string AdapterName = "",
    bool OnPrimaryAdapter = true)
{
    public double TotalMb => LocalMb + SharedMb;

    /// <summary>界面上给出的处置建议。措辞与工具的诚实边界保持一致。</summary>
    public string Note => Class switch
    {
        ProcClass.Game => "无法从外部释放",
        ProcClass.System => "不要关闭",
        _ when !OnPrimaryAdapter => "占用的是其他显卡",
        _ when ProcessClassifier.IsSecurityOrInputSoftware(Name) => "安全软件，慎关",
        _ => "可结束以回收",
    };

    /// <summary>是否属于安全/输入法类软件。</summary>
    public bool IsSensitive => Class == ProcClass.Other && ProcessClassifier.IsSecurityOrInputSoftware(Name);

    public string ClassText => Class switch
    {
        ProcClass.Game => "游戏",
        ProcClass.System => "系统",
        _ => "其他",
    };
}

/// <summary>
/// 采样线程产出的一个完整快照。不可变，可安全跨线程传递。
///
/// 帧数用 <see cref="Frame"/> 单独带出来，而不是塞进基本参数：
/// 它需要管理员权限才拿得到，多数时候是不可用的，
/// 让它作为可选字段，缺了也只是这一项显示「不可用」，不牵动其它数据。
/// </summary>
public sealed record Snapshot(
    DateTime At,
    GpuInfo Gpu,
    IReadOnlyList<ProcVram> Procs,
    double RamUsedMb,
    double RamTotalMb,
    string Throttle,
    bool GameRunning,
    DateTime? GameStartedAt,
    double GameRamMb,
    string PrimaryAdapterName = "",
    IReadOnlyList<GpuAdapter>? Adapters = null,
    FrameStats? Frame = null,
    TargetKind Kind = TargetKind.Naraka,
    string TargetName = "")
{
    public static readonly Snapshot Empty = new(
        DateTime.MinValue, GpuInfo.Empty, Array.Empty<ProcVram>(), 0, 0, "none", false, null, 0);

    /// <summary>这一份快照是不是《永劫无间》口径 —— 决定汇总允不允许给结论。</summary>
    public bool NarakaScope => Kind == TargetKind.Naraka;

    /// <summary>帧数。没有数据时是一个「不可用」的占位值，不会是 null。</summary>
    public FrameStats Frames => Frame ?? FrameStats.Unavailable;

    /// <summary>是否真的拿到了帧数（有权限、且在采集中）。</summary>
    public bool HasFps => Frame is { Available: true } && Frame.Fps > 0;

    /// <summary>帧数不可用时，给用户看的原因。</summary>
    public string? FpsError => Frame?.Error;

    /// <summary>本机一共有几块 GPU 适配器。</summary>
    public int AdapterCount => Adapters?.Count ?? 0;

    /// <summary>是否是多显卡环境（笔记本的核显 + 独显就属于这种）。</summary>
    public bool MultiAdapter => AdapterCount > 1;

    private double SumClass(ProcClass cls)
    {
        double s = 0;
        foreach (var p in Procs)
        {
            // 只统计主显卡（游戏实际运行的那块）上的占用。
            // 把核显上的占用算进来，会让「关闭后台可回收多少显存」虚高十几倍。
            if (p.Class == cls && p.OnPrimaryAdapter)
            {
                s += p.LocalMb;
            }
        }
        return Math.Round(s, 1);
    }

    private int CountClass(ProcClass cls)
    {
        int n = 0;
        foreach (var p in Procs)
        {
            if (p.Class == cls && p.OnPrimaryAdapter)
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>游戏自身占用的本机显存（不可外部释放）。</summary>
    public double GameVramMb => SumClass(ProcClass.Game);

    public int GameProcCount => CountClass(ProcClass.Game);

    /// <summary>Windows 组件占用的本机显存（勿动）。</summary>
    public double SystemVramMb => SumClass(ProcClass.System);

    /// <summary>其他后台程序占用的本机显存（可结束回收）。</summary>
    public double OtherVramMb => SumClass(ProcClass.Other);

    public int OtherProcCount => CountClass(ProcClass.Other);

    /// <summary>主显卡上的进程显存合计。</summary>
    public double PrimaryAdapterProcessMb => Math.Round(GameVramMb + SystemVramMb + OtherVramMb, 1);

    /// <summary>其他显卡（通常是核显）上被后台程序占用的显存。关掉它们不会释放独显显存。</summary>
    public double OtherAdapterVramMb
    {
        get
        {
            double s = 0;
            foreach (var p in Procs)
            {
                if (!p.OnPrimaryAdapter)
                {
                    s += p.LocalMb;
                }
            }
            return Math.Round(s, 1);
        }
    }

    /// <summary>当前 nvidia-smi 报告的全部显存占用（含非进程持有的部分）。</summary>
    public double GpuUsedMb => Gpu.MemUsedMb;

    /// <summary>
    /// 「探针能解释的部分」与「驱动报告的占用」之间的差额。
    /// 差额通常是驱动自身、非进程分配，以及瞬间未被计数器捕获的部分。
    /// </summary>
    public double UnexplainedMb => Math.Max(0, Math.Round(Gpu.MemUsedMb - PrimaryAdapterProcessMb, 1));
}

/// <summary>显存告警的档位。</summary>
public enum VramState
{
    Ok,
    Warn,
    Crit,
}
