using System.Diagnostics;
using System.Globalization;

namespace GameHelper.Core;

/// <summary>一次帧数读取的结果。</summary>
public sealed record FrameStats(
    /// <summary>是否真的有帧数数据（PresentMon 可用 + 已在采集）。</summary>
    bool Available,
    /// <summary>当前帧率（最近若干帧的平均间隔换算）。</summary>
    double Fps,
    /// <summary>1% Low：最慢那 1% 帧的平均帧率。比平均帧率更能反映卡顿感。</summary>
    double Fps1Low,
    /// <summary>统计窗口内的帧数。</summary>
    int Frames,
    /// <summary>不可用时说明原因。可用时为 null。</summary>
    string? Error)
{
    public static readonly FrameStats Unavailable =
        new(false, 0, 0, 0, "未启用");

    public static FrameStats Fail(string reason) => new(false, 0, 0, 0, reason);

    public double FrameTimeMs => Fps > 0 ? 1000.0 / Fps : 0;
}

/// <summary>
/// 真实帧数采集。
///
/// 为什么必须借 PresentMon 而不是自己数一数：
///   帧数只有一个干净来源——显卡驱动把画面「呈现（Present）」出去时发出的
///   ETW 事件。要拿到它，要么注入游戏去 hook Present（反作弊红线，绝不做），
///   要么读内核 ETW。内核 ETW 会话的 API 是一组很重的非托管结构
///   （EVENT_TRACE_LOGFILEW 里嵌了 union 和 TIME_ZONE_INFORMATION），
///   手写错一个偏移就是访问违规——而 .NET Core 里这个异常捕获不到，
///   进程会直接崩在用户打游戏的时候。
///   PresentMon 是微软/Intel 官方开源工具，把这些坑都踩过了，而且
///   CapFrameX、OCAT、FrameView、PIX 都建立在它之上。
///
/// 它做的事是**只读**的：监听操作系统发出的呈现事件，不进游戏进程、
/// 不改内存、不注入任何 DLL —— 因此不触碰反作弊。
///
/// 唯一的门槛：启动内核 ETW 会话需要管理员，或者用户在
/// "Performance Log Users" 组里。不满足时本类静默降级，绝不影响主功能。
/// </summary>
public sealed class FrameTracker : IDisposable
{
    /// <summary>统计窗口保留的帧数。900 帧约等于 60fps 下 15 秒。</summary>
    private const int WindowFrames = 900;

    /// <summary>算「当前帧率」用的较短窗口，约 1.5 秒（60fps 下 90 帧）。</summary>
    private const int RecentFrames = 90;

    private readonly List<double> _frameTimesMs = new();
    private readonly object _gate = new();
    private readonly List<string> _stderr = new();

    private Process? _proc;
    private Task? _pump;
    private int _msIndex = -1;
    private bool _headerParsed;
    private bool _disposed;
    private string? _error;
    private int _totalFrames;

    /// <summary>PresentMon 的完整路径；null 表示没找到。</summary>
    public string? PresentMonPath { get; private set; }

    /// <summary>是否找到了 PresentMon。</summary>
    public bool Installed => PresentMonPath is not null;

    /// <summary>是否正在采集。</summary>
    public bool Running { get; private set; }

    /// <summary>不可用的原因。可用时为 null。</summary>
    public string? Error => _error;

    /// <summary>自启动以来累计收到的帧数。</summary>
    public int TotalFrames => _totalFrames;

    // ------------------------------------------------------------------ 定位

    /// <summary>
    /// 按优先级找 PresentMon。
    ///
    /// 顺序：用户自定义路径 → 程序目录 tools\ → 程序目录 → PATH。
    /// 之所以留自定义路径这一档，是因为有人愿意把工具放在别处，
    /// 而硬编码单一位置会让这部分人怎么用都不生效。
    /// </summary>
    public static string? FindPresentMon(string? customPath = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            candidates.Add(customPath);
        }

        string root = AppPaths.RootDir;
        candidates.Add(Path.Combine(root, "tools", "PresentMon-x64.exe"));
        candidates.Add(Path.Combine(root, "PresentMon-x64.exe"));

        string? inPath = FindOnPath("PresentMon-x64.exe");
        if (inPath is not null)
        {
            candidates.Add(inPath);
        }

        // 最后一档：从本程序里嵌的那一份解出来。
        // 放在最后是为了让「磁盘上已有的」优先 —— 用户想换新版只需替换文件。
        string? extracted = ExtractEmbedded();
        if (extracted is not null)
        {
            candidates.Add(extracted);
        }

        foreach (string c in candidates)
        {
            try
            {
                if (File.Exists(c))
                {
                    return c;
                }
            }
            catch
            {
                // 路径非法（例如含非法字符），换下一个
            }
        }

        return null;
    }

    /// <summary>内嵌资源的名字，与 csproj 里的 LogicalName 必须一致。</summary>
    private const string EmbeddedName = "PresentMon-x64.exe";

    /// <summary>
    /// 把内嵌的 PresentMon 解到本地，返回可用路径；拿不到就返回 null。
    ///
    /// 解到 %LOCALAPPDATA% 而不是 exe 旁边：程序可能被放在 Program Files，
    /// 那里普通权限写不进去；而 LOCALAPPDATA 对本用户永远可写。
    /// </summary>
    private static string? ExtractEmbedded()
    {
        try
        {
            using var src = typeof(FrameTracker).Assembly
                .GetManifestResourceStream(EmbeddedName);
            if (src is null || src.Length == 0)
            {
                // 发行包里漏嵌了这份资源 —— 用 null 让上层照常降级，不要抛。
                return null;
            }

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "游戏助手", "tools");
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, EmbeddedName);

            // 已存在且大小一致就直接复用，避免每次启动都重写一遍文件
            // （那会在开机自启动的场景下无谓地触发杀软扫描）。
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length != src.Length)
            {
                using var dst = File.Create(path);
                src.CopyTo(dst);
            }

            return path;
        }
        catch
        {
            // 写不出来（权限、被杀软拦截等）就当没有这个能力。
            // 帧数本来就是可选指标，缺了它其它功能照常。
            return null;
        }
    }

    private static string? FindOnPath(string fileName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }

        foreach (string dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            try
            {
                string full = Path.Combine(dir.Trim().Trim('"'), fileName);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch
            {
                // 忽略单个无效条目
            }
        }

        return null;
    }

    /// <summary>用给定的自定义路径重新定位（设置里改了路径时调用）。</summary>
    public void Refresh(string? customPath = null)
    {
        PresentMonPath = FindPresentMon(customPath);
        if (PresentMonPath is null)
        {
            _error = "未找到 PresentMon-x64.exe";
        }
    }

    // ------------------------------------------------------------------ 启停

    /// <summary>
    /// 开始采集指定进程的帧数。
    ///
    /// 参数含义（PresentMon 1.x 命令行）：
    ///   -process_id            只跟这个进程
    ///   -output_stdout         CSV 直接写标准输出，我们不落中间文件
    ///   -no_top                不输出「活跃交换链」表格，那部分会混进 CSV
    ///   -stop_existing_session 停掉同名残留会话，否则上次没退干净会起不来
    ///   -terminate_on_proc_exit 游戏退出时 PresentMon 自己结束，不留孤儿进程
    /// </summary>
    public bool Start(int pid, string? customPath = null)
    {
        if (_disposed || pid <= 0)
        {
            return false;
        }

        Stop();
        Refresh(customPath);

        if (PresentMonPath is null)
        {
            _error = "未找到 PresentMon-x64.exe（应放在程序目录的 tools 子目录）";
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = PresentMonPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // stderr 是一般单字节文本（英文报错），按 UTF-8 读就够。
            // stdout **不能在这里定编码** —— 见 PumpStdout 的注释：
            // PresentMon 写的是带 BOM 的 UTF-16LE，必须按 BOM 判。
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        psi.ArgumentList.Add("-process_id");
        psi.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-output_stdout");
        psi.ArgumentList.Add("-no_top");
        psi.ArgumentList.Add("-stop_existing_session");
        psi.ArgumentList.Add("-terminate_on_proc_exit");

        try
        {
            _proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _error = "启动 PresentMon 失败：" + ex.Message;
            return false;
        }

        if (_proc is null)
        {
            _error = "启动 PresentMon 失败（未返回进程）";
            return false;
        }

        _stderr.Clear();
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                lock (_gate)
                {
                    // 只留最近几行，权限不足的提示就在这里面。
                    if (_stderr.Count < 20)
                    {
                        _stderr.Add(e.Data);
                    }
                }
            }
        };

        // 解析状态必须在读取线程启动**之前**复位好，否则第一行可能
        // 撞上一个还没重置的 _msIndex，被当成数据行丢掉。
        lock (_gate)
        {
            _frameTimesMs.Clear();
            _totalFrames = 0;
        }

        _headerParsed = false;
        _msIndex = -1;

        var p = _proc;
        _pump = Task.Run(() => PumpStdout(p));
        _proc.BeginErrorReadLine();

        Running = true;
        _error = null;
        return true;
    }

    /// <summary>
    /// 逐行读 PresentMon 的 CSV。
    ///
    /// 为什么不用省事的 <c>BeginOutputReadLine()</c>：
    /// PresentMon 1.10 把 CSV 以 **UTF-16LE** 写到标准输出（流首是 BOM FF FE，
    /// 每个 ASCII 字符后面跟一个 0x00）。若按 UTF-8 解码，读到的每个字符间
    /// 都会夹一个 NUL，于是 <c>StartsWith("Application")</c> 永远不成立、
    /// 一帧都解析不出来 —— 而且**不会报任何错**，只表现为「帧数一直不可用」。
    /// 这属于最难查的一类故障，因此这里不用固定的 <c>StandardOutputEncoding</c>，
    /// 而是交给 StreamReader 按 BOM 自行判断，对将来换编码也不敏感。
    /// </summary>
    private void PumpStdout(Process p)
    {
        try
        {
            using var reader = new StreamReader(
                p.StandardOutput.BaseStream,
                new System.Text.UTF8Encoding(false),
                detectEncodingFromByteOrderMarks: true);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                OnLine(line);
            }
        }
        catch
        {
            // 进程被 Kill、管道关闭 —— 都是正常结束路径，不是错误。
        }
    }

    public void Stop()
    {
        Running = false;

        var p = _proc;
        _proc = null;

        var pump = _pump;
        _pump = null;

        if (p is null)
        {
            return;
        }

        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 已经退出，或权限不足。无论哪种都不该影响主流程。
        }

        try
        {
            // 读线程要等它收尾再 Dispose 进程，否则它可能正卡在
            // ReadLine 上读到已释放的流，抛出的异常虽然吞掉了，
            // 但会在「每局切换」这条热路径上反复发生。
            pump?.Wait(500);
        }
        catch
        {
            // 超时或取消，都无所谓
        }

        try
        {
            p.Dispose();
        }
        catch
        {
            // 忽略
        }
    }

    // ------------------------------------------------------------------ 解析

    /// <summary>
    /// 喂进一行 PresentMon 输出。
    ///
    /// 设为 internal 而不是 private，是为了让 <c>--selftest</c> 能用合成数据
    /// 直接验证解析与过滤逻辑 —— 真跑一次需要管理员开 ETW 会话，
    /// 在自检环境里既没有权限也不该有副作用。
    /// </summary>
    internal void OnLine(string line)
    {
        if (line.Length == 0)
        {
            return;
        }

        // PresentMon 的第一行是 CSV 表头。按列名定位，不按列号 ——
        // 不同版本的列集会变，硬编码列号会在升级后静默算错。
        if (!_headerParsed)
        {
            if (line.StartsWith("Application", StringComparison.OrdinalIgnoreCase))
            {
                string[] cols = line.Split(',');
                for (int i = 0; i < cols.Length; i++)
                {
                    if (string.Equals(cols[i].Trim(), "MsBetweenPresents", StringComparison.OrdinalIgnoreCase))
                    {
                        _msIndex = i;
                        break;
                    }
                }

                _headerParsed = true;
            }

            return;
        }

        if (_msIndex < 0)
        {
            return;
        }

        string[] f = line.Split(',');
        if (_msIndex >= f.Length)
        {
            return;
        }

        if (!double.TryParse(f[_msIndex].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ms))
        {
            return;
        }

        // 帧间隔异常大（例如切到桌面、读盘卡顿）会让统计失真。
        // 超过 2 秒的间隔按「不是一个连续帧」处理，直接丢弃。
        if (ms <= 0 || ms > 2000)
        {
            return;
        }

        lock (_gate)
        {
            _frameTimesMs.Add(ms);
            _totalFrames++;
            if (_frameTimesMs.Count > WindowFrames)
            {
                _frameTimesMs.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// 把 PresentMon 退出时的 stderr 翻译成用户能直接照做的中文原因。
    ///
    /// 抽成方法是为了能被 <c>--selftest</c> 用**实测抓到的原文**做断言 ——
    /// 这段映射决定了用户看到的是一句「以管理员身份运行」还是一句
    /// 无用的「未知原因」，值得被锁住。
    /// </summary>
    internal static string DescribeExit(string stderr)
    {
        // 实测原文（PresentMon 1.10.0，非管理员运行，退出码 6）：
        //   error: failed to start trace session: access denied.
        // 结论是「必须管理员」，不是「可能需要」，所以不含糊其辞。
        if (stderr.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("elevated", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("administrative", StringComparison.OrdinalIgnoreCase))
        {
            return "帧数需要管理员权限：Windows 只允许管理员启动性能跟踪会话。"
                 + "请用「工具 → 以管理员身份重启…」提权后再记录。";
        }

        // 残留的同名会话也是一种常见失败：上一轮没退干净时会起不来。
        // 我们启动时已带 -stop_existing_session，走到这里说明是别的原因。
        return stderr.Length > 0 ? "PresentMon 已退出：" + stderr : "PresentMon 已退出：未知原因";
    }

    /// <summary>读取当前帧数。算法见 <see cref="Compute"/>。</summary>
    public FrameStats Read()
    {
        if (!Running)
        {
            return FrameStats.Fail(_error ?? "未开始采集");
        }

        // 进程已退出（多半是权限不足）→ 把 PresentMon 自己的报错转成中文原因。
        if (_proc is { HasExited: true })
        {
            // 这里有个必须处理的竞态：stderr 是异步读的，进程秒退时回调常常
            // 还没送达。立刻去读会读到空，于是报一句「未知原因」——
            // 恰好把「权限不足」这个唯一有用的信息丢掉了。
            // 空的话等一小会儿再看。这只会在「每局放弃一次」时发生，
            // 所以最多阻塞采样线程 300ms，可以接受。
            string why = string.Empty;
            for (int i = 0; i < 6; i++)
            {
                lock (_gate)
                {
                    why = string.Join(" ", _stderr);
                }

                if (why.Length > 0)
                {
                    break;
                }

                Thread.Sleep(50);
            }

            Running = false;
            _error = DescribeExit(why);
            return FrameStats.Fail(_error);
        }

        double[] recent;
        double[] all;
        lock (_gate)
        {
            if (_frameTimesMs.Count == 0)
            {
                // 已启动但还没收到帧：进程刚起，或游戏还没进对局。
                return new FrameStats(true, 0, 0, 0, null);
            }

            int take = Math.Min(RecentFrames, _frameTimesMs.Count);
            recent = new double[take];
            _frameTimesMs.CopyTo(
                _frameTimesMs.Count - take, recent, 0, take);
            all = _frameTimesMs.ToArray();
        }

        var (fps, low) = Compute(recent, all);

        return new FrameStats(true, Math.Round(fps, 1), Math.Round(low, 1), all.Length, null);
    }

    /// <summary>
    /// 由帧间隔算出「当前帧率」与「1% Low」。
    ///
    /// 抽成静态方法是为了让自检能直接验证这段算法 —— 它最容易被写错，
    /// 而且错了不会崩，只会安静地给出一个看起来合理的假数字，
    /// 那种错比崩溃更难发现。
    ///
    /// 1% Low 的算法：把窗口内的帧间隔降序排，取最慢的 1%（至少 1 帧），
    /// 用它们的平均间隔换算帧率。这是业界通用做法，也是「卡顿感」最灵敏的
    /// 指标 —— 平均 60fps 但 1% Low 只有 20 的游戏，玩起来就是一顿一顿的。
    /// </summary>
    internal static (double Fps, double Low) Compute(double[] recent, double[] all)
    {
        double avgRecent = Mean(recent);
        double fps = avgRecent > 0 ? 1000.0 / avgRecent : 0;

        // 1% Low 需要足够样本才有意义：少于 100 帧时给 0，
        // 而不是拿两三帧算一个假的极值去吓人。
        double low = 0;
        if (all.Length >= 100)
        {
            Array.Sort(all);
            Array.Reverse(all);                       // 降序：最慢的在前
            int n = Math.Max(1, all.Length / 100);    // 最慢的 1%
            double avgSlow = Mean(all, n);
            low = avgSlow > 0 ? 1000.0 / avgSlow : 0;
        }

        return (fps, low);
    }

    private static double Mean(double[] data, int? count = null)
    {
        int n = count ?? data.Length;
        if (n <= 0 || data.Length == 0)
        {
            return 0;
        }

        if (n > data.Length)
        {
            n = data.Length;
        }

        double s = 0;
        for (int i = 0; i < n; i++)
        {
            s += data[i];
        }

        return s / n;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
