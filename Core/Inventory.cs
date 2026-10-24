using System.Diagnostics;
using System.Security.Principal;

namespace GameHelper.Core;

/// <summary>运行环境与游戏进程的探测。</summary>
public static class Inventory
{
    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>返回游戏主进程；未运行则为 null。</summary>
    public static Process? FindGame()
    {
        try
        {
            var procs = Process.GetProcessesByName(ProcessClassifier.GameProcessName);
            if (procs.Length == 0)
            {
                return null;
            }

            // 只保留第一个，其余句柄释放掉，避免句柄泄漏。
            var keep = procs[0];
            for (int i = 1; i < procs.Length; i++)
            {
                procs[i].Dispose();
            }
            return keep;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>游戏进程的启动时间；读不到则为 null。</summary>
    public static DateTime? GameStartTime(Process? game)
    {
        if (game is null)
        {
            return null;
        }
        try
        {
            return game.StartTime;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 找出正在运行的游戏与反作弊进程。清理功能靠它做硬门禁：
    /// 只要这两个还活着，就绝不允许改动游戏目录。
    /// </summary>
    public static List<string> FindBlockers()
    {
        var result = new List<string>();
        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch
        {
            return result;
        }

        foreach (var p in all)
        {
            try
            {
                string name = p.ProcessName;
                if (name.Equals(ProcessClassifier.GameProcessName, StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(ProcessClassifier.AntiCheatPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add($"{name} (PID {p.Id})");
                }
            }
            catch
            {
                // 访问被拒绝的进程直接跳过
            }
            finally
            {
                p.Dispose();
            }
        }

        return result;
    }

    /// <summary>
    /// 枚举「可以拿来当监控目标」的进程。
    ///
    /// 为什么要过滤而不是列出全部进程：本机同时跑着几百个进程，
    /// 其中绝大多数没有窗口、或者一结束就会让系统出问题。
    /// 把它们列进选择列表，用户既找不到想要的，也可能误选到危险项。
    ///
    /// 过滤条件（每一条都有理由，不是凑数）：
    ///   - **必须有窗口标题** —— 这是最有效的一条。后台服务、驱动宿主、
    ///     注入到各进程里的运行时（如 ctfmon）全都没有主窗口，
    ///     一刀砍掉九成噪声。浏览器虽然也有窗口，但它确实是用户主动开着的东西，
    ///     列出来由用户自己判断，比我猜准。
    ///   - **排除系统组件** —— 名单见 <see cref="ProcessClassifier.IsSystemApp"/>。
    ///   - **排除安全 / 输入法软件** —— 结束它们会让用户失去防护或无法打字，
    ///     虽然这里只是监控、不会结束，但列在「可监控目标」里仍然会造成误解。
    ///   - **排除《永劫无间》自身** —— 它走 Naraka 通道，那条路上判定层是可用的。
    ///     两个通道对同一个进程给出不同口径，只会让人困惑。
    ///   - **排除本工具自己** —— 监控自己的帧数没有意义。
    ///
    /// 显存占用是附加上去的信息（读不到显示「未知」），**不参与过滤**：
    /// 探针不一定覆盖得到每个进程，而一个还没开始渲染的游戏占用就是 0，
    /// 按占用过滤会正好把用户想监控的那个游戏漏掉。
    /// </summary>
    public static List<TargetCandidate> FindTargetCandidates()
    {
        var result = new List<TargetCandidate>();
        var seen = new HashSet<int>();
        int self = Environment.ProcessId;

        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch
        {
            return result;
        }

        foreach (var p in all)
        {
            try
            {
                if (p.Id == self || seen.Contains(p.Id))
                {
                    continue;
                }

                string name = p.ProcessName;

                // 第一条硬门槛：没有主窗口的进程直接排除。
                // MainWindowHandle 在进程没有窗口时是 0；标题非空进一步确认。
                if (p.MainWindowHandle == IntPtr.Zero)
                {
                    continue;
                }

                string title;
                try
                {
                    title = p.MainWindowTitle ?? string.Empty;
                }
                catch
                {
                    title = string.Empty;
                }

                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                if (ProcessClassifier.IsSystemApp(name)
                    || ProcessClassifier.IsSecurityOrInputSoftware(name)
                    || ProcessClassifier.IsGame(name))
                {
                    continue;
                }

                seen.Add(p.Id);
                result.Add(new TargetCandidate(p.Id, name, title, 0));
            }
            catch
            {
                // 单个进程读不了（受保护、正在退出）—— 跳过它，不影响其它
            }
            finally
            {
                p.Dispose();
            }
        }

        return result;
    }

    /// <summary>Steam 是否在运行。清理前会据此给出提示。</summary>
    public static bool IsSteamRunning()
    {
        try
        {
            var p = Process.GetProcessesByName("steam");
            bool any = p.Length > 0;
            foreach (var x in p)
            {
                x.Dispose();
            }
            return any;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>CPU 型号，用于「关于」窗口。</summary>
    public static string CpuName()
    {
        try
        {
            string? v = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
            return string.IsNullOrWhiteSpace(v) ? "未知" : v;
        }
        catch
        {
            return "未知";
        }
    }
}
