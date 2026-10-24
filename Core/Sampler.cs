using System.Diagnostics;

namespace GameHelper.Core;

/// <summary>
/// 后台采样器。
///
/// 设计要点：采样全部发生在一条后台线程上，界面只接收不可变的
/// <see cref="Snapshot"/> 并重绘。这样即使 nvidia-smi 偶发卡住
/// 一两百毫秒，窗口也不会跟着僵一下 —— 这是脚本版做不到的地方
/// （PowerShell 版是同步取值，每次刷新都会短暂冻结界面）。
/// </summary>
public sealed class Sampler : IDisposable
{
    private readonly GpuMemoryProbe _probe = new();
    private readonly FrameTracker _frames = new();
    private readonly Dictionary<int, string> _nameCache = new();
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _tick;
    private string _throttle = "none";
    private DateTime? _gameStartedAt;
    private int _startedAtPid;
    private bool _disposed;
    private IReadOnlyList<GpuAdapter> _adapters = Array.Empty<GpuAdapter>();
    private GpuAdapter? _primary;
    private bool _adapterWarned;

    /// <summary>
    /// 帧数已经放弃过一次（通常是权限不足）。
    /// 不记这个的话，每 1.5 秒都会重新启动一个注定失败的 PresentMon，
    /// 白占资源还刷日志。游戏下一次启动时才重新试一次。
    /// </summary>
    private bool _framesGivenUp;
    private bool _framesWarned;
    private int _framesAttachedPid;

    /// <summary>采样间隔（毫秒）。</summary>
    public int IntervalMs { get; set; } = 1500;

    /// <summary>
    /// 是否采集真实帧数。需要管理员（或 Performance Log Users 组），
    /// 拿不到时自动降级——采集失败不影响其它任何指标。
    /// </summary>
    public bool TrackFrames { get; set; } = true;

    /// <summary>是否顺带查询降频原因。开启后约每 30 秒一次 <c>nvidia-smi -q -d PERFORMANCE</c>。</summary>
    public bool TrackThrottle { get; set; }

    /// <summary>
    /// 外部监控目标；<c>null</c> 表示按《永劫无间》自动探测。
    ///
    /// 设成非 null 时，帧数、进程内存这些「目标相关」的量全部改盯这个进程，
    /// 而汇总里的判定与建议会整体闭嘴 —— 那些阈值是在《永劫无间》上标定的，
    /// 套到别的游戏上是假结论。详见 <see cref="MonitorSession"/>。
    ///
    /// 这个属性会被采样线程读、被界面线程写，所以用自动属性 + 引用赋值：
    /// record 是不可变的，读到的要么是旧值要么是新值，不会读到半个。
    /// </summary>
    public MonitorTarget? ExternalTarget { get; set; }

    /// <summary>当前实际盯着的目标；没有目标时为 null。</summary>
    public MonitorTarget? CurrentTarget { get; private set; }

    public Snapshot Latest { get; private set; } = Snapshot.Empty;

    /// <summary>每完成一次采样触发。回调在后台线程上，界面需自行切换线程。</summary>
    public event Action<Snapshot>? SnapshotReady;

    /// <summary>采样过程中出现的问题（一条一条，供日志用）。</summary>
    public event Action<string>? Problem;

    /// <summary>探针是否可用。</summary>
    public bool ProbeAvailable => _probe.Available;

    public string? ProbeError => _probe.LastError;

    public bool GpuReadable => NvidiaSmi.Path is not null;

    public string? SmiPath => NvidiaSmi.Path;

    /// <summary>采样间隔折算出的降频查询周期：每 20 次采样查一次。</summary>
    private const int ThrottleEveryTicks = 20;

    public void Start()
    {
        if (_loop is not null || _disposed)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Factory.StartNew(
            () => Loop(token),
            token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch
        {
            // 忽略
        }

        var loop = _loop;
        _loop = null;
        if (loop is not null)
        {
            try
            {
                loop.Wait(2000);
            }
            catch
            {
                // 取消导致的异常，正常
            }
        }

        cts.Dispose();
    }

    private void Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                var snapshot = Sample();
                Latest = snapshot;
                SnapshotReady?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                Problem?.Invoke("采样失败：" + ex.Message);
            }

            int wait = IntervalMs - (int)sw.ElapsedMilliseconds;
            if (wait < 100)
            {
                // 保证不让采样把 CPU 吃满
                wait = 100;
            }

            if (token.WaitHandle.WaitOne(wait))
            {
                return;
            }
        }
    }

    private Snapshot Sample()
    {
        _tick++;

        // 进程名缓存与适配器清单每 40 次采样（约 1 分钟）刷一次。
        // PID 会被复用，缓存太久会把新进程叫成旧名字；
        // 适配器 LUID 在驱动重载后会变，也必须定期重读。
        if (_tick % 40 == 1)
        {
            _nameCache.Clear();
            _adapters = GpuAdapters.Enumerate();
            _primary = GpuAdapters.FindPrimary(_adapters);
        }

        var gpu = NvidiaSmi.Query();
        var raw = _probe.ReadRaw();

        var procs = new List<ProcVram>(raw.Count);

        // 先按 (进程, 显卡) 各成一行，同时数一数有多少条落在主显卡上。
        var pending = new List<(int Pid, string Name, GpuMemInstance Inst, GpuAdapter? Adapter, bool OnPrimary)>(raw.Count);
        int primaryHits = 0;

        foreach (var inst in raw)
        {
            string name = ResolveName(inst.Pid);
            var adapter = FindAdapter(inst.AdapterKey);
            bool onPrimary = _primary is null || adapter is null || adapter.Key.Equals(_primary.Key, StringComparison.OrdinalIgnoreCase);
            if (onPrimary)
            {
                primaryHits++;
            }
            pending.Add((inst.Pid, name, inst, adapter, onPrimary));
        }

        // 一条都没匹配上主显卡，说明适配器清单过期了（例如驱动刚重载过）。
        // 这时按「全部算主显卡」处理，宁可暂时不区分，也不要让界面忽然全空。
        if (_primary is not null && primaryHits == 0 && raw.Count > 0)
        {
            pending = pending.Select(x => (x.Pid, x.Name, x.Inst, x.Adapter, true)).ToList();
            if (!_adapterWarned)
            {
                _adapterWarned = true;
                Problem?.Invoke("适配器标识与性能计数器暂时对不上，本次按全部显卡合计统计；约一分钟后会自动重读。");
            }
        }
        else
        {
            _adapterWarned = false;
        }

        foreach (var x in pending)
        {
            // 显卡驱动会在计数器里给一堆进程建立入口，但实际占用是 0。
            // 把它们列出来只会淹没真正占用显存的那几个，直接滤掉。
            if (x.Inst.LocalMb + x.Inst.SharedMb < 0.1)
            {
                continue;
            }

            procs.Add(new ProcVram(
                x.Pid,
                x.Name,
                Math.Round(x.Inst.LocalMb, 1),
                Math.Round(x.Inst.SharedMb, 1),
                ProcessClassifier.Classify(x.Name),
                x.Inst.AdapterKey,
                x.Adapter?.ShortName ?? x.Inst.AdapterKey,
                x.OnPrimary));
        }

        procs.Sort(static (a, b) =>
        {
            // 主显卡的排前面，同组内按占用降序。
            if (a.OnPrimaryAdapter != b.OnPrimaryAdapter)
            {
                return a.OnPrimaryAdapter ? -1 : 1;
            }
            return b.LocalMb.CompareTo(a.LocalMb);
        });

        var (ramUsed, ramTotal) = Native.GetPhysicalMemory();

        double gameRam = 0;
        bool gameRunning = false;
        DateTime? startedAt = null;
        int gamePid = 0;

        // 目标解析：外部目标优先，否则自动找《永劫无间》。
        // 两者共用一个「进程」对象往下去，所以显存、温度这些与目标无关的
        // 指标完全不受切换影响 —— 只有帧数和进程内存跟着换。
        var wanted = ExternalTarget;
        Process? target = null;

        if (wanted is not null)
        {
            target = wanted.TryOpen();
            if (target is null)
            {
                // 目标进程已经退出。这不是错误：用户关了游戏很正常。
                // 让上层显示「目标已退出」即可，不要报警。
                Problem?.Invoke("监控目标 " + wanted.ProcessName + " 已退出。");
                wanted = null;
            }
        }

        if (wanted is null)
        {
            target = Inventory.FindGame();
        }

        var resolved = wanted is not null
            ? wanted
            : target is not null
                ? new MonitorTarget(TargetKind.Naraka, SafeId(target), ProcessClassifier.GameProcessName, null)
                : null;
        CurrentTarget = resolved;

        if (target is not null)
        {
            gameRunning = true;
            try
            {
                gameRam = Math.Round(target.WorkingSet64 / (1024.0 * 1024.0), 0);
            }
            catch
            {
                gameRam = 0;
            }

            try
            {
                gamePid = target.Id;
            }
            catch
            {
                gamePid = 0;
            }

            // 启动时间按 PID 缓存：换了一局（或换了个外部目标）要重新取，
            // 否则「本次记录」的开始时间会一直停在最初那一局。
            if (gamePid != _startedAtPid)
            {
                _gameStartedAt = null;
                _startedAtPid = gamePid;
            }

            startedAt = _gameStartedAt ??= Inventory.GameStartTime(target);
        }
        else
        {
            _gameStartedAt = null;
            _startedAtPid = 0;
        }

        target?.Dispose();

        if (TrackThrottle && _tick % ThrottleEveryTicks == 1)
        {
            _throttle = NvidiaSmi.QueryThrottle();
        }
        else if (!TrackThrottle)
        {
            _throttle = "none";
        }

        FrameStats? frame = SampleFrames(gameRunning && gamePid > 0 ? gamePid : null);

        return new Snapshot(
            DateTime.Now,
            gpu,
            procs,
            ramUsed,
            ramTotal,
            _throttle,
            gameRunning,
            startedAt,
            gameRam,
            _primary?.Name ?? string.Empty,
            _adapters,
            frame,
            resolved?.Kind ?? TargetKind.Naraka,
            resolved?.ProcessName ?? string.Empty);
    }

    /// <summary>
    /// 安全地取进程 ID。<c>Process.Id</c> 在进程已退出时会抛异常，
    /// 而这里拿到的进程随时可能刚好退出 —— 采样线程不能因为一次竞态就崩。
    /// </summary>
    private static int SafeId(Process p)
    {
        try
        {
            return p.Id;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 取一次帧数。
    ///
    /// 三条纪律：
    ///   1. 拿不到就降级——帧数是**锦上添花**的指标，它失败绝不能牵连显存、
    ///      温度这些主指标，所以这里从不抛异常出去。
    ///   2. 失败一次就记住（<c>_framesGivenUp</c>）——否则采样循环每 1.5 秒
    ///      都会重启一个注定失败的 PresentMon，白占资源还刷日志。
    ///      这个标记在**游戏退出时清掉**，下一局再试一次：权限问题是可能
    ///      被用户中途解决掉的（比如他右键用管理员重开了本工具）。
    ///   3. 原因只报一次（<c>_framesWarned</c>）——同一句话每 1.5 秒进一次
    ///      日志，会把真正的问题淹掉。
    /// </summary>
    private FrameStats? SampleFrames(int? gamePid)
    {
        if (!TrackFrames)
        {
            return null;                      // 界面上显示默认的「未启用」
        }

        if (gamePid is null)
        {
            // 游戏没在跑：停掉采集，并清掉放弃标记，让下一局重新试。
            _frames.Stop();
            _framesAttachedPid = 0;
            _framesGivenUp = false;
            _framesWarned = false;
            return null;
        }

        if (_framesGivenUp)
        {
            return FrameStats.Fail(_frames.Error ?? "帧数采集不可用，本局不再重试");
        }

        // 换了一局（PID 变了）或上次已退出 —— 重新挂一次。
        if (!_frames.Running || _framesAttachedPid != gamePid.Value)
        {
            bool ok = _frames.Start(gamePid.Value);
            _framesAttachedPid = gamePid.Value;
            if (!ok)
            {
                GiveUpFrames();
                return FrameStats.Fail(_frames.Error ?? "启动 PresentMon 失败");
            }
        }

        var st = _frames.Read();

        // 注意：Available=true 但 Fps=0 是「已启动、还没收到帧」的正常中间态
        // （刚挂上，或游戏还在加载界面）。只有 Available=false 才是真失败。
        if (!st.Available)
        {
            GiveUpFrames();
            return st;
        }

        return st;
    }

    private void GiveUpFrames()
    {
        _framesGivenUp = true;
        _frames.Stop();
        if (_framesWarned)
        {
            return;
        }

        _framesWarned = true;
        string why = _frames.Error ?? "未知原因";
        Problem?.Invoke("无法采集帧数（" + why + "）。显存、温度等其它指标不受影响；"
            + "若想看帧数，请以管理员身份重新运行本工具，本局不再重试。");
    }

    private GpuAdapter? FindAdapter(string key)
    {
        foreach (var a in _adapters)
        {
            if (a.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return a;
            }
        }
        return null;
    }

    private string ResolveName(int pid)
    {
        lock (_gate)
        {
            if (_nameCache.TryGetValue(pid, out var cached))
            {
                return cached;
            }
        }

        string name;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "pid_" + pid;
            }
        }
        catch
        {
            // 进程已退出，或权限不足（例如以更高完整性级别运行的反作弊）。
            name = "pid_" + pid;
        }

        lock (_gate)
        {
            _nameCache[pid] = name;
        }

        return name;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Stop();
        _frames.Dispose();
        _probe.Dispose();
    }
}
