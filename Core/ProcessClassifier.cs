namespace GameHelper.Core;

/// <summary>
/// 进程名到类别的判定规则。
///
/// 名单与 PowerShell 版本逐条一致 —— 这些名字是照抄过来的，不是重新想的，
/// 目的是保证 exe 版和脚本版对同一个进程给出同样的结论。
/// </summary>
public static class ProcessClassifier
{
    /// <summary>本工具认的游戏进程名（不含扩展名）。</summary>
    public const string GameProcessName = "NarakaBladepoint";

    /// <summary>反作弊相关进程名前缀。</summary>
    public const string AntiCheatPrefix = "Neac";

    /// <summary>
    /// Windows 与桌面组件。列在这里是为了「显式地」表示不关它们，
    /// 而不是因为拿不准。列表里每一项结束掉都会让桌面出问题。
    /// </summary>
    private static readonly HashSet<string> SystemApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "csrss", "winlogon", "services", "lsass", "smss", "Registry",
        "fontdrvhost", "sihost", "System", "MemCompression", "svchost", "audiodg",
        "explorer", "SearchIndexer", "StartMenuExperienceHost", "ShellExperienceHost",
        "TextInputHost", "ApplicationFrameHost", "SearchHost", "LockApp",
        "SecurityHealthService", "WmiPrvSE", "Idle",
    };

    public static bool IsSystemApp(string processName) => SystemApps.Contains(processName);

    public static bool IsGame(string processName) =>
        !string.IsNullOrEmpty(processName) &&
        processName.StartsWith(GameProcessName, StringComparison.OrdinalIgnoreCase);

    public static bool IsAntiCheat(string processName) =>
        !string.IsNullOrEmpty(processName) &&
        processName.StartsWith(AntiCheatPrefix, StringComparison.OrdinalIgnoreCase);

    public static ProcClass Classify(string processName) =>
        IsGame(processName) ? ProcClass.Game
        : SystemApps.Contains(processName) ? ProcClass.System
        : ProcClass.Other;

    /// <summary>
    /// 显存助手里默认关掉的桌面装饰类程序（guard 模式的默认名单）。
    /// 保留在这里，供「一键释放」按钮提示用。
    /// </summary>
    public static readonly string[] KnownVramHogs = { "wallpaper32", "wallpaper64" };

    /// <summary>
    /// 安全 / 输入法类软件。不阻止结束，但在确认框里明确标出来。
    ///
    /// 理由是实测出来的：这台机器上独显占用最多的进程恰好是 HipsDaemon（火绒）。
    /// 一个「一键结束占用显存的后台程序」的按钮把它干掉，是用户未必想要的结果，
    /// 所以至少要让人在按下确认之前看见。
    /// </summary>
    private static readonly string[] SecurityAndInputSoftware =
    {
        "hipsdaemon", "hipstray", "usysdiag",          // 火绒
        "360tray", "360safe", "zhudongfangyu", "qhsafe", // 360
        "msmpeng", "nissrv", "securityhealthservice",  // Defender
        "avp", "avpui",                                 // 卡巴斯基
        "ekrn", "egui",                                 // ESET
        "kxescore", "kavfs",                            // 金山
        "qqpctray", "qqpcmgr",                          // 腾讯电脑管家
        "huorong", "wsctrl",
        "ctfmon", "chsisrv",                            // 输入法相关
    };

    /// <summary>是否是安全/输入法类软件。命中的项在结束确认框里会被标红提示。</summary>
    public static bool IsSecurityOrInputSoftware(string processName)
    {
        if (string.IsNullOrEmpty(processName))
        {
            return false;
        }

        string n = processName.ToLowerInvariant();
        foreach (var s in SecurityAndInputSoftware)
        {
            if (n == s || n.StartsWith(s, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
