using System.Globalization;
using System.Text;

namespace GameHelper.Core;

/// <summary>监控开始/停止等状态变化，供界面提示。</summary>
public enum MonitorState
{
    Idle,
    Recording,
}

/// <summary>
/// 一次显存监控会话。
///
/// 与「显存监测提醒.ps1」的对应关系：这里是进程内实现，不再需要单开一个
/// PowerShell 窗口，也不需要脚本里那套「等游戏启动 -> 预热 20 秒 -> 起循环」的
/// 编排 —— 采样器一直在跑，记录只是把已经采到的快照落盘。
///
/// 告警方式改成了托盘气泡而不是模态弹窗：原来的 WScript.Shell 弹窗会抢走
/// 焦点，在全屏游戏里可能把游戏踢出全屏，这在一个「给游戏用的工具」里
/// 是帮倒忙。
/// </summary>
public sealed class MonitorSession
{
    /// <summary>
    /// 判定名。自检是靠在汇总文本里找这几个字来验证「判对了没」的，
    /// 所以名字必须是常量、两边共用 —— 否则改一次文案，
    /// 断言就变成假失败（或者更糟：永远通过）。
    /// </summary>
    internal const string NameVramClimb = "显存持续累积";
    internal const string NameDecay = "全程持续变卡";

    private readonly List<string> _recent = new();
    private StreamWriter? _writer;
    private DateTime _startedAt;
    private DateTime? _warnAt;
    private DateTime? _critAt;
    private double _peakMb;
    private double _peakPct;
    private int _samples;
    private VramState _state = VramState.Ok;

    // 帧数统计（只在真的拿到帧数时累计，见 Record 里的注释）
    //
    // 这里存的是**原始样本序列**，而不是「最高帧率 / 最差 1% Low」这类极值。
    // 第一版就是存极值的，第一份真实日志汇总出来是「最高 695.6 fps、
    // 1% Low 1.2 fps」—— 两个数字全都来自加载和过场界面：那几十行里温度
    // 只有 59°C、GPU 占用不到 70%，游戏根本没在渲染画面，PresentMon 把不渲染
    // 的 Present 也计入了帧。极值对离群点毫无抵抗力，一次过场就能毁掉整场结论。
    // 改存全序列后，汇总用分位数说话，加载段自然被压到尾巴上去。
    private readonly List<double> _fpsAll = new();
    private readonly List<double> _lowAll = new();
    private readonly List<double> _atAll = new();
    private readonly List<double> _pctAll = new();
    private readonly List<double> _tempAll = new();
    private readonly List<double> _clockAll = new();

    // 下面三列只在「外部目标」口径下才会被汇总用到（见 AppendExternalScope），
    // 但它们对《永劫无间》口径也是纯观测数据，一并收着不增加复杂度。
    private readonly List<double> _utilAll = new();
    private readonly List<double> _memAll = new();
    private readonly List<double> _gameRamAll = new();
    private int _critRows;
    private double _tempMax;

    /// <summary>
    /// 这一份记录盯的是什么目标。决定汇总里允不允许给结论。
    ///
    /// 默认 <see cref="TargetKind.Naraka"/> 是为了让回放旧日志的路径不受影响 ——
    /// 那些日志本来就是《永劫无间》的，且 CSV 里没有这一列。
    /// </summary>
    private TargetKind _kind = TargetKind.Naraka;

    /// <summary>外部目标的进程名，用于汇总标题。回放旧日志时为空。</summary>
    private string _targetName = string.Empty;
    private string? _fpsError;

    /// <summary>
    /// 逐行采样，供对局分析使用。存的是结构而不是几个平行数组 ——
    /// 平行数组只要有一列漏 append，配对就会悄悄错位且不报错。
    /// </summary>
    private readonly List<SampleRow> _rows = new();

    /// <summary>最后一次采样的时刻。停止记录后靠它算出真实时长。</summary>
    private DateTime _lastAt;

    /// <summary>记录文件的完整路径；未开始记录时为 null。</summary>
    public string? CsvPath { get; private set; }

    public string OutputDir { get; set; } = AppPaths.OutputDir;

    /// <summary>警告阈值（显存百分比）。</summary>
    public double WarnPct { get; set; } = 82;

    /// <summary>危急阈值（显存百分比）。</summary>
    public double CritPct { get; set; } = 90;

    public bool Active => _writer is not null;

    public MonitorState State => Active ? MonitorState.Recording : MonitorState.Idle;

    public DateTime StartedAt => _startedAt;

    /// <summary>
    /// 回放一份已有日志时，每行的时间取自 CSV 的 Elapsed_s 列。
    /// 不设的话 <see cref="Record"/> 会拿 DateTime.Now 去算，而回放一整份日志
    /// 只要几百毫秒 —— 那样所有「随时间变化」的判定都会失真成一条竖线。
    /// 仅供 <c>--resummary</c> 使用，正常记录时保持 null。
    /// </summary>
    internal double? ReplayElapsedSec { get; set; }

    /// <summary>
    /// 回放模式下的开始时刻（取日志第一行的时间）。
    /// 复用 <see cref="Start"/> 的重置逻辑，只把起点改回日志里那个。
    /// </summary>
    internal void StartReplay(DateTime startedAt)
    {
        Start();
        _startedAt = startedAt;
    }

    /// <summary>
    /// 已经记录的时长。
    ///
    /// 早先写成「不在记录中就返回 Zero」，于是汇总里永远是「时长 0 秒」——
    /// 因为 <see cref="BuildSummary"/> 正是在 <see cref="Stop"/> 之后调用的，
    /// 那时 Active 已经是 false。改成记住最后一次采样时刻，停止后照样算得出来。
    /// </summary>
    public TimeSpan Elapsed
    {
        get
        {
            if (_startedAt == default || _lastAt <= _startedAt)
            {
                return TimeSpan.Zero;
            }

            // 一律以最后一次采样为准，不去问 Active。
            // 早先这里写成「记录中就返回当下」，于是 BuildSummary() 在 Stop()
            // 之前被调用时，时长里混进了「记录结束到生成汇总」的那一段 ——
            // 一份 99 分钟的日志曾被算成 117 分钟。
            return _lastAt - _startedAt;
        }
    }

    public int Samples => _samples;

    public double PeakMb => _peakMb;

    public double PeakPct => _peakPct;

    /// <summary>当前档位。</summary>
    public VramState VramState => _state;

    public DateTime? FirstWarnAt => _warnAt;

    public DateTime? FirstCritAt => _critAt;

    /// <summary>
    /// 档位发生变化。参数：(新档位, 旧档位)。
    ///
    /// 注意这里**只报告变化，不负责提醒**。越线提醒由 <see cref="VramNotifier"/>
    /// 独立承担 —— 它每个采样周期都在跑，不依赖「是否正在记录」。
    /// 早先提醒是挂在本类的 Record() 里的，而不开记录时 Record() 直接返回，
    /// 于是「随手挂着工具打游戏」这个最需要提醒的场景反而完全不提醒。
    /// </summary>
    public event Action<VramState, VramState>? StateChanged;

    /// <summary>每次落盘一行后触发，用于界面刷新统计数字。</summary>
    public event Action? Progress;

    private const string Header =
        "Timestamp,Elapsed_s,VRAMUsed_MB,VRAMTotal_MB,Pct,GPUTemp_C,GPUUtil_pct," +
        "GPUClock_MHz,MemClock_MHz,Power_W,Throttle,RAMUsed_MB,RAMTotal_MB," +
        "GameRAM_MB,GameVRAM_MB,OtherVRAM_MB,SystemVRAM_MB,FPS,FPS_1Low,FrameCount,State";

    public void Start()
    {
        Stop();

        Directory.CreateDirectory(OutputDir);
        string stamp = DateTime.Now.ToString("MMdd-HHmm");
        CsvPath = Path.Combine(OutputDir, "性能日志-" + stamp + ".csv");

        _writer = new StreamWriter(CsvPath, append: false, new UTF8Encoding(true));
        _writer.WriteLine(Header);

        _startedAt = DateTime.Now;
        _warnAt = null;
        _critAt = null;
        _peakMb = 0;
        _peakPct = 0;
        _samples = 0;
        _state = VramState.Ok;
        _recent.Clear();
        _fpsAll.Clear();
        _lowAll.Clear();
        _atAll.Clear();
        _pctAll.Clear();
        _tempAll.Clear();
        _clockAll.Clear();
        _critRows = 0;
        _tempMax = 0;
        _rows.Clear();
        _lastAt = _startedAt;
        _fpsError = null;
    }

    public void Stop()
    {
        if (_writer is null)
        {
            return;
        }

        try
        {
            _writer.Flush();
            _writer.Dispose();
        }
        catch
        {
            // 忽略
        }
        _writer = null;
    }

    /// <summary>把一个快照写进记录，并处理越线告警。应在界面线程调用。</summary>
    public void Record(Snapshot s)
    {
        if (_writer is null || !s.Gpu.HasData)
        {
            return;
        }

        // 口径跟着第一份有效快照定下来。中途切换目标会让前后样本不可比，
        // 所以记首次的值；真切换了应该在界面上重新开始记录，而不是混在一份里。
        if (_samples == 0)
        {
            _kind = s.Kind;
            _targetName = s.TargetName;
        }

        _samples++;
        double pct = s.Gpu.Pct;

        if (s.Gpu.MemUsedMb > _peakMb)
        {
            _peakMb = s.Gpu.MemUsedMb;
        }
        if (pct > _peakPct)
        {
            _peakPct = pct;
        }

        // 只有「真的拿到帧数」的行才计入帧数统计。
        // 拿不到时把原因存下来，汇总里如实写「不可用」，绝不写成 0 ——
        // 0 fps 会被当成一次真实测量，那是在造假数据。
        // 回放旧日志时，行里的时间取自 CSV 的 Elapsed_s，不是当下。
        _lastAt = ReplayElapsedSec.HasValue
            ? _startedAt.AddSeconds(ReplayElapsedSec.Value)
            : DateTime.Now;

        if (s.Gpu.TempC > _tempMax)
        {
            _tempMax = s.Gpu.TempC;
        }


        var fr = s.Frames;
        if (fr.Available && fr.Fps > 0)
        {
            // 存原始样本，汇总时算分位数。极值会被加载/过场污染，见字段处注释。
            _fpsAll.Add(fr.Fps);
            _atAll.Add((_lastAt - _startedAt).TotalSeconds);
            _pctAll.Add(pct);
            _tempAll.Add(s.Gpu.TempC);
            _clockAll.Add(s.Gpu.GpuClockMhz);
            _utilAll.Add(s.Gpu.UtilPct);
            _memAll.Add(s.Gpu.MemUsedMb);
            _gameRamAll.Add(s.GameRamMb);
            if (fr.Fps1Low > 0)
            {
                _lowAll.Add(fr.Fps1Low);
            }
        }
        else if (!fr.Available && fr.Error is not null && s.GameRunning)
        {
            // 只在「游戏在跑却仍然拿不到帧数」时记原因 ——
            // 游戏压根没启动时的「不可用」不是故障，不该写进失败原因里。
            _fpsError = fr.Error;
        }

        _rows.Add(new SampleRow(
            At: (_lastAt - _startedAt).TotalSeconds,
            GameRamMb: s.GameRamMb,
            MemUsedMb: s.Gpu.MemUsedMb,
            Pct: pct,
            TempC: s.Gpu.TempC,
            UtilPct: s.Gpu.UtilPct,
            Fps: fr.Fps,
            Low: fr.Fps1Low,
            HasFps: fr.Available && fr.Fps > 0));

        var next = pct >= CritPct ? VramState.Crit
                 : pct >= WarnPct ? VramState.Warn
                 : VramState.Ok;

        if (pct >= CritPct)
        {
            _critRows++;
        }

        if (next != _state)
        {
            var prev = _state;
            _state = next;

            // 只记录首次越线的时间点，供汇总使用；提醒不在这里发。
            //
            // 从「正常」一步跳到「危急」时，必须**同时补记**警告时刻。少了这一句，
            // 汇总就会排出「首次危急 20:30:41 / 首次警告 21:29:31」这种
            // 危急比警告还早的顺序 —— 这不是假想，是用户日志里真出现过的一行。
            // 时间点取本行采样的时刻，不是 DateTime.Now。
            // 回放旧日志时必须这样，否则「首次警告」会记成重算那一秒的钟点。
            if (next == VramState.Crit && prev != VramState.Crit)
            {
                _critAt = _lastAt;
                _warnAt ??= _critAt;
            }
            else if (next == VramState.Warn && prev == VramState.Ok)
            {
                _warnAt ??= _lastAt;
            }

            StateChanged?.Invoke(next, prev);
        }

        var el = (int)(_lastAt - _startedAt).TotalSeconds;
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(256);
        sb.Append(DateTime.Now.ToString("HH:mm:ss")).Append(',')
          .Append(el.ToString(ci)).Append(',')
          .Append(s.Gpu.MemUsedMb.ToString("0.0", ci)).Append(',')
          .Append(s.Gpu.MemTotalMb.ToString("0", ci)).Append(',')
          .Append(pct.ToString("0.0", ci)).Append(',')
          .Append(s.Gpu.TempC.ToString("0", ci)).Append(',')
          .Append(s.Gpu.UtilPct.ToString("0", ci)).Append(',')
          .Append(s.Gpu.GpuClockMhz.ToString("0", ci)).Append(',')
          .Append(s.Gpu.MemClockMhz.ToString("0", ci)).Append(',')
          .Append(s.Gpu.PowerW.ToString("0.0", ci)).Append(',')
          .Append(s.Throttle).Append(',')
          .Append(s.RamUsedMb.ToString("0", ci)).Append(',')
          .Append(s.RamTotalMb.ToString("0", ci)).Append(',')
          .Append(s.GameRamMb.ToString("0", ci)).Append(',')
          .Append(s.GameVramMb.ToString("0.0", ci)).Append(',')
          .Append(s.OtherVramMb.ToString("0.0", ci)).Append(',')
          .Append(s.SystemVramMb.ToString("0.0", ci)).Append(',')
          .Append(FpsCell(fr.Fps)).Append(',')
          .Append(FpsCell(fr.Fps1Low)).Append(',')
          .Append(fr.Available ? fr.Frames.ToString(ci) : string.Empty).Append(',')
          .Append(_state.ToString().ToLowerInvariant());

        try
        {
            _writer.WriteLine(sb.ToString());
            // 每 30 秒冲刷一次盘。脚本版曾因为窗口被提前关掉而丢掉整轮数据，
            // 这里是同一个教训的修复：先落盘，再谈别的。
            if (_samples % 20 == 0)
            {
                _writer.Flush();
            }
        }
        catch
        {
            // 忽略写入失败
        }

        Progress?.Invoke();
    }

    /// <summary>
    /// 帧数列的写法：有值写数字，没值写**空**。
    ///
    /// 空和 0 在 CSV 里差别极大 —— 0 会被分析脚本当成一次真实测量，
    /// 空则明确表示「这一行没测到」。工具可以没有数据，但不可以编数据。
    /// </summary>
    private static string FpsCell(double v)
    {
        return v > 0 ? v.ToString("0.0", CultureInfo.InvariantCulture) : string.Empty;
    }

    /// <summary>
    /// 分位数。p 取 0..1。内部复制一份再排序，不打乱调用方的样本顺序 ——
    /// 这些序列是要按原始顺序做配对相关分析的。
    /// </summary>
    internal static double Quantile(List<double> src, double p)
    {
        if (src.Count == 0)
        {
            return 0;
        }

        var v = new List<double>(src);
        v.Sort();
        if (v.Count == 1)
        {
            return v[0];
        }

        int i = (int)Math.Round((v.Count - 1) * Math.Clamp(p, 0, 1));
        return v[i];
    }

    internal static double Median(List<double> src) => Quantile(src, 0.5);

    /// <summary>
    /// 按采集顺序把样本切成三段，各自取中位数。
    ///
    /// 判断「是不是一直在往一个方向走」，得靠这个，不能只看相关系数：
    /// 一个**只发生一次**的台阶 —— 比如第一局特别流畅、之后一直持平 ——
    /// 照样能算出 +0.48 的正相关，可那不是累积。第一版就是这么误判的，
    /// 拿用户那份日志跑出来「显存持续累积：成立」，实际每局回大厅都会释放。
    /// </summary>
    internal static (double A, double B, double C) Thirds(List<double> src)
    {
        int n = src.Count;
        int k = n / 3;
        if (k < 30)
        {
            // 样本太少，三段没有意义，退化成同一个值 —— 判定自然不成立。
            double m = Median(src);
            return (m, m, m);
        }

        return (Median(src.GetRange(0, k)),
                Median(src.GetRange(k, k)),
                Median(src.GetRange(2 * k, n - 2 * k)));
    }

    /// <summary>
    /// Pearson 相关系数。配对样本少于 30 时返回 null ——
    /// 在那么小的样本上谈「趋势」，和编数据没有区别。
    /// </summary>
    internal static double? Corr(List<double> xs, List<double> ys)
    {
        int n = Math.Min(xs.Count, ys.Count);
        if (n < 30)
        {
            return null;
        }

        double mx = 0, my = 0;
        for (int i = 0; i < n; i++)
        {
            mx += xs[i];
            my += ys[i];
        }
        mx /= n;
        my /= n;

        double num = 0, dx = 0, dy = 0;
        for (int i = 0; i < n; i++)
        {
            double a = xs[i] - mx;
            double b = ys[i] - my;
            num += a * b;
            dx += a * a;
            dy += b * b;
        }

        if (dx <= 0 || dy <= 0)
        {
            return null;
        }

        return num / (Math.Sqrt(dx) * Math.Sqrt(dy));
    }

    private static string R(double? v)
    {
        return v.HasValue ? (v.Value >= 0 ? "+" : "") + v.Value.ToString("0.00") : "样本不足";
    }

    /// <summary>生成一份纯文本汇总，用于保存或复制。</summary>
    public string BuildSummary()
    {
        var sb = new StringBuilder();

        // 标题跟着目标走：外部目标下还顶着《永劫无间》的名字，
        // 用户会以为工具认错了进程。
        sb.AppendLine(_kind == TargetKind.Naraka
            ? "游戏助手 - 监控汇总"
            : "监控汇总 - " + (string.IsNullOrEmpty(_targetName) ? "外部目标" : _targetName));
        sb.AppendLine(new string('=', 60));
        sb.AppendLine("采样数   : " + _samples);
        sb.AppendLine("时长     : " + (int)Elapsed.TotalSeconds + " 秒");
        sb.AppendLine("开始时间 : " + (_startedAt == default ? "-" : _startedAt.ToString("yyyy-MM-dd HH:mm:ss")));
        sb.AppendLine("显存峰值 : " + _peakMb.ToString("0") + " MB（" + _peakPct.ToString("0.0") + "%）");
        sb.AppendLine("首次警告 : " + (_warnAt?.ToString("HH:mm:ss") ?? "未触发"));
        sb.AppendLine("首次危急 : " + (_critAt?.ToString("HH:mm:ss") ?? "未触发"));
        sb.AppendLine("阈值     : 警告 " + WarnPct.ToString("0") + "% / 危急 " + CritPct.ToString("0") + "%");
        sb.AppendLine("日志文件 : " + (CsvPath ?? "-"));
        sb.AppendLine();

        // ---- 口径分叉（必须在帧数节之前）----
        // 这一份记录盯的不是《永劫无间》时，判定层、对局拆分、针对性建议
        // **全部闭嘴**，只输出原始观测值。
        //
        // 为什么必须这样：那 6 条判定的阈值（显存 15 个百分点、帧率降 10%、
        // 切局 1200 MB、"在打" GPU 60%、建议帧率上限）全是在这台机器、
        // 这一份《永劫无间》日志上标定出来的。换一个游戏，同样的数字
        // 就是假结论 —— 比如「把帧率上限设到 117」对 CS2 是个离谱建议。
        // 这正是迭代 7 花大力气避免的「安静地给错结论」，只是换了方向。
        //
        // 不报不等于没价值：帧率中位、1% Low、显存、温度、占用、频率
        // 都是**纯观测**，不含任何阈值，跨游戏依然成立。
        //
        // 分叉点放在这里而不是帧数节之后：帧数那节的标题写着
        // 「能否定性越玩越卡」，本身就是永劫无间语境，外部口径不该有它。
        // 放后面会让帧数被打印两遍（第一版就是这么错的）。
        if (_kind != TargetKind.Naraka)
        {
            AppendExternalScope(sb);
            return sb.ToString();
        }

        sb.AppendLine("帧数（能否定性「越玩越卡」的关键）");
        sb.AppendLine(new string('-', 60));
        if (_fpsAll.Count == 0)
        {
            sb.AppendLine("帧数     : 不可用 —— " + (_fpsError ?? "本次未采集到"));
            sb.AppendLine();
            sb.AppendLine("这一轮**仍然无法判断卡顿原因**。");

            // 只有真是权限问题时才提管理员。别的原因（没开游戏、PresentMon 没找到）
            // 也给同一句建议，等于把用户往错的方向支。
            bool perm = _fpsError is not null
                && _fpsError.Contains("管理员", StringComparison.Ordinal);
            if (perm)
            {
                sb.AppendLine("请以管理员身份重新运行本工具，再打一局 —— 这是目前唯一的缺口。");
            }
            else if (_fpsError is not null && _fpsError.Contains("PresentMon", StringComparison.Ordinal))
            {
                sb.AppendLine("本工具目录下缺 tools\\PresentMon-x64.exe，帧数采集需要它。");
            }
            else
            {
                sb.AppendLine("记录期间游戏没有运行，或采集未能挂上。请在游戏运行后重新记录。");
            }
        }
        else
        {
            double fMedShow = Median(_fpsAll);
            sb.AppendLine("帧率中位 : " + fMedShow.ToString("0.0") + " fps");
            sb.AppendLine("帧率区间 : " + Quantile(_fpsAll, 0.05).ToString("0.0") + " – "
                + Quantile(_fpsAll, 0.95).ToString("0.0") + " fps（5%–95%）");
            sb.AppendLine("1% Low   : " + (_lowAll.Count > 0
                ? "中位 " + Median(_lowAll).ToString("0.0") + " fps，最差 5% "
                  + Quantile(_lowAll, 0.05).ToString("0.0") + " fps"
                : "样本不足 100 帧，未计算"));
            sb.AppendLine("有效行数 : " + _fpsAll.Count + " / " + _samples);
            sb.AppendLine("注       : 全部用分位数，不用最高/最低值。"
                + "加载和过场画面也会被计入帧，用极值会被一次过场毁掉整场结论。");
        }

        sb.AppendLine();

        // 拿不到帧数时，下面这些判定全都没有依据，一句也不该说。
        if (_fpsAll.Count == 0)
        {
            sb.AppendLine("提醒：游戏自身的显存无法从外部释放。外部工具能做的只有两件事 ——");
            sb.AppendLine("关闭无关后台程序，以及在显存将满时提醒你退回大厅刷新。");
            return sb.ToString();
        }

        // 早先这里是一段固定文案，不管这一轮测出什么都把八种可能性全列一遍，
        // 读者得自己猜哪条适用于自己。现在改成先算、再只说自己算出来的那些。
        string Verdict(string name, bool? hit, string evidence)
        {
            string mark = hit switch
            {
                true => "[成立]",
                false => "[不成立]",
                null => "[存疑]",
            };
            return mark + " " + name + " —— " + evidence;
        }

        double fMed = Median(_fpsAll);
        double lMed = Median(_lowAll);
        var rClock = Corr(_fpsAll, _clockAll);

        bool thermal = _tempMax >= 85;
        bool vramOut = _peakPct >= 95
            || (_samples > 0 && _critRows * 100.0 / _samples >= 5);

        // 分两步看，一步看落差、一步看趋势：
        // 只有落差（前段和后段差得多，但中段之后就平了）是一次性台阶，
        // 比如「第一局特别流畅，之后一直持平」；
        // 既有落差、后半程还在继续走，才是真的持续劣化。
        // 少了第二步，用户那份日志会被判成「显存持续累积：成立」——
        // 可它每局回大厅都会释放，压根不是泄漏。
        var (p1, p2, p3) = Thirds(_pctAll);
        var (f1, f2, f3) = Thirds(_fpsAll);
        bool vramStep = p3 - p1 >= 15;
        bool vramClimb = vramStep && p3 - p2 >= (p2 - p1) * 0.8;
        bool fpsStep = f1 > 0 && (f1 - f3) / f1 >= 0.10;
        bool decay = fpsStep && f2 - f3 >= (f1 - f2) * 0.8;
        bool stutter = _lowAll.Count > 0 && lMed > 0 && fMed > 0 && lMed < fMed * 0.7;

        bool? gpuBound = null;
        if (rClock.HasValue)
        {
            gpuBound = rClock.Value >= 0.30 ? true
                     : rClock.Value <= -0.30 ? false
                     : null;
        }

        sb.AppendLine("自动判定（每条都附上这一轮的实测依据，可以自己复核）");
        sb.AppendLine(new string('-', 60));
        sb.AppendLine(Verdict("热降频", thermal,
            "最高温度 " + _tempMax.ToString("0") + "°C（门槛 85°C）"));
        sb.AppendLine(Verdict("显存耗尽", vramOut,
            "峰值 " + _peakPct.ToString("0.0") + "%，超过危急线 " + _critRows
            + " 行 / 共 " + _samples + " 行"));
        sb.AppendLine(Verdict(NameVramClimb, vramClimb,
            "前/中/后三段显存中位 " + p1.ToString("0") + "% → " + p2.ToString("0") + "% → "
            + p3.ToString("0") + "%（末段要比首段高 15 个百分点才算累积）"));
        sb.AppendLine(Verdict(NameDecay, decay,
            "前/中/后三段帧率中位 " + f1.ToString("0") + " → " + f2.ToString("0") + " → "
            + f3.ToString("0") + " fps（末段要比首段低 10% 才算）"));
        // 这一条看的是整场，局内那一层在下面「对局拆分」里单独看。
        // 两个尺度会给出不同答案（台阶 vs 下滑），混在一句话里说必然说不清。
        // 判「不成立」不等于「没问题」。落差是真实存在的，得说清楚是哪种，
        // 否则用户看到「越玩越卡：不成立」只会觉得工具在糊弄他。
        if (!vramClimb && vramStep)
        {
            sb.AppendLine("   注：前后差 " + (p3 - p1).ToString("0")
                + " 个百分点，但最后三分之一已基本持平 —— 是一次性抬升，不像泄漏。");
        }
        if (!decay && fpsStep)
        {
            sb.AppendLine("   注：前后差 " + ((f1 - f3) / f1 * 100).ToString("0")
                + "%，但后半程基本持平 —— 更像开局那一段特别流畅，而不是一路下滑。");
        }
        sb.AppendLine(Verdict("偶发长帧", stutter,
            _lowAll.Count > 0
                ? "1% Low 中位 " + lMed.ToString("0.0") + " 只有帧率中位 " + fMed.ToString("0.0")
                  + " 的 " + (lMed / fMed * 100).ToString("0") + "%"
                : "1% Low 样本不足"));
        sb.AppendLine(Verdict("瓶颈在显卡", gpuBound,
            "帧率与 GPU 频率 r=" + R(rClock)
            + "（≥+0.30 才算显卡定帧率；负相关 = 帧低时显卡反而升频，说明它在等别的东西）"));

        // ---- 对局拆分 ----
        // 主诉是「越玩越卡」，但一场记录里混着若干局和若干段回大厅。
        // 不对局切分，看到的只是局与局之间的台阶，回答不了主诉。
        sb.AppendLine();
        var rep = MatchAnalyzer.Analyze(_rows);
        if (rep.Enough)
        {
            MatchAnalyzer.Append(sb, rep);
        }
        else
        {
            sb.AppendLine("对局拆分");
            sb.AppendLine(new string('-', 60));
            sb.AppendLine("这一轮只切出 " + rep.Matches.Count + " 局。");
            sb.AppendLine("至少要两局才谈得上「局与局之间」；一局只能看局内趋势。");
            if (rep.Matches.Count == 1)
            {
                var m = rep.Matches[0];
                sb.AppendLine("这一局：" + (int)(m.DurationSec / 60) + " 分，"
                    + m.Samples + " 个采样点，帧率中位 " + m.FpsMedian.ToString("0.0")
                    + "，1% Low 中位 " + m.LowMedian.ToString("0.0") + "。");
            }
        }

        sb.AppendLine();
        sb.AppendLine("针对这一轮");
        sb.AppendLine(new string('-', 60));
        if (stutter)
        {
            sb.AppendLine(" - 卡顿来自偶发长帧，不是显存不够：把游戏帧率上限设到 "
                + fMed.ToString("0") + " fps 附近，能少一些大起大落。");
            sb.AppendLine(" - 确认游戏装在固态硬盘上；读盘慢会直接放大这类长帧。");
            sb.AppendLine(" - 别清 NVIDIA DXCache：那是着色器缓存，清掉会强制重新编译，开局更卡。");
        }
        if (vramOut || _peakPct >= 90)
        {
            sb.AppendLine(" - 显存峰值已到 " + _peakPct.ToString("0.0")
                + "%，降一档贴图质量或渲染比例才是真能腾出空间的做法。");
        }
        if (thermal)
        {
            sb.AppendLine(" - 温度到过 " + _tempMax.ToString("0") + "°C，先解决散热：清灰、垫高机身。");
        }
        if (fpsStep && !vramClimb)
        {
            sb.AppendLine(" - 帧率前后有落差，但显存没一路涨：退回大厅刷新多半改善不了，别只往显存上使劲。");
        }
        if (!stutter && !vramOut && !thermal && !decay)
        {
            sb.AppendLine(" - 这一轮没查出明确的本机瓶颈。若仍觉得卡，优先怀疑网络或服务器延迟。");
        }
        sb.AppendLine();
        sb.AppendLine("提醒：游戏自身的显存无法从外部释放。外部工具能做的只有两件事 ——");
        sb.AppendLine("关闭无关后台程序，以及在显存将满时提醒你退回大厅刷新。");
        return sb.ToString();
    }

    /// <summary>
    /// 外部目标（非《永劫无间》）口径下的汇总：只报原始观测，不给任何结论。
    ///
    /// 输出什么、不输出什么，理由都写在 <see cref="BuildSummary"/> 的口径分叉处。
    /// 这里额外补一段「为什么没有判定」的说明 —— 用户看到一份没有结论的报告，
    /// 第一反应会是「工具坏了」，必须让他知道这是刻意的。
    /// </summary>
    private void AppendExternalScope(StringBuilder sb)
    {
        sb.AppendLine("观测值（不含判定）");
        sb.AppendLine(new string('-', 60));

        sb.AppendLine("显卡     : 温度中位 " + MedText(_tempAll)
            + "°C，最高 " + _tempMax.ToString("0") + "°C；占用中位 " + MedText(_utilAll)
            + "%；频率中位 " + MedText(_clockAll) + " MHz");
        sb.AppendLine("显存     : 中位 " + MedText(_memAll)
            + " MB，峰值 " + _peakMb.ToString("0") + " MB（" + _peakPct.ToString("0.0") + "%）");
        sb.AppendLine("内存     : 目标进程内存中位 " + MedText(_gameRamAll) + " MB");
        sb.AppendLine();

        if (_fpsAll.Count > 0)
        {
            sb.AppendLine("帧率中位 : " + Median(_fpsAll).ToString("0.0") + " fps");
            sb.AppendLine("帧率区间 : " + Quantile(_fpsAll, 0.05).ToString("0.0") + " – "
                + Quantile(_fpsAll, 0.95).ToString("0.0") + " fps（5%–95%）");
            sb.AppendLine("1% Low   : " + (_lowAll.Count > 0
                ? "中位 " + Median(_lowAll).ToString("0.0") + " fps，最差 5% "
                  + Quantile(_lowAll, 0.05).ToString("0.0") + " fps"
                : "样本不足 100 帧，未计算"));
            sb.AppendLine("有效行数 : " + _fpsAll.Count + " / " + _samples);
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("帧数     : 不可用 —— " + (_fpsError ?? "本次未采集到"));
            sb.AppendLine();
        }

        sb.AppendLine("为什么这一份没有「自动判定」和「建议」");
        sb.AppendLine(new string('-', 60));
        sb.AppendLine("本工具的判定阈值（显存涨 15 个百分点、帧率降 10%、");
        sb.AppendLine("切局 1200 MB、GPU 占用 60% 才算在打、建议帧率上限）");
        sb.AppendLine("全部是在《永劫无间》这一份 99 分钟实战日志上标定出来的。");
        sb.AppendLine("换一个游戏，同样的数字就是假结论 —— 比如拿这台机器上");
        sb.AppendLine("《永劫无间》的帧率中位，去建议另一个游戏也设同样的上限，");
        sb.AppendLine("是没有意义的。所以这里只报上面那些纯观测值，不下结论。");
        sb.AppendLine();
        sb.AppendLine("上面的数字本身可以直接用：帧率中位看整体流畅度，");
        sb.AppendLine("1% Low 看卡顿感（它明显低于中位就说明有偶发长帧），");
        sb.AppendLine("温度和占用中位看这一轮有没有撞到瓶颈。");
        sb.AppendLine();
        sb.AppendLine("注：这里同样不列「卡顿占比」。同一份数据换一种筛选口径，");
        sb.AppendLine("    它能从 11% 跳到 25% —— 不稳的数字不配当结论。");
        sb.AppendLine();
        sb.AppendLine("提醒：帧数采集是只读监听显卡的呈现事件，不进目标进程、");
        sb.AppendLine("不注入、不改内存，因此不触碰反作弊。但不同游戏的反作弊");
        sb.AppendLine("策略不同，若目标游戏因此弹出提示，请改用无帧数的记录。");
    }

    /// <summary>一串观测值的中位数文本；没有样本时写「样本不足」而不是 0。</summary>
    private static string MedText(List<double> v) =>
        v.Count > 0 ? Median(v).ToString("0.0") : "样本不足";

    /// <summary>把汇总写到记录文件旁边的 txt。</summary>
    public string? WriteSummary()
    {
        if (CsvPath is null)
        {
            return null;
        }

        try
        {
            string path = Path.ChangeExtension(CsvPath, ".txt");
            File.WriteAllText(path, BuildSummary(), new UTF8Encoding(true));
            return path;
        }
        catch
        {
            return null;
        }
    }
}
