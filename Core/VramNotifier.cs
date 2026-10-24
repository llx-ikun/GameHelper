namespace GameHelper.Core;

/// <summary>
/// 显存将满的提醒策略 —— 与「是否在记录 CSV」彻底解耦。
///
/// 为什么不写在 MonitorSession 里：提醒对应的是「玩家此刻需要知道」，
/// 记录对应的是「要不要留档」，两件事没有必然关系。之前的实现把提醒塞在
/// Record() 里，而 Record() 开头就 `if (_writer is null) return;` ——
/// 结果是「不开记录就完全不会提醒」，偏偏「随手挂着工具打游戏」才是最需要
/// 提醒的场景。这是本次要修的核心问题。
///
/// 三重防轰炸（缺一条都会变成骚扰）：
///   1. 只在「升级」时提醒：正常→接近→告急，降回安全区才会重新武装；
///   2. 迟滞：要跌破「阈值 − 余量」才重新武装，避免在阈值线上反复横跳；
///   3. 冷却：同一档位的最短提醒间隔，作为前两条之外的最后一道闸。
/// </summary>
public sealed class VramNotifier
{
    /// <summary>总开关。关掉之后完全静默，但仍会记录状态。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>警告阈值（显存占总量的百分比）。</summary>
    public double WarnPct { get; set; } = 82;

    /// <summary>危急阈值。</summary>
    public double CritPct { get; set; } = 90;

    /// <summary>迟滞余量：跌破「阈值 − 余量」才重新武装。</summary>
    public double ReleaseMargin { get; set; } = 3;

    /// <summary>同一档位的最短提醒间隔。</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>越线提醒。参数：(标题, 正文, 是否危急)。</summary>
    public event Action<string, string, bool>? Alert;

    private bool _armedWarn = true;
    private bool _armedCrit = true;
    private DateTime _lastWarn = DateTime.MinValue;
    private DateTime _lastCrit = DateTime.MinValue;

    public int WarnCount { get; private set; }

    public int CritCount { get; private set; }

    public DateTime? LastFiredAt { get; private set; }

    /// <summary>当前档位。仅用于显示，不参与是否提醒的判定。</summary>
    public VramState Level { get; private set; } = VramState.Ok;

    /// <summary>阈值或开关变化后调用，让下一次评估重新开始。</summary>
    public void Reset()
    {
        _armedWarn = true;
        _armedCrit = true;
        _lastWarn = DateTime.MinValue;
        _lastCrit = DateTime.MinValue;
        Level = VramState.Ok;
    }

    /// <summary>喂一个快照。每个采样周期都应调用，与是否在记录无关。</summary>
    public void Update(Snapshot s)
    {
        if (!s.Gpu.HasData)
        {
            return;
        }

        Update(s.Gpu.Pct, s.Gpu.MemUsedMb, s.Gpu.MemTotalMb);
    }

    /// <summary>
    /// 核心判定。单独暴露数值版本，自检才能直接喂边界值，
    /// 不必去构造完整的 Snapshot。
    /// </summary>
    public void Update(double pct, double usedMb, double totalMb)
    {
        Level = pct >= CritPct ? VramState.Crit
              : pct >= WarnPct ? VramState.Warn
              : VramState.Ok;

        if (!Enabled)
        {
            return;
        }

        // 迟滞：先按「跌回安全区」重新武装，再判断是否要提醒。
        if (pct <= WarnPct - ReleaseMargin)
        {
            _armedWarn = true;
        }
        if (pct <= CritPct - ReleaseMargin)
        {
            _armedCrit = true;
        }

        var now = DateTime.Now;

        if (pct >= CritPct)
        {
            if (_armedCrit && now - _lastCrit >= Cooldown)
            {
                _armedCrit = false;
                // 已经告急了，没必要再补一条「正在接近上限」。
                _armedWarn = false;
                _lastCrit = now;
                CritCount++;
                LastFiredAt = now;
                Alert?.Invoke(
                    "显存告急",
                    $"显存已到 {pct:0.0}%（{usedMb:0}/{totalMb:0} MB）。\n" +
                    "打完这一场，退回大厅刷新一下即可释放显存。\n" +
                    "这是唯一安全归还显存的方式。",
                    true);
            }
            return;
        }

        if (pct >= WarnPct && _armedWarn && now - _lastWarn >= Cooldown)
        {
            _armedWarn = false;
            _lastWarn = now;
            WarnCount++;
            LastFiredAt = now;
            Alert?.Invoke(
                "显存接近上限",
                $"显存已到 {pct:0.0}%（{usedMb:0}/{totalMb:0} MB），"
                + $"正在逼近 {totalMb:0} MB 上限。\n"
                + "再打一两局就该回大厅刷新了。",
                false);
        }
    }
}
