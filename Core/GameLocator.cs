using System.Diagnostics;
using Microsoft.Win32;

namespace GameHelper.Core;

/// <summary>
/// 定位《永劫无间》的安装目录，以及清理功能的备份目录。
///
/// 为什么必须自动找而不能写死：
/// 上一版把 Steam 库所在的盘符直接写进了代码（<c>G:\SteamLibrary\...</c>）。
/// 换一台电脑 —— 哪怕只是把游戏装到 D 盘、或者这台机器根本没有 G 盘 ——
/// 那个路径就是错的。而界面**不会报错**：扫描结果全是「不存在」，
/// 看上去像是「你的游戏很干净」，用户根本不知道其实是路径指错了地方。
/// 这类「安静地给错结论」的故障，比直接崩掉更麻烦。
///
/// 所以改成三层探测，从最准到最慢依次尝试；都找不到就返回 null，
/// 让界面明确说「没找到，请手动选」—— 不猜、不填一个假路径。
/// </summary>
public static class GameLocator
{
    /// <summary>《永劫无间》在 Steam 库里的目录名。</summary>
    private const string SteamFolderName = "Naraka Bladepoint";

    /// <summary>各固定盘上常见的 Steam 库位置（相对盘符）。</summary>
    private static readonly string[] CommonSteamRoots =
    {
        "SteamLibrary",
        "Steam",
        Path.Combine("Program Files (x86)", "Steam"),
        Path.Combine("Program Files", "Steam"),
        Path.Combine("Games", "SteamLibrary"),
        Path.Combine("Games", "Steam"),
    };

    /// <summary>
    /// 找游戏目录。找不到返回 <c>null</c>。
    /// </summary>
    public static string? FindGameDir()
    {
        return FromRunningProcess()
            ?? FromSteamLibraries()
            ?? FromCommonDrives();
    }

    // ------------------------------------------------------------ 第一层：运行中进程

    /// <summary>
    /// 从正在运行的游戏进程反推安装目录。这一层最准 ——
    /// 游戏既然在跑，它的 exe 在哪，安装目录就在哪。
    /// </summary>
    private static string? FromRunningProcess()
    {
        try
        {
            using var game = Inventory.FindGame();
            if (game is null)
            {
                return null;
            }

            string? file = game.MainModule?.FileName;
            if (string.IsNullOrEmpty(file))
            {
                return null;
            }

            string? dir = Path.GetDirectoryName(file);
            return LooksLikeGameDir(dir) ? dir : null;
        }
        catch
        {
            // MainModule 读不了（进程受保护、已退出、权限不足）—— 换下一层
            return null;
        }
    }

    // ------------------------------------------------------------ 第二层：Steam 库登记

    private static string? FromSteamLibraries()
    {
        foreach (string lib in SteamLibraryPaths())
        {
            string? hit = Probe(Path.Combine(lib, "steamapps", "common", SteamFolderName));
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    /// <summary>枚举本机所有 Steam 库目录（含 Steam 自己那个）。</summary>
    private static IEnumerable<string> SteamLibraryPaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? steam = ReadSteamInstallPath();
        if (steam is not null && seen.Add(steam))
        {
            yield return steam;
        }

        if (steam is null)
        {
            yield break;
        }

        // libraryfolders.vdf 里登记了这台机器上的每一个 Steam 库，
        // 包括装在其它盘的那些 —— 这是「游戏不在 Steam 安装盘」时唯一靠谱的线索。
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        foreach (string p in ParseLibraryVdf(vdf))
        {
            if (seen.Add(p))
            {
                yield return p;
            }
        }
    }

    /// <summary>读 Steam 自身的安装位置（注册表；读不到就返回 null）。</summary>
    private static string? ReadSteamInstallPath()
    {
        // 两个位置都要看：64 位 Steam 装在 Program Files，32 位在 (x86)。
        string[] keys =
        {
            @"HKEY_CURRENT_USER\Software\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\Software\WOW6432Node\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\Software\Valve\Steam",
        };

        foreach (string key in keys)
        {
            try
            {
                object? v = Registry.GetValue(key, "SteamPath", null)
                         ?? Registry.GetValue(key, "InstallPath", null);
                string? s = v as string;
                if (!string.IsNullOrWhiteSpace(s) && Directory.Exists(s))
                {
                    return s;
                }
            }
            catch
            {
                // 没有 Steam、权限受限、键不存在 —— 都换下一个
            }
        }

        return null;
    }

    /// <summary>
    /// 从 libraryfolders.vdf 里抽出库路径。
    ///
    /// 只用正则抓 <c>"path"  "G:\\SteamLibrary"</c> 这种行，不做完整 VDF 解析 ——
    /// vdf 是 Valve 自己的格式，完整解析要处理嵌套与转义，而这里只需要一个字段，
    /// 为它引入一整套解析器不值得，也更容易在格式变更后整体失效。
    /// </summary>
    private static IEnumerable<string> ParseLibraryVdf(string vdfPath)
    {
        List<string> result = new();
        try
        {
            if (!File.Exists(vdfPath))
            {
                return result;
            }

            foreach (string raw in File.ReadAllLines(vdfPath))
            {
                int q = raw.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase);
                if (q < 0)
                {
                    continue;
                }

                int first = raw.IndexOf('"', q + 6);
                int last = raw.LastIndexOf('"');
                if (first < 0 || last <= first)
                {
                    continue;
                }

                string p = raw[(first + 1)..last].Replace("\\\\", "\\");
                if (p.Length > 1 && Directory.Exists(p))
                {
                    result.Add(p);
                }
            }
        }
        catch
        {
            // 文件被占用、格式变了 —— 当作没有这份线索
        }

        return result;
    }

    // ------------------------------------------------------------ 第三层：扫固定盘

    /// <summary>
    /// 兜底：逐个固定盘试常见位置。
    ///
    /// 只扫固定盘 —— 扫网络盘会去唤醒已断开的映射，卡住几十秒；
    /// 扫光盘/软盘则会弹出「请插入磁盘」。
    /// </summary>
    private static string? FromCommonDrives()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed)
            {
                continue;
            }

            string root;
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                root = drive.RootDirectory.FullName;
            }
            catch
            {
                continue;
            }

            foreach (string mid in CommonSteamRoots)
            {
                string? hit = Probe(Path.Combine(root, mid, "steamapps", "common", SteamFolderName));
                if (hit is not null)
                {
                    return hit;
                }
            }
        }

        return null;
    }

    // ------------------------------------------------------------ 判定与兜底

    /// <summary>
    /// 判断一个目录是不是真的装着游戏。
    ///
    /// 光看目录名不够：同名空目录、或者卸载后留下的残骸都会被认成有效。
    /// 必须确认**游戏主程序在里面**，否则后面的扫描会得出一整套
    /// 「都不存在」的结论，而用户会误读成「我的游戏很干净」。
    /// </summary>
    private static bool LooksLikeGameDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.Combine(dir, ProcessClassifier.GameProcessName + ".exe"));
        }
        catch
        {
            return false;
        }
    }

    private static string? Probe(string dir) => LooksLikeGameDir(dir) ? dir : null;

    /// <summary>
    /// 推断备份目录（清理功能把文件「移动」到这里，而不是删除）。
    ///
    /// 优先放在**游戏所在的盘**：同盘移动只是改个目录项，瞬间完成；
    /// 跨盘移动则是真拷贝几百 MB，慢而且中途失败更难收场。
    /// 那个盘不存在或不可写时，退回「文档」—— 本用户永远可写。
    /// </summary>
    public static string SuggestBackupRoot(string? gameDir)
    {
        const string folder = "_naraka_cleanup_backup";

        if (!string.IsNullOrWhiteSpace(gameDir))
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(gameDir)) ?? string.Empty;
                if (root.Length > 1 && Directory.Exists(root))
                {
                    return Path.Combine(root, folder);
                }
            }
            catch
            {
                // 路径奇异（例如含非法字符）—— 走兜底
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "游戏助手", "清理备份");
    }
}
