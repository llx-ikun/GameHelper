using System.Text;

namespace GameHelper.Core;

/// <summary>把一次快照生成成可以贴给别人看的纯文本体检报告。</summary>
public static class ReportBuilder
{
    public static string BuildHealthReport(Snapshot s)
    {
        var sb = new StringBuilder();

        sb.AppendLine("游戏助手 · 显存体检报告");
        sb.AppendLine(new string('=', 64));
        sb.AppendLine("生成时间 : " + s.At.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine();

        if (!s.Gpu.HasData)
        {
            sb.AppendLine("无法读取显卡数据。");
            sb.AppendLine("本报告的显存部分需要一块驱动正常的 NVIDIA 显卡，且 nvidia-smi 可用。");
            sb.AppendLine("nvidia-smi 路径 : " + (NvidiaSmi.Path ?? "未找到"));
            return sb.ToString();
        }

        sb.AppendLine("【显卡】");
        sb.AppendLine("  型号     : " + s.Gpu.Name);
        sb.AppendLine($"  显存     : 已用 {s.Gpu.MemUsedMb:0} / {s.Gpu.MemTotalMb:0} MB"
                      + $"（{s.Gpu.Pct:0.0}%，剩余 {s.Gpu.FreeMb:0} MB）");
        sb.AppendLine($"  温度     : {s.Gpu.TempC:0} C");
        sb.AppendLine($"  占用率   : {s.Gpu.UtilPct:0} %");
        sb.AppendLine($"  频率     : 核心 {s.Gpu.GpuClockMhz:0} MHz / 显存 {s.Gpu.MemClockMhz:0} MHz");
        sb.AppendLine($"  功耗     : {s.Gpu.PowerW:0.0} W");
        sb.AppendLine();

        sb.AppendLine("【谁在占用显存】");
        sb.AppendLine("  " + "进程".PadRight(28) + "显卡".PadRight(18) + "本机".PadLeft(10) + "共享".PadLeft(10) + "   类别");
        sb.AppendLine("  " + new string('-', 78));

        int n = 0;
        foreach (var p in s.Procs)
        {
            n++;
            if (n > 22)
            {
                break;
            }
            string adapter = p.OnPrimaryAdapter ? p.AdapterName : p.AdapterName + " *";
            sb.AppendLine("  " + p.Name.PadRight(28)
                              + adapter.PadRight(18)
                              + (p.LocalMb.ToString("0.0") + " MB").PadLeft(10)
                              + (p.SharedMb.ToString("0.0") + " MB").PadLeft(10)
                              + "   " + p.ClassText);
        }
        sb.AppendLine();

        if (s.AdapterCount > 1)
        {
            sb.AppendLine("  带 * 的行不在游戏所用的显卡上，结束它们不会释放游戏显存。");
            sb.AppendLine();
        }

        sb.AppendLine("【分类汇总】" + (string.IsNullOrEmpty(s.PrimaryAdapterName)
            ? string.Empty
            : "（只统计 " + s.PrimaryAdapterName + "）"));
        sb.AppendLine("  " + "游戏自身".PadRight(16) + (s.GameVramMb.ToString("0.0") + " MB").PadLeft(12) + "   (无法从外部释放)");
        sb.AppendLine("  " + "Windows 组件".PadRight(16) + (s.SystemVramMb.ToString("0.0") + " MB").PadLeft(12) + "   (dwm、explorer 等，别动)");
        sb.AppendLine("  " + "其他后台程序".PadRight(16) + (s.OtherVramMb.ToString("0.0") + " MB").PadLeft(12) + "   (可结束回收)");
        if (s.UnexplainedMb > 1)
        {
            sb.AppendLine("  " + "驱动/未归因".PadRight(16) + (s.UnexplainedMb.ToString("0.0") + " MB").PadLeft(12) + "   (非进程持有)");
        }
        sb.AppendLine();

        if (s.MultiAdapter)
        {
            sb.AppendLine("【多显卡说明】");
            sb.AppendLine($"  本机共 {s.AdapterCount} 个显卡适配器：");
            foreach (var a in s.Adapters!)
            {
                bool isPrimary = !string.IsNullOrEmpty(s.PrimaryAdapterName) &&
                                 a.Name.Equals(s.PrimaryAdapterName, StringComparison.OrdinalIgnoreCase);
                sb.AppendLine($"    {(isPrimary ? "→" : " ")} {a.Name}  （专用显存 {a.DedicatedVideoMb:0} MB）");
            }
            sb.AppendLine($"  其中「{DescribeOthers(s)}」上的后台程序占用 {s.OtherAdapterVramMb:0.0} MB。");
            sb.AppendLine("  这部分属于核显或虚拟显示器，结束它们不会释放游戏所用显卡的显存。");
            sb.AppendLine();
        }

        if (s.Gpu.MemTotalMb > 0)
        {
            double pct = 100.0 * s.OtherVramMb / s.Gpu.MemTotalMb;
            sb.AppendLine($"  结束后台程序最多可回收约 {s.OtherVramMb:0} MB（{pct:0.0}% 的总显存）。");
        }
        sb.AppendLine();

        var others = s.Procs.Where(p => p.Class == ProcClass.Other && p.OnPrimaryAdapter && p.LocalMb > 0)
                            .Take(12)
                            .ToList();
        if (others.Count > 0)
        {
            sb.AppendLine("【占用最多的后台程序】");
            foreach (var o in others)
            {
                sb.AppendLine("  " + o.Name.PadRight(30) + (o.LocalMb.ToString("0.0") + " MB").PadLeft(10));
            }
            sb.AppendLine();
        }

        if (s.OtherVramMb < 150)
        {
            sb.AppendLine("目前后台程序占用不多。这台机器上显存压力的真正来源是");
            sb.AppendLine("游戏本身及其画质设置，而不是后台程序。");
            sb.AppendLine();
        }

        sb.AppendLine("【另外两项】");
        sb.AppendLine($"  系统内存 : {s.RamUsedMb:0} / {s.RamTotalMb:0} MB");
        if (s.GameRunning)
        {
            sb.AppendLine($"  游戏进程 : 正在运行，物理内存 {s.GameRamMb:0} MB");
            if (s.GameStartedAt is not null)
            {
                sb.AppendLine($"  本局时长 : {Describe(DateTime.Now - s.GameStartedAt.Value)}");
            }
            sb.AppendLine($"  游戏显存 : {s.GameVramMb:0.0} MB");
        }
        else
        {
            sb.AppendLine("  游戏进程 : 未运行");
        }
        sb.AppendLine();

        sb.AppendLine("【三条技术边界】");
        sb.AppendLine("一、游戏自身的显存无法从外部释放。Windows 的 WDDM 显存管理器只允许");
        sb.AppendLine("    「拥有该显存的进程」自己归还；从外部强制的唯一手段是注入游戏进程，");
        sb.AppendLine("    而内核级反作弊会将其判定为篡改。本工具绝不做这件事。");
        sb.AppendLine("二、「挂起」进程不会释放任何显存。实测：一个持有 88.67 MB 显存的进程被");
        sb.AppendLine("    冻结 5 秒，占用始终是 88.67 MB。只有进程真正退出，显存才会释放。");
        sb.AppendLine("三、因此唯一有效且安全的手段只有两条：关闭无关后台程序；显存将满时");
        sb.AppendLine("    退回大厅刷新，让游戏引擎自己拆掉渲染目标、重新分配。");

        return sb.ToString();
    }

    /// <summary>列出主显卡之外的其他适配器名称，用于「多显卡说明」那段。</summary>
    private static string DescribeOthers(Snapshot s)
    {
        if (s.Adapters is null || s.Adapters.Count == 0)
        {
            return "其他显卡";
        }

        var names = new List<string>();
        foreach (var a in s.Adapters)
        {
            bool isPrimary = !string.IsNullOrEmpty(s.PrimaryAdapterName) &&
                             a.Name.Equals(s.PrimaryAdapterName, StringComparison.OrdinalIgnoreCase);
            if (!isPrimary)
            {
                names.Add(a.ShortName);
            }
        }

        return names.Count == 0 ? "其他显卡" : string.Join("、", names);
    }

    public static string Describe(TimeSpan t)
    {
        if (t.TotalHours >= 1)
        {
            return $"{(int)t.TotalHours} 小时 {t.Minutes} 分";
        }
        if (t.TotalMinutes >= 1)
        {
            return $"{t.Minutes} 分 {t.Seconds} 秒";
        }
        return $"{(int)t.TotalSeconds} 秒";
    }
}
