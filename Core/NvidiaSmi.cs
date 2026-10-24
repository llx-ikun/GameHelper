using System.Globalization;

namespace GameHelper.Core;

/// <summary>
/// 通过 nvidia-smi 读取显卡状态。
///
/// 为什么不走 NVML / NVAPI：nvidia-smi.exe 随驱动一起安装，字段名稳定，
/// 而且这正好是 PowerShell 版本一直在用的数据源 —— 换掉它意味着两版
/// 工具的数字可能对不上，排查问题时反而添乱。
/// </summary>
public static class NvidiaSmi
{
    private static readonly object Gate = new();
    private static bool _resolved;
    private static string? _path;

    /// <summary>nvidia-smi.exe 的完整路径；找不到时为 null。</summary>
    public static string? Path
    {
        get
        {
            lock (Gate)
            {
                if (!_resolved)
                {
                    _path = Resolve();
                    _resolved = true;
                }
                return _path;
            }
        }
    }

    /// <summary>
    /// 显卡数据读不到时，该给用户看哪句话。
    ///
    /// 必须分清两种情况，混成一句「等待显卡数据」会害人：
    ///   · **压根找不到 nvidia-smi** —— 在 AMD / Intel 显卡的机器上是常态。
    ///     这是环境限制，不是工具坏了；用户该立刻知道，而不是对着窗口干等
    ///     一个永远不会来的数字。
    ///   · **找得到但读不出来** —— 这才是值得排查的异常。
    /// </summary>
    public static string UnavailableReason =>
        Path is null
            ? "未找到 nvidia-smi：本机可能不是 NVIDIA 显卡"
            : "nvidia-smi 存在但读不到数据（驱动未响应？）";

    /// <summary>
    /// 同上，但带上「还能用什么」的后半句，用于显眼位置（横幅、曲线中央）。
    ///
    /// 只报坏消息不报还有什么能用，用户会以为整个工具都废了。
    /// </summary>
    public static string UnavailableHint =>
        Path is null
            ? "本机未检测到 NVIDIA 显卡（找不到 nvidia-smi）—— 显存曲线、温度、占用都读不到。"
              + "这是环境限制，不是本工具的故障；清理、日志与帧数功能不受影响。"
            : "nvidia-smi 存在但读不到数据，请确认显卡驱动正常。";

    private static string? Resolve()
    {
        var candidates = new List<string>
        {
            System.IO.Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
        };

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(pf))
        {
            candidates.Add(System.IO.Path.Combine(pf, @"NVIDIA Corporation\NVSMI\nvidia-smi.exe"));
        }

        // 新版驱动把 nvidia-smi 放在 System32，老版本放在 NVSMI 目录，
        // 两个都查一遍；再兜底搜一次 PATH。
        foreach (var c in candidates)
        {
            if (System.IO.File.Exists(c))
            {
                return c;
            }
        }

        try
        {
            var r = Shell.Run("where.exe", "nvidia-smi.exe", 5000);
            foreach (var line in r.Lines())
            {
                if (System.IO.File.Exists(line))
                {
                    return line;
                }
            }
        }
        catch
        {
            // 忽略：找不到就是没有
        }

        return null;
    }

    private const string QueryFields =
        "name,temperature.gpu,utilization.gpu,memory.used,memory.total," +
        "clocks.current.graphics,clocks.current.memory,power.draw";

    /// <summary>单次查询显卡状态。驱动未响应或读取失败时返回 <see cref="GpuInfo.Empty"/>。</summary>
    public static GpuInfo Query()
    {
        var exe = Path;
        if (exe is null)
        {
            return GpuInfo.Empty;
        }

        var r = Shell.Run(exe, $"--query-gpu={QueryFields} --format=csv,noheader,nounits", 12000);
        if (!r.Ok)
        {
            return GpuInfo.Empty;
        }

        var line = r.Lines().FirstOrDefault();
        if (string.IsNullOrEmpty(line))
        {
            return GpuInfo.Empty;
        }

        var f = line.Split(',');
        for (int i = 0; i < f.Length; i++)
        {
            f[i] = f[i].Trim();
        }

        // 正常是 8 个字段。显卡名里若含逗号，则最右 7 个一定是数字列。
        string nameField;
        int numStart;
        if (f.Length >= 8)
        {
            numStart = f.Length - 7;
            nameField = string.Join(", ", f.Take(numStart));
        }
        else if (f.Length == 5)
        {
            // 老版本驱动没有 clocks/power 字段
            nameField = f[0];
            numStart = 1;
        }
        else
        {
            return GpuInfo.Empty;
        }

        double temp = D(f[numStart + 0]);
        double util = D(f[numStart + 1]);
        double used = D(f[numStart + 2]);
        double total = D(f[numStart + 3]);

        double gclk = f.Length >= numStart + 5 ? D(f[numStart + 4]) : 0;
        double mclk = f.Length >= numStart + 6 ? D(f[numStart + 5]) : 0;
        double pwr = f.Length >= numStart + 7 ? D(f[numStart + 6]) : 0;

        if (total <= 0)
        {
            return GpuInfo.Empty;
        }

        return new GpuInfo(nameField, temp, util, used, total, gclk, mclk, pwr);
    }

    /// <summary>
    /// 读出当前的降频原因。`-q -d PERFORMANCE` 输出量大且耗时约 0.5~1 秒，
    /// 所以只按较低频率调用，不放进每次采样里。
    /// </summary>
    public static string QueryThrottle()
    {
        var exe = Path;
        if (exe is null)
        {
            return "unknown";
        }

        var r = Shell.Run(exe, "-q -d PERFORMANCE", 12000);
        if (!r.Ok)
        {
            return "unknown";
        }

        string[] patterns =
        {
            "SW Power Cap",
            "HW Thermal Slowdown",
            "SW Thermal Slowdown",
            "HW Slowdown",
            "HW Power Brake Slowdown",
        };

        var hits = new List<string>();
        foreach (var p in patterns)
        {
            // 匹配 "<原因> ... : Active"
            bool active = false;
            foreach (var line in r.Lines())
            {
                if (line.IndexOf(p, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                int colon = line.LastIndexOf(':');
                if (colon >= 0 && line[(colon + 1)..].Trim().Equals("Active", StringComparison.OrdinalIgnoreCase))
                {
                    active = true;
                    break;
                }
            }
            if (active)
            {
                hits.Add(p);
            }
        }

        return hits.Count == 0 ? "none" : string.Join("|", hits);
    }

    /// <summary>nvidia-smi 在字段不可用时输出 [N/A]，这里统一按 0 处理。</summary>
    private static double D(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return 0;
        }
        s = s.Trim();
        if (s.StartsWith("[", StringComparison.Ordinal))
        {
            return 0;
        }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
