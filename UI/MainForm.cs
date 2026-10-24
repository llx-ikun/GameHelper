using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GameHelper.Core;

namespace GameHelper.UI;

/// <summary>
/// 主窗口。
///
/// 结构上分四块（全部用 TableLayoutPanel 定位，避免 WinForms 里
/// 「Dock=Fill 与 Dock=Top 谁先占位」那个经典坑）：
///   菜单栏 / 概览卡片 / 三个功能页 / 运行日志
///
/// 数据流是单向的：
///   采样线程 -> Snapshot -> 界面重绘
///   界面操作 -> 服务层 -> 日志
/// 界面从不自己去读显卡，服务层也不知道界面的存在。
/// </summary>
internal sealed partial class MainForm : Form
{
    private readonly Sampler _sampler = new();
    private readonly MonitorSession _monitor = new();
    private readonly CleanerService _cleaner = new();

    private readonly HashSet<int> _checkedPids = new();
    private readonly List<string> _logHistory = new();

    private TableLayoutPanel _root = null!;
    private Panel _header = null!;
    private Label _lblGpu = null!;
    private Label _lblAdmin = null!;
    private Label _lblVram = null!;
    private Label _lblSub = null!;
    private Label _lblGame = null!;
    private Label _lblRam = null!;
    private MeterBar _bar = null!;

    private TabControl _tabs = null!;
    private MenuStrip _menu = null!;
    private ToolStripMenuItem _miToggleLog = null!;

    private GroupBox _logGroup = null!;
    private TextBox _logBox = null!;

    private NotifyIcon _tray = null!;
    private ToolStripMenuItem _miTrayStart = null!;
    private ToolStripMenuItem _miTrayStop = null!;
    private ToolStripMenuItem _miTrayNotify = null!;

    private Snapshot _last = Snapshot.Empty;
    private bool _logVisible = true;

    /// <summary>
    /// 是否曾经读到过显卡数据。
    ///
    /// 有这个标志才能把三种状态分开说：
    /// 「还没读到」（刚启动，正常）、「根本读不到」（没 NVIDIA，环境限制）、
    /// 「读到过又没了」（驱动异常，值得排查）。
    /// 不区分的话，用 A 卡的人会对着一句「等待显卡数据…」一直等下去。
    /// </summary>
    private bool _gpuSeen;

    /// <summary>
    /// 是否以管理员运行。构造时算一次 —— 一个进程的权限在运行期间不会变，
    /// 每次刷新都去调 <c>WindowsIdentity.GetCurrent()</c> 是白花钱。
    /// </summary>
    private readonly bool _isAdmin = Inventory.IsAdmin();

    private const int HeaderHeight = 132;
    private const int LogHeight = 158;

    internal MainForm()
    {
        BuildForm();
        BuildRoot();
        BuildMenu();
        BuildHeader();
        BuildTabs();
        BuildLogPanel();
        BuildTray();

        WireSampler();
        WireMonitor();

        // 控件都建好之后才能套用设置 —— 阈值要同时写进
        // NumericUpDown、占用条和曲线三处。
        ApplySettings();

        FormClosing += OnFormClosing;
        Shown += OnShown;
    }

    // ------------------------------------------------------------------ 窗体

    private void BuildForm()
    {
        SuspendLayout();

        Text = "游戏助手";
        Icon = Theme.AppIcon();
        BackColor = Theme.Bg;
        Font = Theme.Ui;
        // 设计尺寸按 100% 缩放定。125% 缩放下会等比放大到约 1225x875，
        // 在 1080p 屏幕上仍然放得下（含任务栏）。
        ClientSize = new Size(980, 700);
        MinimumSize = new Size(860, 600);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        DoubleBuffered = true;
        KeyPreview = true;

        ResumeLayout(false);
    }

    private void BuildRoot()
    {
        _root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Theme.Bg,
            Padding = new Padding(0),
            Margin = new Padding(0),
        };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));   // 菜单
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, HeaderHeight)); // 概览
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 功能页
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, LogHeight)); // 日志

        Controls.Add(_root);
    }

    // ------------------------------------------------------------------ 菜单

    private void BuildMenu()
    {
        _menu = new MenuStrip
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Card,
            Font = Theme.Ui,
            Padding = new Padding(6, 1, 0, 1),
            Margin = new Padding(0),
            GripStyle = ToolStripGripStyle.Hidden,
        };

        var miFile = new ToolStripMenuItem("文件(&F)");
        miFile.DropDownItems.Add(Item("导出运行日志…", (_, _) => ExportLog()));
        miFile.DropDownItems.Add(Item("打开输出目录", (_, _) => OpenOutputDir()));
        miFile.DropDownItems.Add(new ToolStripSeparator());
        miFile.DropDownItems.Add(Item("退出", (_, _) => Close()));

        var miView = new ToolStripMenuItem("视图(&V)");
        _miToggleLog = new ToolStripMenuItem("隐藏运行日志")
        {
            ShortcutKeys = Keys.Control | Keys.L,
        };
        _miToggleLog.Click += (_, _) => SetLogVisible(!_logVisible);
        miView.DropDownItems.Add(_miToggleLog);
        miView.DropDownItems.Add(Item("清空日志", (_, _) => ClearLog()));

        var miTools = new ToolStripMenuItem("工具(&T)");
        miTools.DropDownItems.Add(Item("立即刷新", (_, _) => ForceRefresh(), Keys.F5));
        miTools.DropDownItems.Add(Item("生成显存体检报告…", (_, _) => ShowHealthReport()));
        miTools.DropDownItems.Add(new ToolStripSeparator());

        // 采集真实帧数需要管理员权限（PresentMon 起 ETW 会话的硬性要求）。
        // 提权入口放在这里而不是做自动提权：本程序还有「结束进程」这类
        // 在高权限下更危险的操作，提权应当由用户明确发起，不该悄悄发生。
        var miAdmin = Item("以管理员身份重启…", (_, _) => RestartAsAdmin());
        miAdmin.Enabled = !_isAdmin;
        miTools.DropDownItems.Add(miAdmin);
        miTools.DropDownItems.Add(new ToolStripSeparator());
        miTools.DropDownItems.Add(Item("打开游戏目录", (_, _) => OpenFolder(_cleaner.GameDir)));
        miTools.DropDownItems.Add(Item("打开备份目录", (_, _) => OpenFolder(_cleaner.BackupRoot)));

        var miHelp = new ToolStripMenuItem("帮助(&H)");
        miHelp.DropDownItems.Add(Item("功能导览（重新打开）…", (_, _) => ShowAbout(), Keys.F1));
        miHelp.DropDownItems.Add(Item("关于本程序…", (_, _) => ShowVersion()));

        _menu.Items.AddRange(new ToolStripItem[] { miFile, miView, miTools, miHelp });
        MainMenuStrip = _menu;

        _root.Controls.Add(_menu, 0, 0);
    }

    private static ToolStripMenuItem Item(string text, EventHandler onClick, Keys shortcut = Keys.None)
    {
        var it = new ToolStripMenuItem(text);
        if (shortcut != Keys.None)
        {
            it.ShortcutKeys = shortcut;
        }
        it.Click += onClick;
        return it;
    }

    /// <summary>
    /// 以管理员身份重启本程序。
    ///
    /// 这是「真实帧数」唯一能被打开的方式 —— 实测 PresentMon 1.10 启动
    /// ETW 会话必须管理员，普通权限下直接以 access denied 退出。
    ///
    /// 之所以做成用户主动点击而不是把清单改成 requireAdministrator：
    /// 本程序还有「结束进程回收显存」这类操作，在管理员权限下误伤面更大。
    /// 权限该有多大，取决于用户当下要做什么，所以由用户决定何时提权。
    /// </summary>
    private void RestartAsAdmin()
    {
        if (_isAdmin)
        {
            AddLog("当前已经是管理员权限，无需重启。");
            return;
        }

        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            AddLog("无法确定程序所在路径，请手动右键程序 → 以管理员身份运行。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",                     // 触发 UAC 提权
                WorkingDirectory = AppPaths.RootDir,
            });
        }
        catch (Exception ex)
        {
            // 用户在 UAC 对话框上点「否」也会走到这里 —— 那是正常选择，不是故障。
            AddLog("已取消提权：" + ex.Message);
            return;
        }

        // 新实例已经起来，本实例退出，免得两个程序同时采样、
        // 抢同一个 PresentMon 会话名。
        AddLog("已请求以管理员身份重启，本窗口即将关闭。");
        Close();
    }

    // ------------------------------------------------------------------ 概览卡片

    private void BuildHeader()
    {
        _header = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Card,
            Margin = new Padding(10, 0, 10, 6),
            Padding = new Padding(0),
        };

        _lblGpu = Theme.MakeLabel("显卡：读取中…", Theme.Text, Theme.Title);
        _header.Controls.Add(_lblGpu);

        _lblAdmin = Theme.MakeLabel("权限：--", Theme.Sub, Theme.Ui);
        _lblAdmin.TextAlign = ContentAlignment.MiddleRight;
        _lblAdmin.AutoSize = false;
        _header.Controls.Add(_lblAdmin);

        _bar = new MeterBar { Height = 22 };
        _header.Controls.Add(_bar);

        _lblVram = Theme.MakeLabel("显存：-- / -- MB", Theme.Text, Theme.Ui);
        _header.Controls.Add(_lblVram);

        _lblSub = Theme.MakeLabel("温度：--      占用：--", Theme.Sub, Theme.Ui);
        _header.Controls.Add(_lblSub);

        _lblGame = Theme.MakeLabel("游戏：--", Theme.Sub, Theme.Ui);
        _lblGame.TextAlign = ContentAlignment.MiddleRight;
        _lblGame.AutoSize = false;
        _header.Controls.Add(_lblGame);

        _lblRam = Theme.MakeLabel("系统内存：--", Theme.Sub, Theme.Ui);
        _lblRam.TextAlign = ContentAlignment.MiddleRight;
        _lblRam.AutoSize = false;
        _header.Controls.Add(_lblRam);

        // 右对齐的两组信息需要知道面板宽度，用 Resize 显式重排最稳。
        _header.Resize += (_, _) => LayoutHeader();

        _root.Controls.Add(_header, 0, 1);
    }

    private void LayoutHeader()
    {
        int w = _header.ClientSize.Width;
        const int pad = 16;
        int rightW = Math.Min(400, Math.Max(200, w / 2));

        _lblGpu.Location = new Point(pad, 12);
        _lblAdmin.SetBounds(w - pad - rightW, 14, rightW, 20);

        _bar.SetBounds(pad, 46, Math.Max(60, w - pad * 2), 22);
        _lblVram.Location = new Point(pad, 74);
        _lblSub.Location = new Point(pad, 100);

        _lblGame.SetBounds(w - pad - rightW, 74, rightW, 20);
        _lblRam.SetBounds(w - pad - rightW, 100, rightW, 20);
    }

    // ------------------------------------------------------------------ 日志

    private void BuildLogPanel()
    {
        _logGroup = Theme.MakeGroup("  运行日志  ");
        _logGroup.Dock = DockStyle.Fill;
        _logGroup.Margin = new Padding(10, 0, 10, 10);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Card,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Card,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        tools.Controls.Add(Theme.MakeGhostButton("导出日志…", 100, 26));
        tools.Controls.Add(Theme.MakeGhostButton("打开输出目录", 116, 26));
        tools.Controls.Add(Theme.MakeGhostButton("清空", 70, 26));
        tools.Controls.Add(Theme.MakeGhostButton("隐藏日志", 90, 26));
        ((Button)tools.Controls[0]).Click += (_, _) => ExportLog();
        ((Button)tools.Controls[1]).Click += (_, _) => OpenOutputDir();
        ((Button)tools.Controls[2]).Click += (_, _) => ClearLog();
        var btnHide = (Button)tools.Controls[3];
        btnHide.Click += (_, _) => SetLogVisible(!_logVisible);
        btnHide.Name = "btnToggleLog";

        _logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            BackColor = Theme.LogBg,
            ForeColor = Theme.Text,
            Font = Theme.Mono,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0),
        };

        grid.Controls.Add(tools, 0, 0);
        grid.Controls.Add(_logBox, 0, 1);
        _logGroup.Controls.Add(grid);
        _root.Controls.Add(_logGroup, 0, 3);
    }

    private void SetLogVisible(bool visible)
    {
        _logVisible = visible;
        _logGroup.Visible = visible;
        _root.RowStyles[3].Height = visible ? LogHeight : 0F;
        _miToggleLog.Text = visible ? "隐藏运行日志" : "显示运行日志";

        if (_logGroup.Controls.Count > 0 &&
            _logGroup.Controls[0] is TableLayoutPanel g &&
            g.Controls.Count > 0 &&
            g.Controls[0] is FlowLayoutPanel f &&
            f.Controls["btnToggleLog"] is Button b)
        {
            b.Text = visible ? "隐藏日志" : "显示日志";
        }
    }

    internal void AddLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text.TrimEnd();
        _logHistory.Add(line);

        if (_logBox.IsDisposed)
        {
            return;
        }

        if (_logBox.Lines.Length > 3000)
        {
            _logBox.Clear();
            _logBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] （日志过长，已清空前面的内容）" + Environment.NewLine);
        }

        _logBox.AppendText(line + Environment.NewLine);
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    private void ClearLog()
    {
        _logBox.Clear();
        AddLog("已清空日志显示。");
    }

    private void ExportLog()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.OutputDir);
            string path = Path.Combine(
                AppPaths.OutputDir,
                "运行日志-" + DateTime.Now.ToString("MMdd-HHmm") + ".txt");

            var sb = new StringBuilder();
            sb.AppendLine("游戏助手 - 运行日志");
            sb.AppendLine("导出时间 : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("nvidia-smi : " + (NvidiaSmi.Path ?? "未找到"));
            sb.AppendLine(new string('=', 60));
            foreach (var l in _logHistory)
            {
                sb.AppendLine(l);
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            AddLog("日志已导出：" + path);
            OpenFolder(AppPaths.OutputDir);
        }
        catch (Exception ex)
        {
            AddLog("导出日志失败：" + ex.Message);
        }
    }

    private void OpenOutputDir()
    {
        Directory.CreateDirectory(AppPaths.OutputDir);
        OpenFolder(AppPaths.OutputDir);
    }

    private void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                AddLog("目录不存在：" + path);
                return;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + path + "\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AddLog("打开目录失败：" + ex.Message);
        }
    }

    // ------------------------------------------------------------------ 采样连线

    private void WireSampler()
    {
        _sampler.SnapshotReady += s => Post(() => OnSnapshot(s));
        _sampler.Problem += m => Post(() => AddLog(m));
    }

    /// <summary>把后台线程的调用切回界面线程。窗口已关时静默丢弃。</summary>
    private void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
            // 窗口正在销毁，忽略
        }
        catch (InvalidOperationException)
        {
            // 句柄尚未创建或已销毁，忽略
        }
    }

    private void OnShown(object? sender, EventArgs e)
    {
        AddLog("游戏助手已启动。");
        AddLog("输出目录：" + AppPaths.OutputDir);

        if (NvidiaSmi.Path is not null)
        {
            AddLog("nvidia-smi：" + NvidiaSmi.Path);
        }
        else
        {
            // 说清这是环境限制还是故障 —— 在 AMD / Intel 显卡的机器上是前者，
            // 用户不该对着窗口等一个永远读不到的数字。
            AddLog("注意：" + NvidiaSmi.UnavailableHint);
        }

        if (!_sampler.ProbeAvailable)
        {
            AddLog("警告：" + (_sampler.ProbeError ?? "进程显存探针不可用") + "，进程列表将为空。");
        }

        AddLog("提示：游戏自身的显存无法从外部释放；能回收的只有其他后台程序占用的部分。");

        _sampler.Start();
        BeginInvoke(new Action(RefreshGate));

        // 导览放在最后弹：先让主界面把日志、显卡信息都填好，
        // 用户关掉导览时看到的是一个已经活起来的窗口，而不是一片空白。
        ShowFirstRunTour();
    }

    /// <summary>
    /// 首次启动时自动弹一次功能导览。
    ///
    /// 只弹一次，之后不再打扰 —— 「随手打开工具」是高频动作，
    /// 每次都弹一个要人去关的窗口，很快就会变成纯粹的噪音。
    /// 想再看随时可以按 F1 或走帮助菜单。
    /// </summary>
    /// <summary>
    /// 自动化模式（<c>--shot</c> / <c>--dryrun</c>）下不要弹导览。
    ///
    /// 它是**模态**窗口：截图流程里一旦弹出来，就没有任何人去点它，
    /// 进程会永远卡在那里。这类「无人值守时挂死」的问题只在自动化里暴露，
    /// 所以必须显式关掉，而不是指望它自己不发生。
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool SkipFirstRunTour { get; set; }

    /// <summary>设置里「已看过导览」这个键名。读写都用它，避免两边写歪。</summary>
    internal const string TourSeenKey = "已看过导览";

    /// <summary>
    /// 这一次启动到底要不要弹导览。
    ///
    /// 抽成纯函数是为了能被 <c>--selftest</c> 断言：弹出的窗口是模态的，
    /// 无人值守时一旦该弹没弹、或者不该弹却弹了，进程会直接卡死 ——
    /// 这类问题靠人工点几下是测不稳的，只能把判定本身锁住。
    /// </summary>
    internal static bool ShouldShowTour(bool skipFirstRun, bool alreadySeen) =>
        !skipFirstRun && !alreadySeen;

    private void ShowFirstRunTour()
    {
        if (!ShouldShowTour(SkipFirstRunTour, _settings.GetBool(TourSeenKey, false)))
        {
            return;
        }

        // 不管用户怎么关掉（按钮 / 右上角 X / Esc），都记成「看过了」——
        // 唯一不该记住的情况是他明确取消了「不再自动显示」，那时下次还该弹。
        using var dlg = new AboutForm(tour: true);
        dlg.ShowDialog(this);

        if (dlg.DontShowAgain)
        {
            _settings.Set(TourSeenKey, true);
            _settings.Save();
            AddLog("已记住：下次启动不再自动显示功能导览（F1 可随时打开）。");
        }
        else
        {
            AddLog("下次启动仍会显示功能导览（F1 可随时打开）。");
        }
    }

    /// <summary>一次采样到达。所有界面更新都集中在这里。</summary>
    private void OnSnapshot(Snapshot s)
    {
        _last = s;
        UpdateHeader(s);
        UpdateOverview(s);
        UpdateMonitor(s);
        UpdateGate(s);
    }

    private void UpdateHeader(Snapshot s)
    {
        var g = s.Gpu;

        if (g.HasData)
        {
            _gpuSeen = true;
        }

        if (!g.HasData)
        {
            _lblGpu.Text = "显卡：" + NvidiaSmi.UnavailableReason;
            _lblVram.Text = "显存：--";
            _lblSub.Text = "温度：--      占用：--";
            _lblGpu.ForeColor = NvidiaSmi.Path is null ? Theme.Sub : Theme.Amber;
            _bar.SetValue(0, 0);
        }
        else
        {
            _lblGpu.Text = "显卡：" + g.Name;

            // 必须把颜色复位：启动最初 1.5 秒还没采到数据时走的是上面那个分支，
            // 那里会把标签染成警示色。不复位的画，有显卡的机器上它会一直黄着。
            _lblGpu.ForeColor = Theme.Text;

            _lblVram.Text = $"显存：{g.MemUsedMb:0} / {g.MemTotalMb:0} MB"
                          + $"     已用 {g.Pct:0.0}%     剩余 {g.FreeMb:0} MB";
            _lblSub.Text = $"温度：{g.TempC:0} C      占用：{g.UtilPct:0}%"
                         + $"      频率：{g.GpuClockMhz:0} MHz      功耗：{g.PowerW:0} W";

            double pct = g.Pct;
            var color = Theme.StateColor(pct, _monitor.WarnPct, _monitor.CritPct);
            _lblVram.ForeColor = color;
            if (pct >= _monitor.CritPct)
            {
                _lblVram.Text += "     显存告急！";
            }
            else if (pct >= _monitor.WarnPct)
            {
                _lblVram.Text += "     接近上限";
            }

            _bar.WarnPct = _monitor.WarnPct;
            _bar.CritPct = _monitor.CritPct;
            _bar.SetValue(g.MemUsedMb, g.MemTotalMb);
        }

        bool admin = _isAdmin;
        _lblAdmin.Text = admin ? "权限：管理员" : "权限：普通（帧数不可用）";
        _lblAdmin.ForeColor = admin ? Theme.Green : Theme.Amber;

        if (s.GameRunning)
        {
            string extra = s.GameStartedAt is not null
                ? "，已运行 " + ReportBuilder.Describe(DateTime.Now - s.GameStartedAt.Value)
                : string.Empty;
            _lblGame.Text = $"游戏：运行中{extra}";
            _lblGame.ForeColor = Theme.Blue;
        }
        else
        {
            _lblGame.Text = "游戏：未运行";
            _lblGame.ForeColor = Theme.Sub;
        }

        _lblRam.Text = s.RamTotalMb > 0
            ? $"系统内存：{s.RamUsedMb:0} / {s.RamTotalMb:0} MB"
            : "系统内存：--";
    }

    private void ForceRefresh()
    {
        if (_last != Snapshot.Empty)
        {
            UpdateHeader(_last);
            UpdateOverview(_last);
            UpdateMonitor(_last);
        }
        AddLog("已手动刷新。");
    }

    // ------------------------------------------------------------------ 托盘

    private void BuildTray()
    {
        _tray = new NotifyIcon
        {
            Icon = Theme.AppIcon(),
            Text = "游戏助手",
            Visible = true,
        };

        var menu = new ContextMenuStrip { Font = Theme.Ui };
        menu.Items.Add(Item("显示主窗口", (_, _) => RestoreFromTray()));
        menu.Items.Add(new ToolStripSeparator());
        _miTrayStart = Item("开始记录显存", (_, _) => StartMonitor());
        _miTrayStop = Item("停止记录", (_, _) => StopMonitor());
        menu.Items.Add(_miTrayStart);
        menu.Items.Add(_miTrayStop);
        menu.Items.Add(new ToolStripSeparator());

        // 托盘里也能开关提醒，而且**不必先开始记录** —— 这个功能本来就是
        // 给「窗口收起来、挂着工具直接打」的场景用的。
        // 用 CheckedChanged 而不是 Click：程序里同步状态时也会走到同一条路。
        _miTrayNotify = new ToolStripMenuItem("显存将满时通知")
        {
            CheckOnClick = true,
            Checked = _notifier.Enabled,
            Font = Theme.Ui,
        };
        _miTrayNotify.CheckedChanged += (_, _) => SetVramNotify(_miTrayNotify.Checked);
        menu.Items.Add(_miTrayNotify);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("退出", (_, _) => Close()));

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        UpdateTrayMenu();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void UpdateTrayMenu()
    {
        if (_miTrayStart is null || _miTrayStop is null)
        {
            return;
        }
        _miTrayStart.Enabled = !_monitor.Active;
        _miTrayStop.Enabled = _monitor.Active;
    }

    /// <summary>
    /// 越线提醒走托盘气泡，不弹模态窗口。
    ///
    /// 这一点是有意与脚本版不同的：WScript.Shell 的弹窗会抢焦点，
    /// 在全屏游戏里足以把游戏踢出全屏，反而更影响游玩。
    /// </summary>
    private void ShowAlert(string title, string text, bool critical)
    {
        try
        {
            _tray.ShowBalloonTip(
                10000,
                title,
                text,
                critical ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        catch
        {
            // 气泡不可用（例如通知被系统关闭）时不阻塞流程
        }

        AddLog((critical ? "[危急] " : "[警告] ") + text.Replace("\n", " "));
    }

    // ------------------------------------------------------------------ 关闭

    private bool _shutdownDone;

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        Shutdown();
    }

    /// <summary>
    /// 收尾。放在一个方法里，是因为它有两条进入路径：
    /// 用户关窗口，以及 Dispose（例如 --dryrun 里构建完就丢弃窗口）。
    /// 只挂在 FormClosing 上的话，走 Dispose 那条路会留下一个托盘图标不消失。
    /// </summary>
    private void Shutdown()
    {
        if (_shutdownDone)
        {
            return;
        }
        _shutdownDone = true;

        if (_monitor.Active)
        {
            _monitor.Stop();
            _monitor.WriteSummary();
        }

        _sampler.Stop();

        try
        {
            SaveSettings();
        }
        catch
        {
            // 设置存不下来不该阻止窗口关闭
        }

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Shutdown();
            _sampler.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>把控件重绘短暂挂起，避免列表刷新时闪。列表每秒重建一次时很有用。</summary>
    private sealed class RedrawSuspender : IDisposable
    {
        private const int WmSetRedraw = 0x000B;

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private readonly Control _c;

        public RedrawSuspender(Control c)
        {
            _c = c;
            if (c.IsHandleCreated)
            {
                SendMessage(c.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
            }
        }

        public void Dispose()
        {
            if (_c.IsHandleCreated)
            {
                SendMessage(_c.Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
                _c.Invalidate(true);
            }
        }
    }
}
