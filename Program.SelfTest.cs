using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using GameHelper.Core;
using GameHelper.UI;

namespace GameHelper;

/// <summary>
/// 无界面自检与界面构建检查。
///
/// 存在的意义很实际：WinForms 程序「能编译」和「能跑起来」是两件事 ——
/// 控件重叠、字体缺失、P/Invoke 结构体布局错位，都要到运行时才暴露。
/// 这两个模式让「跑一遍看看」不需要有人坐在屏幕前点鼠标。
///
///   --selftest   采集一轮真实数据并输出报告（不创建窗口）
///   --dryrun     构建全部界面控件但不显示窗口，输出结构检查结果
/// </summary>
internal static class SelfTest
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleOutputCP(uint wCodePageId);

    private const int StdOutputHandle = -11;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(
        IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    private const int AttachParentProcess = -1;

    // ------------------------------------------------------------------ 采集自检

    internal static void Run(string[] args)
    {
        var sb = new StringBuilder();
        bool allOk = true;

        sb.AppendLine("游戏助手 · 自检报告");
        sb.AppendLine(new string('=', 64));
        sb.AppendLine("时间      : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("版本      : " + Program.AppVersion);
        sb.AppendLine("运行库    : .NET " + Environment.Version);
        sb.AppendLine("系统      : " + RuntimeInformation.OSDescription);
        sb.AppendLine("进程架构  : " + RuntimeInformation.ProcessArchitecture);
        sb.AppendLine("64 位系统 : " + Environment.Is64BitOperatingSystem);
        sb.AppendLine("输出目录  : " + AppPaths.OutputDir);
        sb.AppendLine("CPU       : " + Inventory.CpuName());
        sb.AppendLine();

        // ---- nvidia-smi ----
        sb.AppendLine("【显卡数据源】");
        string? smi = NvidiaSmi.Path;
        sb.AppendLine("  nvidia-smi     : " + (smi ?? "未找到"));
        var gpu = NvidiaSmi.Query();
        if (gpu.HasData)
        {
            sb.AppendLine("  显卡           : " + gpu.Name);
            sb.AppendLine($"  显存           : {gpu.MemUsedMb:0} / {gpu.MemTotalMb:0} MB ({gpu.Pct:0.0}%)");
            sb.AppendLine($"  温度 / 占用    : {gpu.TempC:0} C / {gpu.UtilPct:0} %");
            sb.AppendLine($"  频率 / 功耗    : {gpu.GpuClockMhz:0} MHz / {gpu.PowerW:0} W");
            Check(sb, "显卡数据读取", true, ref allOk);
        }
        else
        {
            sb.AppendLine("  !! 未能读到显卡数据");
            Check(sb, "显卡数据读取", false, ref allOk);
        }
        sb.AppendLine();

        // ---- 适配器 ----
        sb.AppendLine("【GPU 适配器（DXGI 枚举）】");
        var adapters = GpuAdapters.Enumerate();
        if (adapters.Count == 0)
        {
            sb.AppendLine("  !! 未能枚举到任何适配器");
            Check(sb, "DXGI 适配器枚举", false, ref allOk);
        }
        else
        {
            var primary = GpuAdapters.FindPrimary(adapters);
            foreach (var a in adapters)
            {
                bool isPrimary = primary is not null && a.Key == primary.Key;
                sb.AppendLine($"  {(isPrimary ? "→" : " ")} {a.Name}");
                sb.AppendLine($"      专用显存 {a.DedicatedVideoMb,7:0} MB   LUID {a.Key}   短名「{a.ShortName}」");
            }
            sb.AppendLine("  主显卡（游戏实际使用的那块）：" + (primary?.Name ?? "未能判定"));
            Check(sb, "DXGI 适配器枚举与主显卡判定", primary is not null, ref allOk);
        }
        sb.AppendLine();

        // ---- PDH 探针 ----
        sb.AppendLine("【进程显存探针（PDH \\GPU Process Memory）】");
        using (var probe = new GpuMemoryProbe())
        {
            sb.AppendLine("  可用           : " + probe.Available);
            if (!probe.Available)
            {
                sb.AppendLine("  原因           : " + (probe.LastError ?? "未知"));
            }

            var raw = probe.ReadRaw();
            sb.AppendLine("  原始实例       : " + raw.Count + " 条（进程 × 显卡）");

            // 按适配器分组。这一步是整份报告里最能看出问题的地方：
            // 如果最大的一组不是主显卡，说明把「可回收显存」算成本机总量会严重虚高。
            var byAdapter = new Dictionary<string, (int Count, double Mb)>(StringComparer.OrdinalIgnoreCase);
            foreach (var inst in raw)
            {
                byAdapter.TryGetValue(inst.AdapterKey, out var cur);
                byAdapter[inst.AdapterKey] = (cur.Count + 1, cur.Mb + inst.LocalMb);
            }

            var primaryAdapter = GpuAdapters.FindPrimary(adapters);
            double primarySumMb = 0;
            double allSumMb = 0;

            sb.AppendLine("  按显卡分组     :");
            foreach (var kv in byAdapter.OrderByDescending(x => x.Value.Mb))
            {
                var match = adapters.FirstOrDefault(a => a.Key.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                string label = match?.ShortName ?? kv.Key;
                bool isPrimary = primaryAdapter is not null && match is not null && match.Key == primaryAdapter.Key;
                if (isPrimary)
                {
                    primarySumMb = kv.Value.Mb;
                }
                allSumMb += kv.Value.Mb;
                sb.AppendLine($"    {(isPrimary ? "→" : " ")} {label,-22} {kv.Value.Count,3} 条   {kv.Value.Mb,9:0.0} MB");
            }

            sb.AppendLine($"  全部显卡合计   : {allSumMb:0.0} MB");
            sb.AppendLine($"  主显卡合计     : {primarySumMb:0.0} MB");
            if (gpu.HasData)
            {
                double diff = gpu.MemUsedMb - primarySumMb;
                sb.AppendLine($"  nvidia-smi 报告: {gpu.MemUsedMb:0} MB");
                sb.AppendLine($"  差额（主显卡）  : {diff:0.0} MB   （"
                              + (diff >= 0
                                  ? "驱动持有、非进程分配等，属正常"
                                  : "计数器略高于 nvidia-smi，属正常波动")
                              + "）");
            }
            sb.AppendLine();

            // 逐进程明细，只列主显卡上的
            var top = raw
                .Where(i => primaryAdapter is null || i.AdapterKey.Equals(primaryAdapter.Key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(i => i.LocalMb)
                .Take(10)
                .ToList();

            if (top.Count > 0)
            {
                sb.AppendLine("  主显卡上占用最多的进程 :");
                foreach (var t in top)
                {
                    string name;
                    try
                    {
                        using var p = Process.GetProcessById(t.Pid);
                        name = p.ProcessName;
                    }
                    catch
                    {
                        name = "pid_" + t.Pid;
                    }
                    sb.AppendLine($"    {name,-30} {t.LocalMb,10:0.0} MB   共享 {t.SharedMb:0.0} MB");
                }
            }

            bool rawOk = probe.Available && raw.Count > 0;
            Check(sb, "PDH 探针可用且读到实例", rawOk, ref allOk);
            if (rawOk && primaryAdapter is not null)
            {
                Check(sb, "主显卡上的占用能被正确归属", primarySumMb > 0 || gpu.MemUsedMb < 5, ref allOk);
            }
        }
        sb.AppendLine();

        // ---- 物理内存 ----
        sb.AppendLine("【系统内存（GlobalMemoryStatusEx）】");
        var (ramUsed, ramTotal) = Native.GetPhysicalMemory();
        sb.AppendLine($"  已用 / 总量    : {ramUsed:0} / {ramTotal:0} MB");
        Check(sb, "物理内存读取", ramTotal > 0, ref allOk);
        sb.AppendLine();

        // ---- 权限与游戏 ----
        sb.AppendLine("【权限与游戏进程】");
        bool admin = Inventory.IsAdmin();
        sb.AppendLine("  管理员权限     : " + admin);

        using (var game = Inventory.FindGame())
        {
            sb.AppendLine("  游戏运行中     : " + (game is not null));
            if (game is not null)
            {
                try
                {
                    sb.AppendLine("  游戏 PID       : " + game.Id);
                    sb.AppendLine($"  游戏物理内存   : {game.WorkingSet64 / 1024.0 / 1024.0:0} MB");
                }
                catch
                {
                    // 忽略
                }
            }
        }

        var blockers = Inventory.FindBlockers();
        sb.AppendLine("  清理门禁阻拦项 : " + (blockers.Count == 0 ? "无" : string.Join("、", blockers)));
        sb.AppendLine("  Steam 运行中   : " + Inventory.IsSteamRunning());
        sb.AppendLine();

        // ---- 游戏目录自动定位（换电脑能不能用，就看这一节） ----
        // 以前这两个目录写死成某一台机器的 G:\...，换台电脑就是错的且不报错。
        // 现在由 GameLocator 现找，这里把它找到的结果如实打出来 ——
        // 在没装游戏的机器上「未找到」是正确结果，不是故障。
        sb.AppendLine("【游戏目录定位（可移植性）】");
        string? located = GameLocator.FindGameDir();
        sb.AppendLine("  自动识别       : " + (located ?? "未找到（本机可能没装这个游戏）"));
        sb.AppendLine("  推断的备份目录 : " + GameLocator.SuggestBackupRoot(located));
        Check(sb, "游戏目录定位不依赖写死的盘符",
            located is null || File.Exists(Path.Combine(located, ProcessClassifier.GameProcessName + ".exe")), ref allOk);

        // 备份目录必须**跟着游戏所在盘走**，而不是回到某个写死的盘符。
        // 这里刻意用「本程序所在盘」而不是写死 D:\ —— 断言不能依赖
        // 运行它的机器上恰好有哪个盘，那会在别人电脑上假失败。
        string here = Path.GetPathRoot(AppPaths.RootDir) ?? string.Empty;
        string bkHere = GameLocator.SuggestBackupRoot(Path.Combine(here, "Steam", "x"));
        bool bkFollowsDrive = here.Length > 1
            && bkHere.StartsWith(here, StringComparison.Ordinal)
            && bkHere.Contains("_naraka_cleanup_backup", StringComparison.Ordinal);

        // 没找到游戏时退回「文档」，必须仍然给出可用路径，不能是空
        string bkNone = GameLocator.SuggestBackupRoot(null);
        bool bkFallback = bkNone.Length > 0
            && bkNone.Contains("清理备份", StringComparison.Ordinal);

        sb.AppendLine($"  备份目录跟盘   : {bkFollowsDrive}   （{bkHere}）");
        sb.AppendLine($"  无游戏时兜底   : {bkFallback}   （{bkNone}）");
        Check(sb, "备份目录自动推断（跟盘 / 兜底到文档）", bkFollowsDrive && bkFallback, ref allOk);
        sb.AppendLine();

        // ---- 清理服务 ----
        sb.AppendLine("【清理服务】");
        var cleaner = new CleanerService
        {
            GameDir = located ?? string.Empty,
            BackupRoot = GameLocator.SuggestBackupRoot(located),
        };
        sb.AppendLine("  清理目标       : " + CleanerService.Targets.Count + " 项");
        sb.AppendLine("  硬保护名单     : " + CleanerService.Protected.Count + " 项");
        sb.AppendLine("  游戏目录       : " + (cleaner.GameDir.Length == 0 ? "（未找到）" : cleaner.GameDir)
                     + "  ->  " + (cleaner.GameDirExists ? "存在" : "不存在"));
        sb.AppendLine("  备份目录       : " + cleaner.BackupRoot + "  ->  " + (cleaner.BackupRootExists ? "存在" : "不存在"));
        sb.AppendLine("  已有备份集     : " + cleaner.ListBackups().Count + " 个");
        Check(sb, "清理目标与保护名单已载入",
            CleanerService.Targets.Count == 26 && CleanerService.Protected.Count > 60, ref allOk);
        sb.AppendLine();

        // ---- 标记校验（不落盘，只验证逻辑） ----
        sb.AppendLine("【标记校验逻辑抽样】");
        string tmpDir = Path.Combine(Path.GetTempPath(), "naraka_selftest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tmpDir);
            string sub = Path.Combine(tmpDir, "somefolder");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "FUNDING.yml"), "github: x", new UTF8Encoding(false));

            bool dirMarkerOk = CleanerService.VerifyMarker(sub, 'D', "FUNDING.yml");
            bool dirMarkerBad = CleanerService.VerifyMarker(sub, 'D', "no_such_file_xyz");

            string utf16 = Path.Combine(tmpDir, "utf16.reg");
            File.WriteAllText(utf16, "Windows Registry Editor\r\n\"41FCC608\"=dword:00000001\r\n", Encoding.Unicode);
            bool utf16Ok = CleanerService.VerifyMarker(utf16, 'F', "41FCC608");

            string utf8 = Path.Combine(tmpDir, "utf8.txt");
            File.WriteAllText(utf8, "BasedOnStyle: Google\r\nAlignOperands: true\r\n", new UTF8Encoding(false));
            bool utf8Ok = CleanerService.VerifyMarker(utf8, 'F', "AlignOperands");
            bool utf8Bad = CleanerService.VerifyMarker(utf8, 'F', "not_in_this_file");

            sb.AppendLine("  目录标记命中   : " + dirMarkerOk);
            sb.AppendLine("  目录标记未命中 : " + (!dirMarkerBad));
            sb.AppendLine("  UTF-16 文件标记: " + utf16Ok + "   （这一步是脚本版第一轮失败的地方）");
            sb.AppendLine("  UTF-8 文件标记 : " + utf8Ok);
            sb.AppendLine("  标记不匹配拦截 : " + (!utf8Bad));

            Check(sb, "标记校验（含 UTF-16 BOM 识别）",
                dirMarkerOk && !dirMarkerBad && utf16Ok && utf8Ok && !utf8Bad, ref allOk);
        }
        catch (Exception ex)
        {
            sb.AppendLine("  抽样失败：" + ex.Message);
            Check(sb, "标记校验（含 UTF-16 BOM 识别）", false, ref allOk);
        }
        finally
        {
            try
            {
                Directory.Delete(tmpDir, recursive: true);
            }
            catch
            {
                // 忽略
            }
        }
        sb.AppendLine();

        // ---- 显存提醒策略（VramNotifier 边界值） ----
        // 提醒是「不说话则以，一说话就打扰人」的功能，所以真正需要测的不是
        // 「会不会提醒」，而是「会不会多提醒」。下面几条全部是防轰炸规则的边界。
        sb.AppendLine("【显存提醒策略（VramNotifier）】");
        bool notifyOk = RunNotifierChecks(sb);
        Check(sb, "显存提醒的升档 / 迟滞 / 冷却 / 静默", notifyOk, ref allOk);
        sb.AppendLine();

        // ---- 帧数采集（FrameTracker 解析与统计） ----
        // 这一批断言存在的理由：帧数是「越玩越卡到底是不是显存问题」的
        // 唯一直接证据，而算错的帧数不会崩、只会安静地给出一个看着合理的
        // 假数字 —— 那比没有数据更危险。
        sb.AppendLine("【帧数采集（FrameTracker）】");
        bool frameOk = RunFrameChecks(sb);
        Check(sb, "帧数解析 / 1% Low / 降级文案", frameOk, ref allOk);
        sb.AppendLine();

        // ---- 首次导览的弹出判定 ----
        // 导览是**模态**窗口：该弹没弹 → 用户看不到说明；不该弹却弹了 →
        // 无人值守的自动化会永久卡死（截图流程就踩过一次）。
        // 两个方向都必须锁住。
        sb.AppendLine("【首次启动导览】");
        bool t1 = MainForm.ShouldShowTour(skipFirstRun: false, alreadySeen: false);
        bool t2 = !MainForm.ShouldShowTour(skipFirstRun: false, alreadySeen: true);
        bool t3 = !MainForm.ShouldShowTour(skipFirstRun: true, alreadySeen: false);
        bool t4 = !MainForm.ShouldShowTour(skipFirstRun: true, alreadySeen: true);
        sb.AppendLine($"  全新电脑应弹一次 : {t1}");
        sb.AppendLine($"  看过之后不再打扰 : {t2}");
        sb.AppendLine($"  自动化模式不弹   : {t3}   （模态窗口无人关闭会卡死）");
        sb.AppendLine($"  自动化 + 已看过  : {t4}");

        // 导览窗口本身的默认状态：默认就该「只弹一次」
        bool t5;
        try
        {
            using var tour = new AboutForm(tour: true);
            t5 = tour.DontShowAgain;
        }
        catch
        {
            t5 = false;
        }
        sb.AppendLine($"  导览默认只弹一次 : {t5}");

        Check(sb, "首次导览的弹出判定（含自动化不弹）", t1 && t2 && t3 && t4 && t5, ref allOk);
        sb.AppendLine();

        // ---- 汇总口径 ----
        sb.AppendLine("【汇总判定】");
        bool sumOk = RunSummaryChecks(sb);
        Check(sb, "分位数 / 三段趋势 / 时长与告警时序", sumOk, ref allOk);
        sb.AppendLine();

        // ---- 对局拆分 ----
        sb.AppendLine("【对局拆分】");
        bool matchOk = RunMatchChecks(sb);
        Check(sb, "切局 / 过场剔除 / 局内判读", matchOk, ref allOk);
        sb.AppendLine();

        // ---- 监控目标（其他游戏）----
        sb.AppendLine("【监控目标】");
        bool targetOk = RunTargetChecks(sb);
        Check(sb, "外部目标：判定闭嘴 / 观测照报 / 候选过滤", targetOk, ref allOk);
        sb.AppendLine();

        // ---- 汇总 ----
        sb.AppendLine(new string('=', 64));
        sb.AppendLine(allOk ? "自检完成：全部通过。" : "自检完成：存在未通过项，见上方标记 !! 的行。");
        sb.AppendLine();
        AppendExternalSample(sb);

        Emit(sb.ToString(), "自检报告.txt");
    }

    // ------------------------------------------------------------------ 提醒策略自检

    /// <summary>
    /// VramNotifier 的全部边界用例。返回是否全部通过。
    ///
    /// 拆成独立方法而不是塞进 Run()：这一段没有 I/O、没有环境依赖，
    /// 纯逻辑，是最该被反复跑的一批断言。
    /// </summary>
    private static bool RunNotifierChecks(StringBuilder sb)
    {
        bool ok = true;

        // 先把 Cooldown 归零，把「冷却」这一层单独摘出去 ——
        // 冷却和迟滞都会挡住提醒，混在一起测就分不清是哪一条在起作用。
        var n = new VramNotifier
        {
            WarnPct = 80,
            CritPct = 90,
            ReleaseMargin = 3,
            Cooldown = TimeSpan.Zero,
        };
        int warn = 0;
        int crit = 0;
        n.Alert += (_, _, isCrit) =>
        {
            if (isCrit)
            {
                crit++;
            }
            else
            {
                warn++;
            }
        };

        // 1. 正常区：一声不吭
        n.Update(50, 1600, 3200);
        bool a1 = warn == 0 && crit == 0 && n.Level == VramState.Ok;

        // 2. 首次越过警告线：提醒一次
        n.Update(82, 2624, 3200);
        bool a2 = warn == 1 && crit == 0 && n.Level == VramState.Warn;

        // 3. 阈值线上横跳：82 -> 83 -> 79 -> 82。
        //    79 还没跌破「80 - 3 = 77」，所以不解武装，整段不该再多一条。
        n.Update(83, 2656, 3200);
        n.Update(79, 2528, 3200);
        n.Update(82, 2624, 3200);
        bool a3 = warn == 1;

        // 4. 真正跌回安全区后重新武装，再越线能再提醒一次
        n.Update(50, 1600, 3200);
        n.Update(82, 2624, 3200);
        bool a4 = warn == 2;

        // 5. 直接冲进危急：只发危急，不补一条「正在接近上限」
        n.Update(95, 3040, 3200);
        bool a5 = crit == 1 && warn == 2 && n.Level == VramState.Crit;

        // 6. 回落到 85（仍在警告区，且没跌破 87）：不该补发警告
        n.Update(85, 2720, 3200);
        bool a6 = warn == 2 && crit == 1;

        // 7. 再一次冲进危急：上一步 85 <= 87 已重新武装，所以这次该发
        n.Update(95, 3040, 3200);
        bool a7 = crit == 2;

        // 8. 关掉开关：完全静默，但档位仍要算对（界面横幅还要显示颜色）
        var off = new VramNotifier { WarnPct = 80, CritPct = 90, Cooldown = TimeSpan.Zero, Enabled = false };
        int offFired = 0;
        off.Alert += (_, _, _) => offFired++;
        off.Update(95, 3040, 3200);
        bool a8 = offFired == 0 && off.Level == VramState.Crit;

        // 9. 冷却：同一档位在冷却期内只发一次
        var cd = new VramNotifier
        {
            WarnPct = 80,
            CritPct = 90,
            ReleaseMargin = 3,
            Cooldown = TimeSpan.FromMinutes(5),
        };
        int cdWarn = 0;
        cd.Alert += (_, _, _) => cdWarn++;
        cd.Update(82, 2624, 3200);
        cd.Update(50, 1600, 3200);   // 重新武装
        cd.Update(82, 2624, 3200);   // 冷却期内，应被挡住
        bool a9 = cdWarn == 1;

        // 10. Reset() 清掉冷却与武装状态，让下一次评估从头开始
        cd.Reset();
        cd.Update(82, 2624, 3200);
        bool a10 = cdWarn == 2;

        // 11. 没有显卡数据时喂快照：什么都不该发生，也不该抛
        var blank = new VramNotifier { Cooldown = TimeSpan.Zero };
        int blankFired = 0;
        blank.Alert += (_, _, _) => blankFired++;
        blank.Update(Snapshot.Empty);
        bool a11 = blankFired == 0;

        sb.AppendLine($"  正常区静默     : {a1}");
        sb.AppendLine($"  首次越线提醒   : {a2}");
        sb.AppendLine($"  阈值线上横跳   : {a3}   （79 未跌破 77，不解武装）");
        sb.AppendLine($"  回落后再武装   : {a4}");
        sb.AppendLine($"  告急不补警告   : {a5}");
        sb.AppendLine($"  降档不补提醒   : {a6}");
        sb.AppendLine($"  告急可重新触发 : {a7}");
        sb.AppendLine($"  关闭后完全静默 : {a8}   （档位仍算对，横幅才有颜色）");
        sb.AppendLine($"  冷却期内不重发 : {a9}");
        sb.AppendLine($"  Reset 后重开   : {a10}");
        sb.AppendLine($"  无显卡数据      : {a11}");

        ok &= a1 && a2 && a3 && a4 && a5 && a6 && a7 && a8 && a9 && a10 && a11;
        return ok;
    }

    // ------------------------------------------------------------------ 帧数自检

    /// <summary>
    /// FrameTracker 的解析与统计断言。返回是否全部通过。
    ///
    /// 这里**故意不真正启动 PresentMon**：启动会创建一个内核 ETW 会话，
    /// 需要管理员权限，而且是对系统有副作用的事 —— 自检不该做这种事。
    /// 所以真正被验证的是三件在无权限环境下也能验的事：
    ///   ① 解析规则（按列名定位、丢弃异常间隔、无表头不瞎猜）
    ///   ② 统计算法（1% Low 到底怎么算、样本不足时给什么）
    ///   ③ 失败文案（用实测抓到的报错原文，保证给出的是可照做的建议）
    /// 这三处恰恰是最容易写错、且错了不报错的地方。
    ///
    /// 覆盖不到的一点：真实输出是 UTF-16LE 带 BOM，这里的样本是已解码的字符串。
    /// 编码那一环只能靠真跑一次验证 —— 见 <see cref="FpsProbe"/>。
    /// </summary>
    private static bool RunFrameChecks(StringBuilder sb)
    {
        // 1) 等间隔 10ms → 100fps，且 1% Low 也是 100
        var even = new double[200];
        Array.Fill(even, 10.0);
        var r1 = FrameTracker.Compute(even, even);
        bool a1 = Math.Abs(r1.Fps - 100) < 0.5 && Math.Abs(r1.Low - 100) < 0.5;

        // 2) 平均漂亮、1% Low 难看 —— 这正是「卡」的指纹。
        //    990 帧 10ms（100fps）+ 最慢的 10 帧 100ms → 1% Low = 10fps。
        //    慢帧放在**开头**（模拟「前半段卡、后段恢复」），这样「最近 90 帧」
        //    全是快帧，用来检验两个指标确实各算各的、没有互相串味。
        var spiky = new double[1000];
        for (int i = 0; i < 10; i++)
        {
            spiky[i] = 100.0;
        }
        for (int i = 10; i < 1000; i++)
        {
            spiky[i] = 10.0;
        }
        var recent = new double[90];
        Array.Copy(spiky, 910, recent, 0, 90);
        var r2 = FrameTracker.Compute(recent, spiky);
        bool a2 = Math.Abs(r2.Fps - 100) < 1 && Math.Abs(r2.Low - 10) < 1;

        // 3) 样本不足 100 帧：1% Low 给 0（不拿两三帧编一个极值），帧率照算
        var few = new double[30];
        Array.Fill(few, 20.0);
        var r3 = FrameTracker.Compute(few, few);
        bool a3 = Math.Abs(r3.Fps - 50) < 1 && r3.Low == 0;

        // 4) 空数据不抛，也不返回 NaN（NaN 会让界面画出一堆问号）
        var r4 = FrameTracker.Compute(Array.Empty<double>(), Array.Empty<double>());
        bool a4 = r4.Fps == 0 && r4.Low == 0;

        // 5) 按列名定位 MsBetweenPresents，而不是死认第 3 列。
        //    这里把该列放在第 3 位（索引 2），再把列序调换一次验证仍然正确。
        var ft = new FrameTracker();
        ft.OnLine("Application,ProcessID,MsBetweenPresents,MsBetweenDisplayChange");
        for (int i = 0; i < 120; i++)
        {
            ft.OnLine("game.exe,1234,16.666,16.666");
        }
        bool a5 = ft.TotalFrames == 120;

        var ft2 = new FrameTracker();
        ft2.OnLine("Application,ProcessID,MsBetweenDisplayChange,MsBetweenPresents");
        for (int i = 0; i < 120; i++)
        {
            ft2.OnLine("game.exe,1234,16.666,8.333");
        }
        bool a6 = ft2.TotalFrames == 120;

        // 6) 超长帧间隔（切桌面、读盘）必须丢弃，否则会把峰值拉歪
        var ft3 = new FrameTracker();
        ft3.OnLine("Application,ProcessID,MsBetweenPresents,X");
        for (int i = 0; i < 10; i++)
        {
            ft3.OnLine("g.exe,1,16.0,16.0");
        }
        ft3.OnLine("g.exe,1,5000,5000");     // 5 秒：不是一个连续帧
        ft3.OnLine("g.exe,1,0,0");           // 0：非法
        ft3.OnLine("g.exe,1,-1,-1");         // 负数：非法
        bool a7 = ft3.TotalFrames == 10;

        // 7) 没有表头时不瞎猜列号，宁可一个都不收
        var ft4 = new FrameTracker();
        ft4.OnLine("game.exe,1234,16.666,16.666");
        ft4.OnLine("game.exe,1234,16.666,16.666");
        bool a8 = ft4.TotalFrames == 0;

        // 8) 没启动时 Read() 必须给「不可用 + 原因」，绝不能给 0 fps ——
        //    0 fps 会被后面当成一次真实测量，那是编数据。
        var st = new FrameTracker().Read();
        bool a9 = !st.Available && !string.IsNullOrEmpty(st.Error) && st.Fps == 0;

        // 9) pid 非法时 Start 直接失败，不去启动进程（自检不能有副作用）
        var ft5 = new FrameTracker();
        bool neg = ft5.Start(0);
        bool a10 = !neg && !ft5.Running;

        // 10) 退出原因映射。用的是**实测原文**（PresentMon 1.10，非管理员运行，
        //     退出码 6）—— 这条断言保证用户看到的是一句能照做的建议，
        //     而不是没用的「未知原因」。
        const string realFail =
            "warning: PresentMon requires elevated privilege in order to query processes\r\n"
            + "error: failed to start trace session: access denied.";
        bool a11 = FrameTracker.DescribeExit(realFail)
            .Contains("管理员", StringComparison.Ordinal);

        // 空 stderr（异步回调没送达）也要有可读的兜底，不能是空白
        bool a12 = FrameTracker.DescribeExit(string.Empty)
            .Contains("未知原因", StringComparison.Ordinal);

        // 其它未知报错要原样带出来，方便排查
        bool a13 = FrameTracker.DescribeExit("boom").Contains("boom", StringComparison.Ordinal);

        // 11) FindPresentMon 的契约：**返回什么，那个文件就必须真的存在**。
        //     发布时漏带 PresentMon 是最容易犯的错，犯了以后帧数会一直
        //     静默不可用 —— 界面不报错，只是永远没数据。
        string? found = FrameTracker.FindPresentMon();
        bool a14 = found is null || File.Exists(found);

        // 自定义路径写错时必须优雅降级到其余候选，而不是直接放弃
        string? bogus = FrameTracker.FindPresentMon(@"Z:\definitely-missing\PresentMon-x64.exe");
        bool a15 = bogus is null || File.Exists(bogus);

        sb.AppendLine($"  等间隔 10ms=100fps : {a1}");
        sb.AppendLine($"  平均100/1%Low 10   : {a2}   （慢帧在前，检验两指标各算各的）");
        sb.AppendLine($"  样本<100 不编极值  : {a3}");
        sb.AppendLine($"  空数据不返回 NaN   : {a4}");
        sb.AppendLine($"  按列名定位帧间隔   : {a5}");
        sb.AppendLine($"  列序调换仍正确     : {a6}");
        sb.AppendLine($"  丢弃超长/非法间隔  : {a7}");
        sb.AppendLine($"  无表头不瞎猜列号   : {a8}");
        sb.AppendLine($"  未启动即不可用     : {a9}");
        sb.AppendLine($"  非法 pid 不启动    : {a10}");
        sb.AppendLine($"  权限失败给出建议   : {a11}   （用实测报错原文）");
        sb.AppendLine($"  空报错有兜底文案   : {a12}");
        sb.AppendLine($"  未知报错原样带出   : {a13}");
        sb.AppendLine($"  找到的路径确实存在 : {a14}");
        sb.AppendLine($"  错误自定义路径降级 : {a15}");
        sb.AppendLine($"  PresentMon 实际位置: {(found is null ? "未找到（帧数将不可用！）" : found)}");

        return a1 && a2 && a3 && a4 && a5 && a6 && a7 && a8
            && a9 && a10 && a11 && a12 && a13 && a14 && a15;
    }

    // ------------------------------------------------------------------ 帧数端到端探测

    /// <summary>
    /// 真挂一次 PresentMon，看能不能读到帧。
    ///
    /// 这是唯一能证明「整条链路通」的检查：权限、PresentMon 是否在位、
    /// 输出编码、列名定位，任何一环出问题都只表现为「帧数一直不可用」。
    /// 静态断言查不出这类问题 —— 只有真跑一次才算数。
    /// </summary>
    internal static void FpsProbe(int seconds)
    {
        string report = BuildFpsProbeReport(seconds);

        // 只走 Emit 这一条通道 —— 它内部用 WriteFile 直写 UTF-8 字节。
        // 别在这里再 Console.WriteLine 一遍：GUI 子系统进程的控制台
        // 代码页多半不是 UTF-8，中文会变成乱码（实测过）。
        Emit(report, "帧数探测.txt");
    }

    private static string BuildFpsProbeReport(int seconds)
    {
        var sb = new StringBuilder();
        sb.AppendLine("游戏助手 - 帧数采集探测");
        sb.AppendLine(new string('=', 64));

        bool admin = Inventory.IsAdmin();
        sb.AppendLine("管理员权限 : " + (admin ? "是" : "否（PresentMon 需要它才能启动采集）"));

        string? pm = FrameTracker.FindPresentMon();
        sb.AppendLine("PresentMon : " + (pm ?? "未找到 —— 帧数将不可用"));
        sb.AppendLine();

        var target = PickPresentingProcess();
        if (target is null)
        {
            sb.AppendLine("找不到可探测的目标进程（游戏 / dwm / explorer 都没在跑）。");
            return sb.ToString();
        }

        sb.AppendLine("探测目标   : " + target.Value.Name + "（PID " + target.Value.Pid + "）");
        sb.AppendLine("探测时长   : " + seconds + " 秒");
        sb.AppendLine();

        int last = 0;
        string? failure = null;

        using (var ft = new FrameTracker())
        {
            if (!ft.Start(target.Value.Pid))
            {
                failure = ft.Error ?? "启动采集失败";
                sb.AppendLine("启动失败：" + failure);
            }
            else
            {
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    Thread.Sleep(1000);
                    var s = ft.Read();

                    sb.AppendLine($"  {sw.Elapsed.TotalSeconds,3:0} 秒  收到 {s.Frames,5} 帧"
                        + (s.Fps > 0
                            ? $"，当前 {s.Fps:0.0} fps" +
                              (s.Fps1Low > 0 ? $"，1% Low {s.Fps1Low:0.0}" : string.Empty)
                            : string.Empty));

                    last = s.Frames;

                    if (!s.Available)
                    {
                        // 已经定性失败，再等下去只是浪费时间
                        failure = s.Error ?? "采集不可用";
                        sb.AppendLine();
                        sb.AppendLine("采集中断：" + failure);
                        break;
                    }
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine(new string('-', 64));
        if (last > 0)
        {
            sb.AppendLine($"结论：帧数采集**可用**，共解析到 {last} 帧。");
        }
        else
        {
            sb.AppendLine("结论：帧数采集**不可用**。");
            if (failure is not null)
            {
                sb.AppendLine("原因：" + failure);
            }
            else
            {
                sb.AppendLine("PresentMon 在跑，但一帧都没解析出来。可能是目标进程没有呈现画面，");
                sb.AppendLine("或 PresentMon 的输出列名变了（可用 tools\\PresentMon-x64.exe -h 核对）。");
            }
        }

        return sb.ToString();
    }

    /// <summary>挑一个必定在呈现画面的进程当探测目标。</summary>
    private static (string Name, int Pid)? PickPresentingProcess()
    {
        // 游戏优先 —— 那才是真正想测的对象；其次是桌面合成器。
        foreach (string name in new[] { "NarakaBladepoint", "dwm", "explorer" })
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length == 0)
                {
                    continue;
                }

                int pid = procs[0].Id;
                foreach (var p in procs)
                {
                    p.Dispose();
                }

                return (name + ".exe", pid);
            }
            catch
            {
                // 权限不足等，换下一个
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ 界面构建检查

    internal static void DryRun(MainForm form)
    {
        string report = form.BuildDryRunReport();
        Emit(report, "界面检查报告.txt");
    }

    // ------------------------------------------------------------------ 输出

    private static void Check(StringBuilder sb, string name, bool ok, ref bool allOk)
    {
        if (!ok)
        {
            allOk = false;
        }
        sb.AppendLine($"  [{(ok ? "通过" : "失败")}] {name}");
    }

    /// <summary>
    /// 对局拆分的断言。
    ///
    /// 两个阈值（切局 1200 MB、在打 60% GPU）是在**一份**日志上标定的，
    /// 换台机器就可能不准。断言锁不住「阈值对不对」，
    /// 只能锁住「机制没写错」：切得开、切不多、短段会被跳过、
    /// 单局不给趋势结论、局内下降能被认出来。
    /// </summary>
    private static bool RunMatchChecks(StringBuilder sb)
    {
        // 切得开：3 局各 100 行，内存每次掉 5500 MB
        var three = FakeRows(3, 100, (m, i) => 120);
        var r3 = MatchAnalyzer.Analyze(three);
        bool a1 = r3.Matches.Count == 3 && r3.Matches.All(m => m.Samples == 100);
        bool a2 = r3.Enough;
        sb.AppendLine($"  切出 3 局且样本不串 : {a1}   多局才给趋势 : {a2}");

        // 单局不能谈「局与局之间」
        var one = MatchAnalyzer.Analyze(FakeRows(1, 100, (m, i) => 120));
        bool a3 = !one.Enough;
        sb.AppendLine($"  单局不冒充趋势     : {a3}");

        // 切不多：局内内存自然起伏（每次掉 3 MB）不该切
        var drift = new List<SampleRow>();
        for (int i = 0; i < 200; i++)
        {
            drift.Add(new SampleRow(i * 1.5, 6000 - (i * 3 % 300), 2800, 68, 80, 85, 120, 70, true));
        }
        // Split 返回的是边界（含首尾），段数 = 边界数 - 1
        int segs = MatchAnalyzer.Split(drift).Count - 1;
        bool a4 = segs == 1;
        sb.AppendLine($"  局内起伏不误切     : {a4}（切出 {segs} 段）");

        // 在打门槛：GPU 占用 20% 的行（过场）要被剔掉
        var mixed = new List<SampleRow>();
        for (int i = 0; i < 300; i++)
        {
            bool active = i % 2 == 0;
            mixed.Add(new SampleRow(i * 1.5, 6000, 2800, 68, active ? 85 : 20,
                active ? 85 : 20, active ? 120 : 400, 70, true));
        }
        var rm = MatchAnalyzer.Analyze(mixed);
        bool a5 = rm.Matches.Count == 1 && rm.Matches[0].Samples == 150;
        sb.AppendLine($"  过场行被剔除       : {a5}（150 / 300）");

        // 局内单调下降要认出来
        var down = MatchAnalyzer.Analyze(FakeRows(2, 100, (m, i) => 150 - i * 0.6));
        bool a6 = down.QuarterFps[0] > down.QuarterFps[1]
               && down.QuarterFps[1] > down.QuarterFps[2]
               && down.QuarterFps[2] > down.QuarterFps[3];
        var sbDown = new StringBuilder();
        MatchAnalyzer.Append(sbDown, down);
        bool a7 = sbDown.ToString().Contains("一路向下");
        sb.AppendLine($"  局内下降四格递减   : {a6}   判读说一路向下 : {a7}");

        // 局内持平要认出来，不能硬说成变卡
        var even = MatchAnalyzer.Analyze(FakeRows(2, 100, (m, i) => 120));
        var sbEven = new StringBuilder();
        MatchAnalyzer.Append(sbEven, even);
        bool a8 = sbEven.ToString().Contains("基本持平");
        sb.AppendLine($"  局内持平不说成变卡 : {a8}");

        // 太短的段要跳过，不能当成一局
        var tiny = FakeRows(2, 10, (m, i) => 120);
        bool a9 = !MatchAnalyzer.Analyze(tiny).Enough;
        sb.AppendLine($"  过短的段不算一局   : {a9}");

        return a1 && a2 && a3 && a4 && a5 && a6 && a7 && a8 && a9;
    }

    /// <summary>造 N 局采样：局与局之间插一行「游戏内存骤降」当作回大厅。</summary>
    private static List<SampleRow> FakeRows(int matches, int perMatch, Func<int, int, double> fpsAt)
    {
        var rows = new List<SampleRow>();
        double at = 0;
        for (int m = 0; m < matches; m++)
        {
            if (m > 0)
            {
                // 这一行让上一局的内存从 6000 掉到 500 —— 触发切局
                rows.Add(new SampleRow(at, 500, 1500, 36, 60, 0, 0, 0, false));
                at += 1.5;
            }
            for (int i = 0; i < perMatch; i++)
            {
                double fps = fpsAt(m, i);
                rows.Add(new SampleRow(at, 6000, 2800, 68, 80, 85, fps, fps * 0.6, true));
                at += 1.5;
            }
        }
        return rows;
    }

    /// <summary>
    /// 汇总口径的断言 —— 这是「用户看到什么结论」的最后一道关。
    ///
    /// 前面采集错一点，这里就会把错的结论包装成人话讲给用户，而且讲得很自信。
    /// 尤其是「台阶 vs 持续趋势」那条：第一版用相关系数判，把用户那份日志里
    /// 「第一局特别流畅、之后一直持平」判成了「显存持续累积：成立」，
    /// 可那台机器每局回大厅都会释放显存。假结论比没结论更糟。
    /// </summary>
    private static bool RunSummaryChecks(StringBuilder sb)
    {
        var v = new List<double> { 5, 1, 3, 9, 7 };
        bool q1 = MonitorSession.Median(v) == 5;
        bool q2 = MonitorSession.Quantile(v, 0.05) == 1;
        bool q3 = MonitorSession.Quantile(v, 0.95) == 9;
        sb.AppendLine($"  中位数 / 5% / 95% : {MonitorSession.Median(v)} / "
            + $"{MonitorSession.Quantile(v, 0.05)} / {MonitorSession.Quantile(v, 0.95)}");

        var xs = new List<double>();
        var ys = new List<double>();
        var zs = new List<double>();
        for (int i = 0; i < 60; i++)
        {
            xs.Add(i);
            ys.Add(i * 2.0);
            zs.Add(60 - i);
        }
        double? rUp = MonitorSession.Corr(xs, ys);
        double? rDown = MonitorSession.Corr(xs, zs);
        bool r1 = rUp.HasValue && Math.Abs(rUp.Value - 1) < 1e-9;
        bool r2 = rDown.HasValue && Math.Abs(rDown.Value + 1) < 1e-9;

        // 样本太少时不许给结论 —— 在 3 个点上谈趋势等于编数据
        var tiny = new List<double> { 1, 2, 3 };
        bool r3 = MonitorSession.Corr(tiny, tiny) is null;
        sb.AppendLine($"  相关性 同向/反向/样本过少 : {(rUp?.ToString("0.00") ?? "null")} / "
            + $"{(rDown?.ToString("0.00") ?? "null")} / {(r3 ? "不给结论" : "仍然给了结论")}");

        var seq = new List<double>();
        for (int i = 0; i < 300; i++)
        {
            seq.Add(i);
        }
        var (a, b, c) = MonitorSession.Thirds(seq);
        bool t1 = a == 50 && b == 150 && c == 250;
        sb.AppendLine($"  三段拆分（0..299）: {a} / {b} / {c}");

        // 台阶型：前段低，之后持平 —— 应当判「不成立」，但落差要如实写出来
        string step = BuildFakeSummary(i => i < 100 ? 50 : 70, i => i < 100 ? 140 : 120, 300);
        bool s1 = step.Contains("[不成立] " + MonitorSession.NameVramClimb);
        bool s2 = step.Contains("[不成立] " + MonitorSession.NameDecay);
        bool s3 = step.Contains("一次性抬升") && step.Contains("后半程基本持平");
        sb.AppendLine($"  台阶型不误判为累积 : {s1}   不误判为持续下降 : {s2}   落差仍如实说明 : {s3}");

        // 持续型：一路走 —— 这才该判成立
        string climb = BuildFakeSummary(i => 40 + i * 30.0 / 300, i => 150 - i * 60.0 / 300, 300);
        bool c1 = climb.Contains("[成立] " + MonitorSession.NameVramClimb);
        bool c2 = climb.Contains("[成立] " + MonitorSession.NameDecay);
        sb.AppendLine($"  持续上升能判出累积 : {c1}   持续下降能判出越玩越卡 : {c2}");

        bool timing = CheckTiming(sb);

        return q1 && q2 && q3 && r1 && r2 && r3 && t1 && s1 && s2 && s3 && c1 && c2 && timing;
    }

    /// <summary>用给定的两条曲线喂一遍 MonitorSession，取回汇总文本。</summary>
    private static string BuildFakeSummary(Func<int, double> pctAt, Func<int, double> fpsAt, int n)
    {
        var s = new MonitorSession();
        string tmp = Path.Combine(Path.GetTempPath(), "naraka-sum-" + Guid.NewGuid().ToString("N"));
        s.OutputDir = tmp;
        s.StartReplay(new DateTime(2026, 1, 1, 12, 0, 0));
        try
        {
            for (int i = 0; i < n; i++)
            {
                double pct = pctAt(i);
                double fps = fpsAt(i);
                var gpu = new GpuInfo(string.Empty, 65, 85, pct * 40.96, 4096, 1850, 6000, 55);
                var snap = new Snapshot(
                    At: DateTime.Now,
                    Gpu: gpu,
                    Procs: Array.Empty<ProcVram>(),
                    RamUsedMb: 16000,
                    RamTotalMb: 32000,
                    Throttle: "none",
                    GameRunning: true,
                    GameStartedAt: null,
                    GameRamMb: 6000,
                    Frame: new FrameStats(true, fps, fps * 0.5, 900, null));

                s.ReplayElapsedSec = i * 1.5;
                s.Record(snap);
            }
            return s.BuildSummary();
        }
        finally
        {
            s.Stop();
            try
            {
                Directory.Delete(tmp, recursive: true);
            }
            catch
            {
                // 临时目录删不掉不影响自检
            }
        }
    }

    /// <summary>
    /// 用给定的两条曲线喂一遍 MonitorSession，取回汇总文本。
    /// <paramref name="kind"/> 指定监控目标口径 —— 用来验证「换游戏后判定层闭嘴」。
    /// </summary>
    private static string BuildFakeSummary(
        Func<int, double> pctAt, Func<int, double> fpsAt, int n, TargetKind kind)
    {
        var s = new MonitorSession();
        string tmp = Path.Combine(Path.GetTempPath(), "naraka-sum-" + Guid.NewGuid().ToString("N"));
        s.OutputDir = tmp;
        s.StartReplay(new DateTime(2026, 1, 1, 12, 0, 0));
        try
        {
            for (int i = 0; i < n; i++)
            {
                double pct = pctAt(i);
                double fps = fpsAt(i);
                var gpu = new GpuInfo(string.Empty, 65, 85, pct * 40.96, 4096, 1850, 6000, 55);
                var snap = new Snapshot(
                    At: DateTime.Now,
                    Gpu: gpu,
                    Procs: Array.Empty<ProcVram>(),
                    RamUsedMb: 16000,
                    RamTotalMb: 32000,
                    Throttle: "none",
                    GameRunning: true,
                    GameStartedAt: null,
                    GameRamMb: 6000,
                    Frame: new FrameStats(true, fps, fps * 0.5, 900, null),
                    Kind: kind);

                s.ReplayElapsedSec = i * 1.5;
                s.Record(snap);
            }
            return s.BuildSummary();
        }
        finally
        {
            s.Stop();
            try
            {
                Directory.Delete(tmp, recursive: true);
            }
            catch
            {
                // 临时目录删不掉不影响自检
            }
        }
    }

    /// <summary>
    /// 监控目标（其他游戏）的断言。
    ///
    /// 这一节锁的是**一条不能越过的线**：换游戏之后，为《永劫无间》标定出来的
    /// 判定和建议一个字都不许出现。越过去了就是「安静地给错结论」——
    /// 用户会拿着「帧率上限设到 117」这种话去调 CS2，还以为是工具算出来的。
    /// </summary>
    private static bool RunTargetChecks(StringBuilder sb)
    {
        // 同一套曲线，两种口径 —— 只有 Kind 不同，其它完全一致。
        // 这样比对出来的任何差异都只能归因于口径，排除数据因素的干扰。
        Func<int, double> pctAt = i => 40 + i * 30.0 / 300;
        Func<int, double> fpsAt = i => 150 - i * 60.0 / 300;

        string naraka = BuildFakeSummary(pctAt, fpsAt, 300, TargetKind.Naraka);
        string ext = BuildFakeSummary(pctAt, fpsAt, 300, TargetKind.External);

        // 1. 外部口径：判定标记一个都不许有
        bool noVerdict = !ext.Contains("[成立]") && !ext.Contains("[不成立]")
                      && !ext.Contains("[存疑]");
        sb.AppendLine($"  外部口径不给任何判定 : {noVerdict}");

        // 2. 永劫无间口径照常给判定（回归保护 —— 不能为了闭嘴把自家功能也关掉）
        bool stillVerdicts = naraka.Contains("[成立]") || naraka.Contains("[不成立]");
        sb.AppendLine($"  永劫无间口径判定照旧 : {stillVerdicts}");

        // 3. 永劫无间专属的建议节，外部口径一个都不能出现。
        //    查节标题而不是查建议里的词 —— 解释段里为了说明「为什么不给建议」，
        //    会引用这些词，按词面匹配就会把正常的解释误判成建议外泄。
        //    节标题是结构标记，不会在正文里偶然出现。
        bool noAdvice = !ext.Contains("针对这一轮") && !ext.Contains("DXCache");
        sb.AppendLine($"  永劫无间专属建议不外泄 : {noAdvice}");

        // 4. 对局拆分也不许出现（切局阈值是永劫无间的）
        bool noMatch = !ext.Contains("对局拆分") && !ext.Contains("局内四分位");
        sb.AppendLine($"  对局拆分不用于外部口径 : {noMatch}");

        // 5. 但原始观测必须还在 —— 闭嘴不等于什么都不报
        bool rawKept = ext.Contains("帧率中位") && ext.Contains("1% Low")
                    && ext.Contains("显存") && ext.Contains("温度中位");
        sb.AppendLine($"  原始观测值照常输出 : {rawKept}");

        // 6. 必须解释「为什么没有结论」，否则用户会以为工具坏了
        bool explains = ext.Contains("为什么这一份没有") && ext.Contains("标定");
        sb.AppendLine($"  说明了不给结论的原因 : {explains}");

        // 7. 两种口径都要守住「不列卡顿占比」
        bool noStutterPct = !ext.Contains("卡顿占比：") && !naraka.Contains("卡顿占比：");
        sb.AppendLine($"  两边都不列卡顿占比 : {noStutterPct}");

        // 8. 反作弊边界必须如实说明（这是用户最在意的一条）
        bool acHonest = ext.Contains("不触碰反作弊") || ext.Contains("不注入");
        sb.AppendLine($"  如实说明反作弊边界 : {acHonest}");

        // 9. Snapshot 的口径默认值必须是 Naraka ——
        //    否则回放旧日志（CSV 里没有这一列）会被误判成外部口径而闭嘴。
        var legacy = new Snapshot(
            DateTime.Now, GpuInfo.Empty, Array.Empty<ProcVram>(), 0, 0, "none", false, null, 0);
        bool defaultNaraka = legacy.NarakaScope && legacy.Kind == TargetKind.Naraka;
        sb.AppendLine($"  缺省口径为永劫无间 : {defaultNaraka}");

        return noVerdict && stillVerdicts && noAdvice && noMatch && rawKept
            && explains && noStutterPct && acHonest && defaultNaraka;
    }

    /// <summary>
    /// 外部口径的完整样例输出，附在自检报告末尾。
    ///
    /// 上面的断言只验证「有 / 没有」，验证不了**读起来是什么样** ——
    /// 断行错位、半句话被截断、该解释的地方没说清，这些都得亲眼看。
    /// 所以把整篇样例原样印出来。
    /// </summary>
    private static void AppendExternalSample(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine(new string('=', 64));
        sb.AppendLine("外部口径样例（同一套数据，仅监控目标不同）");
        sb.AppendLine(new string('=', 64));
        sb.AppendLine(BuildFakeSummary(
            i => 40 + i * 30.0 / 300, i => 150 - i * 60.0 / 300, 300, TargetKind.External));
    }

    /// <summary>时长与告警时序。这两条都是真的在用户日志里露过马脚的。</summary>
    private static bool CheckTiming(StringBuilder sb)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "naraka-tm-" + Guid.NewGuid().ToString("N"));
        var s = new MonitorSession { OutputDir = tmp };
        s.StartReplay(new DateTime(2026, 1, 1, 12, 0, 0));
        double sec;
        DateTime? warn;
        DateTime? crit;
        try
        {
            // 从「正常」一步跳到「危急」：警告时刻必须被同时补记，
            // 否则汇总里会出现「危急比警告还早」的排序。
            double[] pcts = { 50, 95 };
            for (int i = 0; i < pcts.Length; i++)
            {
                var gpu = new GpuInfo(string.Empty, 65, 85, pcts[i] * 40.96, 4096, 1850, 6000, 55);
                var snap = new Snapshot(
                    At: DateTime.Now,
                    Gpu: gpu,
                    Procs: Array.Empty<ProcVram>(),
                    RamUsedMb: 16000,
                    RamTotalMb: 32000,
                    Throttle: "none",
                    GameRunning: true,
                    GameStartedAt: null,
                    GameRamMb: 6000,
                    Frame: new FrameStats(true, 120, 60, 900, null));

                s.ReplayElapsedSec = i * 10;
                s.Record(snap);
            }

            s.Stop();
            // 最后一次采样在第 10 秒 —— 停止之后必须仍然算得出来
            sec = s.Elapsed.TotalSeconds;
            warn = s.FirstWarnAt;
            crit = s.FirstCritAt;
        }
        finally
        {
            try
            {
                Directory.Delete(tmp, recursive: true);
            }
            catch
            {
                // 忽略
            }
        }

        bool okSec = Math.Abs(sec - 10) < 0.01;
        bool okOrder = warn.HasValue && crit.HasValue && warn.Value <= crit.Value;
        sb.AppendLine($"  停止后时长仍正确 : {okSec}（{sec:0} 秒，期望 10）");
        sb.AppendLine($"  危急不早于警告   : {okOrder}（警告 {warn:HH:mm:ss} / 危急 {crit:HH:mm:ss}）");
        return okSec && okOrder;
    }

    /// <summary>
    /// 用**当前**算法对一份已有日志重新生成汇总。
    ///
    /// 存在的理由：汇总口径是会改的。这个工具第一版报「最高帧率 / 最差 1% Low」，
    /// 结果一份真实日志汇总出 695.6 fps 和 1.2 fps —— 两个数字都来自加载过场。
    /// 口径改成中位数之后，用户手里那些旧日志不该就此变废纸：
    /// 重算一遍，就能用新口径看旧数据，也能直接对比两次口径差多少。
    /// </summary>
    internal static void Resummary(string csvPath)
    {
        if (!File.Exists(csvPath))
        {
            Emit("找不到日志文件：" + csvPath, "重算汇总-失败.txt");
            return;
        }

        var lines = new List<string>();
        try
        {
            using var rd = new StreamReader(csvPath, new UTF8Encoding(false), true);
            string? line;
            while ((line = rd.ReadLine()) is not null)
            {
                if (line.Trim().Length > 0)
                {
                    lines.Add(line);
                }
            }
        }
        catch (Exception ex)
        {
            Emit("读不了这份日志：" + ex.Message, "重算汇总-失败.txt");
            return;
        }

        if (lines.Count < 2)
        {
            Emit("这份日志只有表头，没有数据行。", "重算汇总-失败.txt");
            return;
        }

        // 按列名定位，不认列号 —— CSV 加过列，写死下标迟早错位。
        var head = lines[0].Split(',');
        var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < head.Length; i++)
        {
            col[head[i].Trim().TrimStart('\uFEFF')] = i;
        }

        static double Num(string[] r, Dictionary<string, int> map, string name)
        {
            return map.TryGetValue(name, out int i)
                && i < r.Length
                && double.TryParse(r[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                    ? v
                    : 0;
        }

        static string Str(string[] r, Dictionary<string, int> map, string name)
        {
            return map.TryGetValue(name, out int i) && i < r.Length ? r[i].Trim() : string.Empty;
        }

        // 日志里只有 HH:mm:ss，日期借文件的修改日期。
        DateTime day = File.GetLastWriteTime(csvPath).Date;
        DateTime startedAt = day;
        if (TimeSpan.TryParse(Str(lines[1].Split(','), col, "Timestamp"), out TimeSpan t0))
        {
            startedAt = day + t0;
        }

        var session = new MonitorSession();
        string scratch = Path.Combine(Path.GetTempPath(), "naraka-resum-" + Guid.NewGuid().ToString("N"));
        try
        {
            session.OutputDir = scratch;
            session.StartReplay(startedAt);

            int used = 0;
            for (int n = 1; n < lines.Count; n++)
            {
                var r = lines[n].Split(',');
                double fps = Num(r, col, "FPS");
                double low = Num(r, col, "FPS_1Low");
                double frames = Num(r, col, "FrameCount");
                double gameRam = Num(r, col, "GameRAM_MB");

                var gpu = new GpuInfo(
                    string.Empty,
                    Num(r, col, "GPUTemp_C"),
                    Num(r, col, "GPUUtil_pct"),
                    Num(r, col, "VRAMUsed_MB"),
                    Num(r, col, "VRAMTotal_MB"),
                    Num(r, col, "GPUClock_MHz"),
                    Num(r, col, "MemClock_MHz"),
                    Num(r, col, "Power_W"));

                var snap = new Snapshot(
                    At: startedAt.AddSeconds(Num(r, col, "Elapsed_s")),
                    Gpu: gpu,
                    Procs: Array.Empty<ProcVram>(),
                    RamUsedMb: Num(r, col, "RAMUsed_MB"),
                    RamTotalMb: Num(r, col, "RAMTotal_MB"),
                    Throttle: Str(r, col, "Throttle"),
                    GameRunning: gameRam > 0,
                    GameStartedAt: null,
                    GameRamMb: gameRam,
                    Frame: fps > 0
                        ? new FrameStats(true, fps, low, (int)frames, null)
                        : FrameStats.Unavailable);

                session.ReplayElapsedSec = Num(r, col, "Elapsed_s");
                session.Record(snap);
                used++;
            }

            string text = session.BuildSummary();
            // 汇总里的「日志文件」指的是重算时那个临时 CSV，换回用户真正的那一份。
            if (session.CsvPath is not null)
            {
                text = text.Replace(session.CsvPath, csvPath);
            }
            text = "（这是对 " + Path.GetFileName(csvPath)
                + " 用当前算法重新生成的汇总，原文件未改动）\r\n" + text;

            EmitAt(text, Path.ChangeExtension(csvPath, ".重算.txt"));
        }
        finally
        {
            session.Stop();
            try
            {
                if (Directory.Exists(scratch))
                {
                    Directory.Delete(scratch, recursive: true);
                }
            }
            catch
            {
                // 临时目录删不掉不影响结果
            }
        }
    }

    /// <summary>把报告写到指定路径（而不是默认的日志目录），同时送一份到控制台。</summary>
    private static void EmitAt(string text, string fullPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(fullPath, text, new UTF8Encoding(true));
        }
        catch
        {
            // 忽略
        }

        TryConsole(text, fullPath);
    }

    /// <summary>
    /// 报告同时写文件和控制台。写成文件才是主渠道 —— 这是个 WinExe，
    /// 从资源管理器双击时根本没有控制台可写。
    /// </summary>
    private static void Emit(string text, string fileName)
    {
        string? path = null;
        try
        {
            Directory.CreateDirectory(AppPaths.OutputDir);
            path = Path.Combine(AppPaths.OutputDir, fileName);
            File.WriteAllText(path, text, new UTF8Encoding(true));
        }
        catch
        {
            // 忽略
        }

        TryConsole(text, path);
    }

    /// <summary>
    /// 把同一份报告送到控制台 / 标准输出。
    ///
    /// 这里有两个坑，都踩过：
    ///   1. 不能先 AttachConsole 再写 —— AttachConsole 会把本进程的标准句柄
    ///      替换成控制台句柄，于是已经重定向到管道或文件的 stdout 被绕过。
    ///   2. 不能用 Console.OpenStandardOutput() 取那个句柄 —— GUI 子系统进程
    ///      里它拿到的未必是真正的 stdout，结果是静默什么都不写。
    /// 所以：先问 GetStdHandle 要句柄，判空、判类型，然后用 WriteFile 直写字节。
    /// </summary>
    private static void TryConsole(string text, string? path)
    {
        var diag = new StringBuilder();
        try
        {
            IntPtr h = GetStdHandle(StdOutputHandle);
            diag.Append($"GetStdHandle=0x{h.ToInt64():X8}");

            if (!IsValidHandle(h))
            {
                // 既没有 stdout、也没有可继承的控制台 —— 典型场景是从资源管理器
                // 双击 exe。自己开一个控制台窗口，让报告有地方显示。
                bool attached = AttachConsole(AttachParentProcess);
                bool allocated = !attached && AllocConsole();
                diag.Append($" attach={attached} alloc={allocated}");
                if (!attached && !allocated)
                {
                    Diagnostic(diag.ToString() + "  结论：没有可用的控制台，仅写文件。");
                    return;
                }

                h = GetStdHandle(StdOutputHandle);
                diag.Append($" h2=0x{h.ToInt64():X8}");
                if (!IsValidHandle(h))
                {
                    Diagnostic(diag.ToString());
                    return;
                }
            }

            // 只有输出目标真的是控制台时才切代码页；写管道/文件时无此概念。
            if (GetConsoleMode(h, out _))
            {
                SetConsoleOutputCP(65001);
            }

            string payload = "\r\n" + text;
            if (path is not null)
            {
                payload += "（报告已同时写入：" + path + "）\r\n";
            }
            payload += "\r\n";

            byte[] bytes = new UTF8Encoding(false).GetBytes(payload);

            // 直接 WriteFile 写到句柄上，而不是 Console.OpenStandardOutput()。
            // 后者在 GUI 子系统进程里不一定能拿到真正的 stdout（实测就是这个
            // 环节让 `exe --selftest > 报告.txt` 变成了空文件），而我们刚刚已经
            // 亲自判定过句柄类型，直接写反而最没有悬念。
            if (!WriteFile(h, bytes, (uint)bytes.Length, out _, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                Diagnostic(diag.ToString() + $" write=fail err={err}" + Explain(err));
            }
        }
        catch (Exception ex)
        {
            // 控制台这条路走不通不影响主流程（报告已经写进文件了），
            // 但留一份现场，免得「什么输出都没有」时无从查起。
            diag.Append("  ex=" + ex.Message);
            Diagnostic(diag.ToString());
        }
    }

    /// <summary>把 Windows 错误码翻译成能直接照做的说明。</summary>
    private static string Explain(int win32Error) => win32Error switch
    {
        232 or 109 =>
            Environment.NewLine +
            "说明：调用方没有读取输出，或者已经提前把管道关掉了。" + Environment.NewLine +
            "      Windows 上 PowerShell 5.1 直接调用 GUI 子系统程序（WinExe）时" + Environment.NewLine +
            "      不等进程结束，所以「赋值捕获」和「> 重定向」都拿不到输出。" + Environment.NewLine +
            "      改用管道消费就能正常拿到，例如：" + Environment.NewLine +
            "          & .\\游戏助手.exe --selftest | ForEach-Object { $_ }" + Environment.NewLine +
            "      报告本身始终会写进「日志」目录，不受此影响。" + Environment.NewLine,
        _ => string.Empty
    };

    private static bool IsValidHandle(IntPtr h) => h != IntPtr.Zero && h != new IntPtr(-1);

    /// <summary>控制台输出失败时留下的现场记录，正常情况不会产生这个文件。</summary>
    private static void Diagnostic(string text)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.OutputDir);
            File.WriteAllText(
                Path.Combine(AppPaths.OutputDir, "控制台输出失败.txt"),
                "游戏助手 · 控制台输出诊断" + Environment.NewLine +
                "时间 : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                "详情 : " + text + Environment.NewLine,
                new UTF8Encoding(true));
        }
        catch
        {
            // 连诊断都写不了就真的没办法了
        }
    }
}
