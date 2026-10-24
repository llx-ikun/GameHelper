using System.Text;

namespace GameHelper.Core;

/// <summary>一条清理目标。Kind: 'D' 目录 / 'F' 文件。Marker 为空表示不做标记校验。</summary>
public sealed record CleanTarget(string Rel, char Kind, string Marker);

/// <summary>一次扫描的结果行。</summary>
public sealed record ScanEntry(
    string Rel,
    char Kind,
    string Marker,
    bool Exists,
    bool IsDir,
    long Bytes,
    bool Verified,
    bool Blocked)
{
    public double SizeMb => Math.Round(Bytes / (1024.0 * 1024.0), 1);

    public string KindText => Kind == 'D' ? "目录" : "文件";

    /// <summary>界面上的状态列。</summary>
    public string StatusText =>
        !Exists ? "不存在"
        : Blocked ? "已拦截（受保护）"
        : Verified ? "已确认，可隔离"
        : "标记不符，需人工核对";

    /// <summary>只有「存在 + 通过标记校验 + 不在保护名单」的项才允许被移出。</summary>
    public bool CanQuarantine => Exists && Verified && !Blocked;
}

/// <summary>隔离操作的结果。</summary>
public sealed record QuarantineResult(
    int Moved,
    int Failed,
    string? Destination,
    IReadOnlyList<string> FailedItems,
    IReadOnlyList<string> Messages);

/// <summary>还原操作的结果。</summary>
public sealed record RestoreResult(int Restored, int Skipped, IReadOnlyList<string> Messages);

/// <summary>
/// 游戏目录的第三方文件清理。
///
/// 安全模型（与脚本版完全一致，一条都没放松）：
///   1. 扫描是只读的；
///   2. 「隔离」是把文件移动到备份目录，绝不删除；
///   3. 每个目标在处理前都要通过内容/子项标记校验，标记不符的跳过；
///   4. 已知游戏文件走硬保护名单，即使被写进目标清单也不会被处理；
///   5. 游戏或反作弊在运行期间，隔离直接被门禁拦下。
/// </summary>
public sealed class CleanerService
{
    // 这里**不再保留**任何默认盘符。
    // 原先写的是某一台机器上的 G:\SteamLibrary\... —— 换台电脑就是错的，
    // 而界面只会显示「扫描结果都不存在」，安静地给出错误结论。
    // 现在目录一律由 GameLocator 按本机实际情况推断；推断不到就留空，
    // 留空是安全的：扫描与隔离都有 GameDirExists 门禁挡着。

    /// <summary>游戏安装目录。留空表示尚未确定；使用前务必先看 <see cref="GameDirExists"/>。</summary>
    public string GameDir { get; set; } = string.Empty;

    /// <summary>隔离文件的备份目录（只移动，不删除）。</summary>
    public string BackupRoot { get; set; } = string.Empty;

    /// <summary>
    /// 属于 dlssg-to-fsr3（Nukem9）/ DLSSTweaks（emoose）的目标，已于 2026-10-14 逐项核验。
    /// 标记的含义 —— 目录：必须存在的子项名；文件：必须包含的子串。
    /// </summary>
    public static readonly IReadOnlyList<CleanTarget> Targets = new CleanTarget[]
    {
        new(".github", 'D', "FUNDING.yml"),
        new(".vs", 'D', "launch.vs.json"),
        new("ConfigTool", 'D', "DLSSTweaks.ConfigTool.csproj"),
        new("dependencies", 'D', "FidelityFX-SDK"),
        new("external", 'D', "DLSS"),
        new("source", 'D', "wrapper_generic"),
        new("src", 'D', "DllMain.cpp"),
        new("vcpkg-ports", 'D', "quickdllproxy"),
        new("resources", 'D', "dlssg_to_fsr3.ini"),
        new(".clang-format", 'F', "AlignOperands"),
        new(".editorconfig", 'F', "indent_style"),
        new(".gitignore", 'F', ".vs/launch.vs.json"),
        new(".gitmodules", 'F', "spdlog"),
        new("build_vs2022.bat", 'F', "Visual Studio 17"),
        new("cmake.toml", 'F', "dlsstweaks-proj"),
        new("CMakeLists.txt", 'F', "dlssg-to-fsr3"),
        new("CMakePresets.json", 'F', "cmake"),
        new("CMakeUserEnvVars.json", 'F', "GAME_ROOT_DIRECTORY"),
        new("cmkr.cmake", 'F', "cmkr"),
        new("DisableNvidiaSigOverride.reg", 'F', "41FCC608"),
        new("EnableNvidiaSigOverride.reg", 'F', "41FCC608"),
        new("dlsstweaks.ini", 'F', "DLSSTweaks"),
        new("LICENSE.md", 'F', "GNU GENERAL PUBLIC LICENSE"),
        new("Make-Release.ps1", 'F', "vcvarsall"),
        new("README.md", 'F', "dlssg-to-fsr3"),
        new("vcpkg.json", 'F', "quickdllproxy"),
    };

    /// <summary>
    /// 硬拦截名单：已确认的游戏文件，任何情况下都不碰。
    /// 名单里有些看着像第三方（例如 nvngx_dlss.dll），但它们是游戏随包分发的组件 ——
    /// 删掉会导致游戏起不来或光追/DLSS 失效。
    /// </summary>
    public static readonly IReadOnlyList<string> Protected = new string[]
    {
        "NarakaBladepoint_Data", "NarakaBladepoint.exe", "UnityPlayer.dll",
        "UnityPlayer_LVB.dll", "UnityPlayer_LVB_IBT.dll",
        "GameAssembly.dll", "GameAssembly_Super.dll", "GameAssembly_Super_IBT.dll",
        "dbghelp.dll", "ccmini", "CCRecord", "D3D12",
        "webviewsupport.cef904430", "webview_support_helper.dll",
        "log", "logs", "setting.json", "ExtraVars.unityargs",
        "steam_api64.dll", "steam_appid.txt", "steam.data",
        "StartGame.exe", "NeacClient.exe", "NeacInterface.dll", "NeacSafe64.sys",
        "nvngx_dlss.dll", "nvngx_dlssd.dll", "nvngx_dlssg.dll", "NE_NvNgxDlss.dll",
        "libxess.dll", "libxess_dx11.dll", "libxess_fg.dll", "libxell.dll",
        "sl.common.dll", "sl.dlss.dll", "sl.dlss_d.dll", "sl.dlss_g.dll",
        "sl.interposer.dll", "sl.pcl.dll", "sl.reflex.dll",
        "amd_fidelityfx_denoiser_dx12.dll", "amd_fidelityfx_framegeneration_dx12.dll",
        "amd_fidelityfx_loader_dx12.dll", "amd_fidelityfx_upscaler_dx12.dll",
        "ffx_fsr2_api_dx11_x64.dll",
        "UnityCrashHandler64.exe", "GFSDK_Aftermath_Lib.x64.dll",
        "WinPixEventRuntime.dll", "d3dcompiler_47.dll", "GdiPlus.dll",
        "msvcp100.dll", "msvcp110.dll", "msvcp120.dll", "msvcp140.dll",
        "msvcr100.dll", "msvcr110.dll", "msvcr120.dll",
        "vcruntime140.dll", "vccorlib140.dll", "concrt140.dll",
        "icudt78.dll", "icuin78.dll", "icuio78.dll", "icutu78.dll", "icuuc78.dll",
        "I18N.dll", "I18N.CJK.dll", "I18N.MidEast.dll", "I18N.Other.dll",
        "I18N.Rare.dll", "I18N.West.dll",
        "ntunisdk_config", "unisdk_dll_load_whitelist", "gmbridge.data",
        "webview_support_cef_enabled",
    };

    private static readonly HashSet<string> ProtectedSet = new(Protected, StringComparer.OrdinalIgnoreCase);

    public bool GameDirExists => Directory.Exists(GameDir);

    public bool BackupRootExists => Directory.Exists(BackupRoot);

    /// <summary>扫描游戏目录，返回每一项的存在性、大小与校验结果。只读。</summary>
    public List<ScanEntry> Scan()
    {
        var list = new List<ScanEntry>(Targets.Count);

        foreach (var t in Targets)
        {
            string full = Path.Combine(GameDir, t.Rel);
            bool exists = Directory.Exists(full) || File.Exists(full);
            if (!exists)
            {
                list.Add(new ScanEntry(t.Rel, t.Kind, t.Marker, false, false, 0, false, false));
                continue;
            }

            bool isDir = Directory.Exists(full);
            long bytes = GetSizeBytes(full, isDir);
            bool blocked = ProtectedSet.Contains(t.Rel);
            bool verified = !blocked && VerifyMarker(full, t.Kind, t.Marker);

            list.Add(new ScanEntry(t.Rel, t.Kind, t.Marker, true, isDir, bytes, verified, blocked));
        }

        return list;
    }

    /// <summary>
    /// 门禁：返回正在运行的游戏 / 反作弊进程。非空时调用方必须拒绝隔离操作。
    /// </summary>
    public List<string> Blockers() => Inventory.FindBlockers();

    /// <summary>
    /// 把通过校验的项目移动到备份目录。绝不删除。
    /// </summary>
    public QuarantineResult Quarantine(IEnumerable<ScanEntry> entries)
    {
        var messages = new List<string>();

        var live = Blockers();
        if (live.Count > 0)
        {
            messages.Add("已拒绝：游戏或其反作弊仍在运行 —— " + string.Join("、", live));
            messages.Add("这些进程存活期间不要改动游戏目录：文件会被锁，而且在运行中的内核级反作弊眼皮底下改游戏文件可能被判定为篡改。");
            return new QuarantineResult(0, 0, null, Array.Empty<string>(), messages);
        }

        if (!Directory.Exists(GameDir))
        {
            messages.Add("找不到游戏目录：" + GameDir);
            return new QuarantineResult(0, 0, null, Array.Empty<string>(), messages);
        }

        var picked = entries.Where(e => e.CanQuarantine).ToList();
        if (picked.Count == 0)
        {
            messages.Add("没有通过校验、需要隔离的内容。");
            return new QuarantineResult(0, 0, null, Array.Empty<string>(), messages);
        }

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string dest = Path.Combine(BackupRoot, stamp);
        Directory.CreateDirectory(dest);

        var moved = new List<string>();
        var failed = new List<string>();

        foreach (var e in picked)
        {
            string source = Path.Combine(GameDir, e.Rel);
            string target = Path.Combine(dest, e.Rel);
            try
            {
                string? parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                if (Directory.Exists(source))
                {
                    Directory.Move(source, target);
                }
                else
                {
                    File.Move(source, target, overwrite: true);
                }

                moved.Add(e.Rel);
                messages.Add("已移动：" + e.Rel);
            }
            catch (Exception ex)
            {
                failed.Add(e.Rel);
                messages.Add("失败：" + e.Rel + " —— " + ex.Message);
            }
        }

        WriteManifest(dest, moved);
        WriteRestoreScript(dest);

        messages.Add($"完成：移动 {moved.Count} 项，失败 {failed.Count} 项。备份位置：{dest}");
        messages.Add("要撤销，请在本页点「还原」，选择备份集 " + stamp + "。");

        return new QuarantineResult(moved.Count, failed.Count, dest, failed, messages);
    }

    /// <summary>列出所有备份集名称，按时间倒序。</summary>
    public List<string> ListBackups()
    {
        var result = new List<string>();
        if (!Directory.Exists(BackupRoot))
        {
            return result;
        }

        try
        {
            foreach (var d in Directory.GetDirectories(BackupRoot))
            {
                result.Add(Path.GetFileName(d));
            }
        }
        catch
        {
            // 忽略
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        result.Reverse();
        return result;
    }

    /// <summary>把某个备份集里的内容移回游戏目录。已存在的项目跳过，不覆盖。</summary>
    public RestoreResult Restore(string? backupName)
    {
        var messages = new List<string>();

        if (string.IsNullOrWhiteSpace(backupName))
        {
            var all = ListBackups();
            if (all.Count == 0)
            {
                messages.Add("没有找到备份集。");
                return new RestoreResult(0, 0, messages);
            }
            backupName = all[0];
        }

        string src = Path.Combine(BackupRoot, backupName);
        if (!Directory.Exists(src))
        {
            messages.Add("未找到该备份集：" + src);
            return new RestoreResult(0, 0, messages);
        }

        int restored = 0;
        int skipped = 0;

        // 优先按清单还原；清单不存在时退回遍历目录。
        var rels = ReadManifest(src);
        if (rels.Count == 0)
        {
            foreach (var entry in Directory.GetFileSystemEntries(src))
            {
                string nm = Path.GetFileName(entry);
                if (nm.Equals("manifest.txt", StringComparison.OrdinalIgnoreCase) ||
                    nm.Equals("restore.ps1", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                rels.Add(nm);
            }
        }

        foreach (var rel in rels)
        {
            string from = Path.Combine(src, rel);
            string to = Path.Combine(GameDir, rel);
            if (!Directory.Exists(from) && !File.Exists(from))
            {
                continue;
            }

            if (Directory.Exists(to) || File.Exists(to))
            {
                skipped++;
                messages.Add("已存在，跳过：" + rel);
                continue;
            }

            try
            {
                string? parent = Path.GetDirectoryName(to);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                if (Directory.Exists(from))
                {
                    Directory.Move(from, to);
                }
                else
                {
                    File.Move(from, to);
                }

                restored++;
                messages.Add("已还原：" + rel);
            }
            catch (Exception ex)
            {
                messages.Add("还原失败：" + rel + " —— " + ex.Message);
            }
        }

        messages.Add($"还原完成：{restored} 项，跳过 {skipped} 项。");
        return new RestoreResult(restored, skipped, messages);
    }

    private static IEnumerable<string> ManifestLines(string dir)
    {
        string path = Path.Combine(dir, "manifest.txt");
        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }
        try
        {
            return File.ReadAllLines(path, Encoding.UTF8);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static List<string> ReadManifest(string dir)
    {
        var rels = new List<string>();
        foreach (var line in ManifestLines(dir))
        {
            // 清单里的项目行以两个空格开头，头部信息行不含前导空格。
            if (line.StartsWith("  ", StringComparison.Ordinal))
            {
                rels.Add(line[2..]);
            }
        }
        return rels;
    }

    private static void WriteManifest(string dir, IReadOnlyList<string> moved)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Naraka Bladepoint cleanup - quarantine manifest");
        sb.AppendLine("Created : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("Moved   : " + moved.Count + " items");
        sb.AppendLine();
        foreach (var m in moved)
        {
            sb.AppendLine("  " + m);
        }

        try
        {
            File.WriteAllText(Path.Combine(dir, "manifest.txt"), sb.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 清单写不出来不影响文件已经移动的事实
        }
    }

    /// <summary>
    /// 在备份目录里放一份可独立运行的还原脚本。这是「可逆」承诺的兜底 ——
    /// 即使本程序丢了，用户也能靠这个脚本把文件拿回来。
    /// </summary>
    private void WriteRestoreScript(string dir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 把被隔离的文件还原回游戏目录。");
        sb.AppendLine("# 由「游戏助手」自动生成。");
        sb.AppendLine("param([string]$GameDir = '" + GameDir.Replace("'", "''") + "')");
        sb.AppendLine("$Backup = '" + dir.Replace("'", "''") + "'");
        sb.AppendLine("$manifest = Join-Path $Backup 'manifest.txt'");
        sb.AppendLine("if (-not (Test-Path $manifest)) { Write-Host '找不到 manifest.txt'; exit 1 }");
        sb.AppendLine("$items = Get-Content $manifest | Where-Object { $_ -like '  *' } | ForEach-Object { $_.Substring(2) }");
        sb.AppendLine("foreach ($rel in $items) {");
        sb.AppendLine("    $from = Join-Path $Backup $rel");
        sb.AppendLine("    $to   = Join-Path $GameDir $rel");
        sb.AppendLine("    if (-not (Test-Path -LiteralPath $from)) { continue }");
        sb.AppendLine("    if (Test-Path -LiteralPath $to) { Write-Host ('已存在，跳过: ' + $rel); continue }");
        sb.AppendLine("    $parent = Split-Path -Parent $to");
        sb.AppendLine("    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }");
        sb.AppendLine("    Move-Item -LiteralPath $from -Destination $to -Force");
        sb.AppendLine("    Write-Host ('已还原: ' + $rel)");
        sb.AppendLine("}");
        sb.AppendLine("Write-Host '完成。'");

        try
        {
            File.WriteAllText(Path.Combine(dir, "restore.ps1"), sb.ToString(), new UTF8Encoding(true));
        }
        catch
        {
            // 忽略
        }
    }

    private static long GetSizeBytes(string path, bool isDir)
    {
        try
        {
            if (!isDir)
            {
                return new FileInfo(path).Length;
            }

            long sum = 0;
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    sum += new FileInfo(f).Length;
                }
                catch
                {
                    // 单个文件读不到就跳过
                }
            }
            return sum;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 标记校验。目录看子项是否存在，文件看内容是否包含指定子串（忽略大小写）。
    /// </summary>
    public static bool VerifyMarker(string full, char kind, string marker)
    {
        if (string.IsNullOrEmpty(marker))
        {
            return true;
        }

        if (kind == 'D')
        {
            return Directory.Exists(Path.Combine(full, marker)) || File.Exists(Path.Combine(full, marker));
        }

        string text = ReadTextSafe(full);
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.Contains(marker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 读文本，按 BOM 判断编码。
    ///
    /// 这一步不能省：两个 .reg 目标是 UTF-16LE 编码，如果当 UTF-8 读，
    /// 内容里全是 NUL 字节，标记永远匹配不上，于是这两项会被误判成
    /// 「需人工核对」而一直清不掉。脚本版第一轮就是这么失败的。
    /// </summary>
    private static string ReadTextSafe(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }
}
