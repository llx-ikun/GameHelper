using System.Globalization;
using System.Text;

namespace GameHelper.Core;

/// <summary>
/// 一行采样。对局分析需要同时看时间、内存、显存、帧率，
/// 所以存成结构，而不是几个各自 append 的平行数组 —— 平行数组一旦
/// 有一列漏 append，配对就悄悄错位了，而且不报错。
/// </summary>
public readonly record struct SampleRow(
    double At,
    double GameRamMb,
    double MemUsedMb,
    double Pct,
    double TempC,
    double UtilPct,
    double Fps,
    double Low,
    bool HasFps);

/// <summary>一局的统计。</summary>
public sealed record MatchInfo(
    int Index,
    double StartSec,
    double DurationSec,
    int Samples,
    double FpsMedian,
    double LowMedian,
    double VramMedianMb,
    double VramPeakMb,
    double TempMaxC);

/// <summary>
/// 对局分析结果。<see cref="Enough"/> 为 false 时不要在汇总里写任何趋势结论。
/// </summary>
public sealed record MatchReport(
    IReadOnlyList<MatchInfo> Matches,
    double[] QuarterFps,
    double[] QuarterLow,
    int DroppedShort)
{
    /// <summary>只有一局（或一局都没切出来）时，谈「局与局之间」是没有意义的。</summary>
    public bool Enough => Matches.Count >= 2;
}

/// <summary>
/// 把一次记录切成一局一局来看。
///
/// 为什么要有这个：用户的主诉是「越玩越卡」。但一场 99 分钟的记录里
/// 混着 6 局和 6 段回大厅，直接对全场求趋势，看到的是局与局的台阶，
/// 不是局内的下滑 —— 两者对策完全不同（前者回大厅就恢复了，后者才是泄漏）。
///
/// 两个阈值都不是拍的，标定过程与数据见 DESIGN.md §4.9。
/// </summary>
public static class MatchAnalyzer
{
    /// <summary>
    /// 切局阈值：游戏内存一次掉 1200 MB 以上，就认为回大厅或重开了一局。
    ///
    /// 标定依据（3946 行真实日志）：单次降幅在 0–400 MB 有 922 次，
    /// **400–1500 MB 一次都没有**，1500 MB 以上只有 7 次。
    /// 1200 落在这段真空区中间；实测阈值从 800 到 6000 切出的段数完全相同。
    /// </summary>
    public const double DropThresholdMb = 1200;

    /// <summary>
    /// 「真在打」的门槛：GPU 占用 ≥ 60%。
    ///
    /// 标定依据：能确定是过场的那些行（帧率 &gt;250，165 Hz 屏 + 3050 跑不出来）
    /// 其 GPU 占用中位只有 **28%**，而战斗期中位是 81%。门槛取 60 能清掉
    /// 约 70% 的过场行，同时只删掉 6% 的真实战斗帧。
    /// 再严（80）要删掉 44% 的真实帧，不划算。
    ///
    /// 温度门槛**经检验无效**：叠加「温度 ≥64」只多删 17 行，
    /// 却引入一个随显卡型号漂移的魔数 —— 所以不用。
    /// </summary>
    public const double ActiveUtilPct = 60;

    /// <summary>短于这个样本数的段不算一局（可能是误切，或刚开就退了）。</summary>
    public const int MinMatchSamples = 40;

    /// <summary>少于这么多样本时，四分位没有意义。</summary>
    private const int MinSamplesForQuarters = 40;

    public static MatchReport Analyze(IReadOnlyList<SampleRow> rows)
    {
        var matches = new List<MatchInfo>();
        var qf = new List<double>[] { new(), new(), new(), new() };
        var ql = new List<double>[] { new(), new(), new(), new() };

        if (rows.Count < MinMatchSamples)
        {
            return new MatchReport(matches, Flat(qf), Flat(ql), 0);
        }

        var bounds = Split(rows);
        int dropped = 0;

        for (int b = 0; b + 1 < bounds.Count; b++)
        {
            int s = bounds[b];
            int e = bounds[b + 1];

            var pick = new List<int>();
            for (int i = s; i < e; i++)
            {
                if (rows[i].HasFps && rows[i].UtilPct >= ActiveUtilPct)
                {
                    pick.Add(i);
                }
            }

            if (pick.Count < MinMatchSamples)
            {
                dropped++;
                continue;
            }

            double start = rows[pick[0]].At;
            double dur = rows[pick[^1]].At - start;

            matches.Add(new MatchInfo(
                Index: matches.Count + 1,
                StartSec: start,
                DurationSec: dur,
                Samples: pick.Count,
                FpsMedian: Med(pick.Select(i => rows[i].Fps)),
                LowMedian: Med(pick.Where(i => rows[i].Low > 0).Select(i => rows[i].Low)),
                VramMedianMb: Med(pick.Select(i => rows[i].MemUsedMb)),
                VramPeakMb: pick.Max(i => rows[i].MemUsedMb),
                TempMaxC: pick.Max(i => rows[i].TempC)));

            if (pick.Count < MinSamplesForQuarters)
            {
                continue;
            }

            // 局内按时间四等分：如果「越玩越卡」成立，Q1→Q4 应当一路向下。
            int k = pick.Count / 4;
            for (int qn = 0; qn < 4; qn++)
            {
                var part = qn < 3
                    ? pick.GetRange(qn * k, k)
                    : pick.GetRange(3 * k, pick.Count - 3 * k);
                qf[qn].AddRange(part.Select(i => rows[i].Fps));
                ql[qn].AddRange(part.Where(i => rows[i].Low > 0).Select(i => rows[i].Low));
            }
        }

        return new MatchReport(matches, Flat(qf), Flat(ql), dropped);
    }

    /// <summary>按游戏内存骤降切出对局的边界（含首尾）。</summary>
    internal static List<int> Split(IReadOnlyList<SampleRow> rows)
    {
        var b = new List<int> { 0 };
        for (int i = 1; i < rows.Count; i++)
        {
            if (rows[i - 1].GameRamMb - rows[i].GameRamMb >= DropThresholdMb)
            {
                b.Add(i);
            }
        }
        b.Add(rows.Count);
        return b;
    }

    private static double Med(IEnumerable<double> v)
    {
        return MonitorSession.Median(v.ToList());
    }

    private static double[] Flat(List<double>[] parts)
    {
        var r = new double[4];
        for (int i = 0; i < 4; i++)
        {
            r[i] = parts[i].Count > 0 ? MonitorSession.Median(parts[i]) : 0;
        }
        return r;
    }

    // ---------------------------------------------------------------- 渲染

    /// <summary>把对局表与局内四分位写成汇总里的一节。</summary>
    public static void Append(StringBuilder sb, MatchReport rep)
    {
        sb.AppendLine("对局拆分（" + rep.Matches.Count + " 局）");
        sb.AppendLine(new string('-', 60));
        sb.AppendLine("切局依据：游戏内存一次掉 " + DropThresholdMb.ToString("0")
            + " MB 以上 —— 那是回大厅或重开一局。");
        sb.AppendLine("统计口径：只算 GPU 占用 ≥" + ActiveUtilPct.ToString("0")
            + "% 的采样点，也就是真在打的时候；加载和过场不计入。");
        if (rep.DroppedShort > 0)
        {
            sb.AppendLine("另有 " + rep.DroppedShort + " 段太短（不足 "
                + MinMatchSamples + " 个采样点），已跳过。");
        }
        sb.AppendLine();
        sb.AppendLine("  #   起始    时长   样本   帧率  1%Low   显存    峰值   温度");
        foreach (var m in rep.Matches)
        {
            sb.AppendLine(Cell(m.Index, 3)
                + "  " + Clock(m.StartSec)
                + "  " + Dur(m.DurationSec)
                + Cell(m.Samples, 6)
                + Cell(m.FpsMedian, 7, 1)
                + Cell(m.LowMedian, 7, 1)
                + Cell(m.VramMedianMb, 8)
                + Cell(m.VramPeakMb, 8)
                + Cell(m.TempMaxC, 7));
        }

        sb.AppendLine();
        sb.AppendLine("局内四分位（每局按时间四等分，各局同一位置合在一起）");
        sb.AppendLine(new string('-', 60));
        sb.AppendLine("         开局 Q1      Q2      Q3   收尾 Q4");
        sb.AppendLine("帧率  " + Row(rep.QuarterFps, 1));
        sb.AppendLine("1%Low " + Row(rep.QuarterLow, 1));
        sb.AppendLine();
        sb.AppendLine("读法：真是「一局之内越打越卡」的话，这四格应该一路向下。");
        sb.AppendLine("      若 Q1 反而最低，那卡的是开局加载，不是游玩过程。");
        AppendQuarterVerdict(sb, rep);

        // 「卡顿占比」这个量看着直观，但标定显示它对口径极其敏感
        // （同一份数据在几种判据下能从 11% 跳到 25%），
        // 所以这里刻意不报它 —— 换一个判据就翻脸的数字，不能当结论给。
        sb.AppendLine();
        sb.AppendLine("注：这里没有列「卡顿占比」。同一份数据换一种筛选口径，");
        sb.AppendLine("    它能从 11% 跳到 25% —— 不稳的数字不配当结论，");
        sb.AppendLine("    想看卡顿看上面的 1% Low 那一行。");
    }

    /// <summary>
    /// 给四格一句判读。只描述形状，不编原因。
    /// 与全程那套「先看落差、再看后半程」是同一个道理：
    /// 有落差但中间有起伏，是一次性台阶，不是持续劣势。
    /// </summary>
    private static void AppendQuarterVerdict(StringBuilder sb, MatchReport rep)
    {
        var f = rep.QuarterFps;
        if (f.Any(x => x <= 0))
        {
            return;
        }

        double drop = (f[0] - f[3]) / f[0] * 100;
        bool downAll = f[0] >= f[1] && f[1] >= f[2] && f[2] >= f[3];

        sb.AppendLine();
        sb.Append("这一份：Q1 到 Q4 " + (drop >= 0 ? "降" : "升") + " "
            + Math.Abs(drop).ToString("0") + "%");
        if (downAll && drop >= 10)
        {
            sb.AppendLine("，四格一路向下 —— 一局之内确实在变卡。");
        }
        else if (drop >= 10)
        {
            sb.AppendLine("，但中间有起伏，不是一路向下 —— 有落差，不像持续劣化。");
        }
        else
        {
            sb.AppendLine("，局内基本持平 —— 谈不上「一局之内越打越卡」。");
        }
    }

    private static string Row(double[] v, int dp)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < v.Length; i++)
        {
            sb.Append(Cell(v[i], 8, dp));
        }
        return sb.ToString();
    }

    private static string Cell(double v, int w, int dp = 0)
    {
        return v.ToString("F" + dp, CultureInfo.InvariantCulture).PadLeft(w);
    }

    private static string Clock(double sec)
    {
        int m = (int)(sec / 60);
        int s = (int)(sec % 60);
        return m.ToString("00") + ":" + s.ToString("00");
    }

    private static string Dur(double sec)
    {
        return ((int)(sec / 60)).ToString().PadLeft(3) + " 分";
    }
}
