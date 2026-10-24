using GameHelper.UI;

namespace GameHelper;

internal static class Program
{
    internal const string AppName = "游戏助手";
    internal const string AppVersion = "2.0.0";

    [STAThread]
    internal static void Main(string[] args)
    {
        // 两个诊断入口，用来在没有屏幕交互的情况下验证程序：
        //   --selftest  采集一轮真实数据（显卡、显存探针、清理逻辑）
        //   --dryrun    构建全部界面控件但不显示窗口
        if (HasFlag(args, "--selftest"))
        {
            SelfTest.Run(args);
            return;
        }

        if (HasFlag(args, "--version"))
        {
            SelfTest.Run(args);
            return;
        }

        // 帧数采集自检：真挂一次 PresentMon 看能不能读到帧。
        // 存在的意义：帧数要管理员权限、要 PresentMon、还要列名对得上，
        // 任何一环出问题都只表现为「一直不可用」。与其让用户打完一整局
        // 才发现没数据，不如开局前花 6 秒验一次。
        //   --fpsprobe            探测 6 秒
        //   --fpsprobe 10         探测 10 秒
        if (HasFlag(args, "--fpsprobe"))
        {
            int seconds = 6;
            if (TryGetValue(args, "--fpsprobe", out string secText)
                && int.TryParse(secText, out int parsed)
                && parsed is > 0 and <= 120)
            {
                seconds = parsed;
            }

            SelfTest.FpsProbe(seconds);
            return;
        }

        //   --resummary <csv>    用当前算法对一份旧日志重新生成汇总
        string? resumeCsv = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--resummary", StringComparison.OrdinalIgnoreCase))
            {
                resumeCsv = args[i + 1];
                break;
            }
        }
        if (resumeCsv is not null)
        {
            SelfTest.Resummary(resumeCsv);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetDefaultFont(Theme.Ui);

        // 未捕获异常也要让用户看见，而不是窗口悄悄消失。
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        try
        {
            var form = new MainForm();

            // 无人值守的自动化一律跳过首次导览：它是模态窗口，
            // 没人去点就会一直卡住（截图流程踩过一次）。
            if (HasFlag(args, "--dryrun"))
            {
                form.SkipFirstRunTour = true;
                SelfTest.DryRun(form);
                form.Dispose();
                return;
            }

            if (TryGetValue(args, "--shot", out string shotDir))
            {
                form.SkipFirstRunTour = true;
                form.CaptureScreenshots(shotDir);
                form.Dispose();
                return;
            }

            Application.Run(form);
        }
        catch (Exception ex)
        {
            ReportCrash(ex);
        }
    }

    private static bool HasFlag(string[] args, string flag)
    {
        foreach (var a in args)
        {
            if (a.Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>取出形如 <c>--shot &lt;目录&gt;</c> 的参数值。</summary>
    private static bool TryGetValue(string[] args, string flag, out string value)
    {
        value = string.Empty;
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                value = args[i + 1];
                return true;
            }

            value = Core.AppPaths.OutputDir;
            return true;
        }
        return false;
    }

    private static void ReportCrash(Exception? ex)
    {
        string text = ex?.ToString() ?? "未知错误";

        try
        {
            Directory.CreateDirectory(Core.AppPaths.OutputDir);
            File.WriteAllText(
                Path.Combine(Core.AppPaths.OutputDir, "崩溃-" + DateTime.Now.ToString("MMdd-HHmmss") + ".txt"),
                text,
                new System.Text.UTF8Encoding(true));
        }
        catch
        {
            // 连日志都写不出来时，至少弹个框
        }

        try
        {
            MessageBox.Show(
                AppName + " 遇到了未预期的错误：\r\n\r\n" + text,
                "程序错误",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // 忽略
        }
    }
}
