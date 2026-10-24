using System.Diagnostics;

namespace GameHelper.Core;

/// <summary>
/// 监控目标的种类。
///
/// 这个枚举决定的不只是「盯哪个进程」，更重要的是**汇总里允不允许说话**：
/// <list type="bullet">
///   <item><see cref="Naraka"/> —— 全部判定、对局拆分、针对性建议都可输出。
///         因为那些阈值是在《永劫无间》上标定的。</item>
///   <item><see cref="External"/> —— 只输出原始观测值。
///         判定层整体闭嘴，理由见 <see cref="MonitorSession"/> 里的说明。</item>
/// </list>
/// </summary>
public enum TargetKind
{
    /// <summary>《永劫无间》。判定层与建议唯一可用的场景。</summary>
    Naraka,

    /// <summary>用户手动指定的其他进程。只报数字，不给结论。</summary>
    External,
}

/// <summary>
/// 一次监控的目标：盯哪个进程，以及这个目标属于哪种口径。
///
/// 不可变 —— 采样线程和界面都会读它，用 record 是为了避免两边看到不同的值。
/// </summary>
public sealed record MonitorTarget(
    TargetKind Kind,
    int Pid,
    string ProcessName,
    string? WindowTitle = null)
{
    /// <summary>是否是《永劫无间》口径。</summary>
    public bool IsNaraka => Kind == TargetKind.Naraka;

    /// <summary>界面上显示的名字：有窗口标题就「进程名 · 标题」，否则只显示进程名。</summary>
    public string DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(WindowTitle))
            {
                return ProcessName;
            }

            // 标题可能很长（浏览器标签页能到几十个字符），截断避免把布局撑坏。
            string t = WindowTitle.Trim();
            if (t.Length > 24)
            {
                t = t[..24] + "…";
            }

            return ProcessName + " · " + t;
        }
    }

    /// <summary>按 PID 重新打开这个进程；已退出或读不到就返回 null。</summary>
    public Process? TryOpen()
    {
        if (Pid <= 0)
        {
            return null;
        }

        try
        {
            var p = Process.GetProcessById(Pid);
            // 进程可能正处于退出过程中：能拿到对象但已 HasExited。
            if (p.HasExited)
            {
                p.Dispose();
                return null;
            }

            return p;
        }
        catch
        {
            // 不存在、权限不足 —— 都当作「目标已不在」。
            return null;
        }
    }
}

/// <summary>进程选择列表里的一行。</summary>
public sealed record TargetCandidate(
    int Pid,
    string ProcessName,
    string WindowTitle,
    double VramMb)
{
    /// <summary>是否读到了显存占用（读不到只说明探针没覆盖到它，不代表它不占）。</summary>
    public bool HasVram => VramMb > 0;

    public string VramText => HasVram ? Math.Round(VramMb).ToString("0") + " MB" : "未知";

    public string DisplayName
    {
        get
        {
            string t = WindowTitle.Trim();
            if (t.Length > 30)
            {
                t = t[..30] + "…";
            }

            return string.IsNullOrEmpty(t) ? ProcessName : ProcessName + "  —  " + t;
        }
    }
}
