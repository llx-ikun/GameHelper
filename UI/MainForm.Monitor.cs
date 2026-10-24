using System.Diagnostics;
using System.Text;
using GameHelper.Core;

namespace GameHelper.UI;

/// <summary>监控页：实时曲线、阈值设置、记录到 CSV、越线提醒。</summary>
internal sealed partial class MainForm
{
    private TrendChart _chart = null!;
    private Label _lblMonStats = null!;
    private Label _lblMonBanner = null!;
    private Label _lblFpsValue = null!;
    private Label _lblFpsDetail = null!;
    private Label _lblTarget = null!;
    private Button _btnPickTarget = null!;

    /// <summary>最近一次采样快照，用于切换目标后立刻重画帧数卡片。</summary>
    private Snapshot? _lastSnapshot;
    private NumericUpDown _numWarn = null!;
    private NumericUpDown _numCrit = null!;
    private CheckBox _chkThrottle = null!;
    private CheckBox _chkVramNotify = null!;
    private Button _btnMonStart = null!;
    private Button _btnMonStop = null!;
    private Button _btnMonSummary = null!;
    private bool _syncingThresholds;

    /// <summary>防止「界面复选框」与「托盘菜单勾选项」互相触发造成回环。</summary>
    private bool _syncingNotify;

    /// <summary>显存将满的提醒策略。每个采样周期都在评估，与是否在记录无关。</summary>
    private readonly VramNotifier _notifier = new();

    private const double DefaultWarnPct = 82;
    private const double DefaultCritPct = 90;

    private TabPage BuildMonitorTab()
    {
        var page = new TabPage("监控")
        {
            BackColor = Theme.Bg,
            Padding = new Padding(8),
        };

        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Bg,
        };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        // 86 而不是 78：控制条加了「显存将满时通知」之后变长，窄窗口下会折成两行。
        // 一行 32px + 3px 上下边距 = 38，两行需要 76 以上；按 78 会切掉第二行。
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 86F));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        // ---- 控制条 ----
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Theme.Bg,
            Margin = new Padding(0, 0, 0, 6),
        };

        _btnMonStart = Theme.MakeButton("开始记录", Theme.Blue, 104, 32);
        _btnMonStart.Click += (_, _) => StartMonitor();

        _btnMonStop = Theme.MakeButton("停止记录", Theme.Gray, 104, 32);
        _btnMonStop.Click += (_, _) => StopMonitor();
        _btnMonStop.Enabled = false;

        _btnMonSummary = Theme.MakeGhostButton("导出汇总…", 110, 32);
        _btnMonSummary.Click += (_, _) => ExportMonitorSummary();

        var btnOpen = Theme.MakeGhostButton("打开输出目录", 116, 32);
        btnOpen.Click += (_, _) => OpenOutputDir();

        _numWarn = MakeThresholdBox((decimal)DefaultWarnPct);
        _numCrit = MakeThresholdBox((decimal)DefaultCritPct);
        _numWarn.ValueChanged += (_, _) => OnThresholdChanged();
        _numCrit.ValueChanged += (_, _) => OnThresholdChanged();

        _chkThrottle = new CheckBox
        {
            Text = "记录降频原因",
            AutoSize = true,
            ForeColor = Theme.Text,
            Font = Theme.Ui,
            Margin = new Padding(14, 7, 0, 0),
            BackColor = Theme.Bg,
        };
        _chkThrottle.CheckedChanged += (_, _) =>
        {
            // 可以在记录过程中随时开关。开启后会立刻触发一次查询。
            _sampler.TrackThrottle = _chkThrottle.Checked || _monitor.Active;
            AddLog(_chkThrottle.Checked ? "已开启降频原因记录。" : "已关闭降频原因记录。");
        };

        // 默认打勾，与 VramNotifier.Enabled 的默认值保持一致。
        // 这样 ApplySettings 里只有「用户存过 false」才会触发一次变更事件，
        // 启动时不会白写一行「已开启显存提醒」的日志。
        _chkVramNotify = new CheckBox
        {
            Text = "显存将满时通知",
            AutoSize = true,
            Checked = true,
            ForeColor = Theme.Text,
            Font = Theme.Ui,
            Margin = new Padding(14, 7, 0, 0),
            BackColor = Theme.Bg,
        };
        _chkVramNotify.CheckedChanged += (_, _) => SetVramNotify(_chkVramNotify.Checked);

        bar.Controls.Add(_btnMonStart);
        bar.Controls.Add(_btnMonStop);
        bar.Controls.Add(_btnMonSummary);
        bar.Controls.Add(btnOpen);
        bar.Controls.Add(MakeInlineLabel("警告阈值"));
        bar.Controls.Add(_numWarn);
        bar.Controls.Add(MakeInlineLabel("%"));
        bar.Controls.Add(MakeInlineLabel("危急阈值"));
        bar.Controls.Add(_numCrit);
        bar.Controls.Add(MakeInlineLabel("%"));
        bar.Controls.Add(_chkThrottle);
        bar.Controls.Add(_chkVramNotify);

        outer.Controls.Add(bar, 0, 0);

        // ---- 图表 + 统计 ----
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 258F));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var chartCell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Bg,
            Margin = new Padding(0, 0, 6, 0),
        };
        chartCell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        chartCell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        chartCell.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));

        var chartGroup = Theme.MakeGroup("  显存占用曲线（MB · 实时）  ");
        chartGroup.Dock = DockStyle.Fill;
        _chart = new TrendChart(400)
        {
            Dock = DockStyle.Fill,
            WarnPct = DefaultWarnPct,
            CritPct = DefaultCritPct,
        };
        chartGroup.Controls.Add(_chart);

        _lblMonBanner = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.UiBold,
            ForeColor = Theme.Green,
            BackColor = Theme.Bg,
            Text = "显存正常。",
        };

        chartCell.Controls.Add(chartGroup, 0, 0);
        chartCell.Controls.Add(_lblMonBanner, 0, 1);

        // 右栏拆成「帧数」+「本次记录」两块。
        // 帧数放在记录统计**上面**：它是判断「越玩越卡」的唯一直接证据，
        // 而记录统计在没有开始记录前是空的，不该占着最显眼的位置。
        var statsCell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        // 118 而不是估算出来的 ~86：GroupBox 有标题栏 + (10,6,10,10) 内边距，
        // 大字号那一行在 125% 缩放下会更高。宁可多留，也不要把数字切掉 ——
        // 上一轮控制条就是按「刚好够」算的，结果第二行被切了。
        // 现在里面多了一行「监控目标」，同步抬到 150。
        statsCell.RowStyles.Add(new RowStyle(SizeType.Absolute, 150F));
        statsCell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var fpsGroup = Theme.MakeGroup("  帧数（实时）  ");
        fpsGroup.Dock = DockStyle.Fill;

        var fpsInner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Card,
            Margin = new Padding(0),
        };
        // 三行：目标行 26 + 数值行 44 + 详情行（吃掉剩余）。
        // 原来只有两行、总高 118；加目标行后同步抬高到 146，
        // 否则详情行会被压到只剩一两行字，「掉帧明显」那条提示就看不见了。
        fpsInner.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
        fpsInner.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
        fpsInner.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        // 监控目标行：默认盯《永劫无间》，可以改成别的游戏。
        // 做成一行而不是塞进设置页 —— 换目标是个偶尔做一次的操作，
        // 但做的时候要能立刻看见现在盯的是谁。
        var targetRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            BackColor = Theme.Card,
            Margin = new Padding(0),
        };
        targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56F));

        _lblTarget = new Label
        {
            Dock = DockStyle.Fill,
            Font = Theme.Ui,
            ForeColor = Theme.Sub,
            BackColor = Theme.Card,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Text = "永劫无间",
        };

        _btnPickTarget = Theme.MakeButton("更改…", Theme.Card, 52, 20);
        _btnPickTarget.Margin = new Padding(0, 3, 4, 3);
        _btnPickTarget.Click += (_, _) => PickTarget();

        targetRow.Controls.Add(_lblTarget, 0, 0);
        targetRow.Controls.Add(_btnPickTarget, 1, 0);

        _lblFpsValue = new Label
        {
            Dock = DockStyle.Fill,
            Font = Theme.H1,
            ForeColor = Theme.Sub,
            BackColor = Theme.Card,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "待机",
        };

        _lblFpsDetail = new Label
        {
            Dock = DockStyle.Fill,
            Font = Theme.Ui,
            ForeColor = Theme.Sub,
            BackColor = Theme.Card,
            TextAlign = ContentAlignment.TopLeft,
            Text = "等待游戏启动…",
        };

        fpsInner.Controls.Add(targetRow, 0, 0);
        fpsInner.Controls.Add(_lblFpsValue, 0, 1);
        fpsInner.Controls.Add(_lblFpsDetail, 0, 2);
        fpsGroup.Controls.Add(fpsInner);

        var statsGroup = Theme.MakeGroup("  本次记录  ");
        statsGroup.Dock = DockStyle.Fill;

        _lblMonStats = new Label
        {
            Dock = DockStyle.Fill,
            Font = Theme.Ui,
            ForeColor = Theme.Text,
            BackColor = Theme.Card,
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(4),
            Text = "尚未开始记录。\r\n\r\n点「开始记录」后，这里会显示采样数、"
                 + "时长、显存峰值与首次越线的时间点。",
        };
        statsGroup.Controls.Add(_lblMonStats);

        statsCell.Controls.Add(fpsGroup, 0, 0);
        statsCell.Controls.Add(statsGroup, 0, 1);

        body.Controls.Add(chartCell, 0, 0);
        body.Controls.Add(statsCell, 1, 0);
        outer.Controls.Add(body, 0, 1);

        page.Controls.Add(outer);
        return page;
    }

    private static Label MakeInlineLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Theme.Text,
        Font = Theme.Ui,
        Margin = new Padding(12, 8, 2, 0),
        BackColor = Color.Transparent,
    };

    private static NumericUpDown MakeThresholdBox(decimal value) => new()
    {
        Minimum = 30,
        Maximum = 99,
        Value = value,
        Width = 56,
        Font = Theme.Ui,
        TextAlign = HorizontalAlignment.Right,
        Margin = new Padding(0, 5, 0, 0),
    };

    private void WireMonitor()
    {
        _monitor.OutputDir = AppPaths.OutputDir;
        _monitor.WarnPct = DefaultWarnPct;
        _monitor.CritPct = DefaultCritPct;

        // 越线提醒走 VramNotifier，不走 MonitorSession。
        // MonitorSession.Record() 开头就 `if (_writer is null) return;`，
        // 提醒挂在那里就等于「不开记录就永远不提醒」—— 而「随手挂着工具、
        // 不记录直接打游戏」恰恰是最需要提醒的场景。
        _notifier.WarnPct = DefaultWarnPct;
        _notifier.CritPct = DefaultCritPct;
        _notifier.Enabled = _chkVramNotify.Checked;
        _notifier.Alert += (title, text, critical) => Post(() => ShowAlert(title, text, critical));

        _monitor.StateChanged += (next, prev) => Post(() =>
        {
            if (next == VramState.Ok && prev != VramState.Ok)
            {
                AddLog($"显存已回落到安全区间（{_last.Gpu.Pct:0.0}%）。");
            }
        });

        // Record() 是在界面线程上被调用的，所以这里不需要再切线程。
        _monitor.Progress += UpdateMonitorStats;
    }

    /// <summary>
    /// 显存提醒总开关的唯一入口。界面复选框与托盘菜单勾选项都调这里，
    /// <c>_syncingNotify</c> 挡住两者互相同步时的回环。
    ///
    /// <paramref name="quiet"/> 用于启动时套用设置：状态照同步，但不写日志，
    /// 免得每次开程序都先来一行「已开启显存提醒」。
    /// </summary>
    private void SetVramNotify(bool enabled, bool quiet = false)
    {
        if (_syncingNotify)
        {
            return;
        }

        _syncingNotify = true;
        try
        {
            _notifier.Enabled = enabled;

            if (_chkVramNotify is not null && _chkVramNotify.Checked != enabled)
            {
                _chkVramNotify.Checked = enabled;
            }
            if (_miTrayNotify is not null && _miTrayNotify.Checked != enabled)
            {
                _miTrayNotify.Checked = enabled;
            }
        }
        finally
        {
            _syncingNotify = false;
        }

        // 每次开关都重新开始一轮。不重置的话，「关掉之前已经在告急」
        // 会在重新打开时被武装状态吞掉 —— 打开开关却等不到提醒。
        _notifier.Reset();

        if (!quiet)
        {
            AddLog(enabled
                ? $"已开启显存提醒（警告 {_notifier.WarnPct:0}% / 危急 {_notifier.CritPct:0}%）。"
                : "已关闭显存提醒。曲线和记录不受影响。");
        }
    }

    private void OnThresholdChanged()
    {
        if (_syncingThresholds)
        {
            return;
        }

        _syncingThresholds = true;
        try
        {
            // 保证「警告」严格小于「危急」，否则两个档位会重叠。
            if (_numWarn.Value >= _numCrit.Value)
            {
                if (_numWarn.Focused)
                {
                    _numCrit.Value = Math.Min(_numCrit.Maximum, _numWarn.Value + 1);
                }
                else
                {
                    _numWarn.Value = Math.Max(_numWarn.Minimum, _numCrit.Value - 1);
                }
            }

            _monitor.WarnPct = (double)_numWarn.Value;
            _monitor.CritPct = (double)_numCrit.Value;

            // 提醒用阈值跟着一起走，否则「界面写着 90%，提醒却按 82% 发」。
            _notifier.WarnPct = _monitor.WarnPct;
            _notifier.CritPct = _monitor.CritPct;

            _bar.WarnPct = _monitor.WarnPct;
            _bar.CritPct = _monitor.CritPct;
            _chart.WarnPct = _monitor.WarnPct;
            _chart.CritPct = _monitor.CritPct;
        }
        finally
        {
            _syncingThresholds = false;
        }
    }

    private void StartMonitor()
    {
        if (_monitor.Active)
        {
            return;
        }

        _monitor.WarnPct = (double)_numWarn.Value;
        _monitor.CritPct = (double)_numCrit.Value;
        _sampler.TrackThrottle = _chkThrottle.Checked;

        try
        {
            _monitor.Start();
        }
        catch (Exception ex)
        {
            AddLog("无法开始记录：" + ex.Message);
            return;
        }

        _chart.Clear();
        AddLog($"开始记录显存。阈值 警告 {_monitor.WarnPct:0}% / 危急 {_monitor.CritPct:0}%；文件：{_monitor.CsvPath}");
        if (_sampler.TrackThrottle)
        {
            AddLog("降频原因约每 30 秒查询一次（nvidia-smi -q -d PERFORMANCE）。");
        }

        UpdateMonitorState();
    }

    private void StopMonitor()
    {
        if (!_monitor.Active)
        {
            AddLog("当前没有在记录。");
            return;
        }

        _monitor.Stop();
        _sampler.TrackThrottle = false;

        string? summary = _monitor.WriteSummary();
        AddLog("已停止记录。" + (summary is null ? string.Empty : "汇总已写入：" + summary));

        UpdateMonitorState();
        UpdateMonitorStats();
    }

    private void ExportMonitorSummary()
    {
        if (_monitor.Samples == 0)
        {
            AddLog("还没有任何记录，先点「开始记录」。");
            return;
        }

        string text = _monitor.BuildSummary();
        string? path = _monitor.WriteSummary();

        using var dlg = new TextReportForm("监控汇总", text, path);
        dlg.ShowDialog(this);
    }

    private void UpdateMonitorState()
    {
        if (_btnMonStart is null)
        {
            return;
        }

        _btnMonStart.Enabled = !_monitor.Active;
        _btnMonStop.Enabled = _monitor.Active;
        UpdateTrayMenu();
    }

    private void UpdateMonitorStats()
    {
        if (_lblMonStats is null || _lblMonStats.IsDisposed)
        {
            return;
        }

        if (!_monitor.Active && _monitor.Samples == 0)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("状态        " + (_monitor.Active ? "记录中" : "已停止"));
        sb.AppendLine("采样数      " + _monitor.Samples);
        sb.AppendLine("时长        " + ReportBuilder.Describe(_monitor.Elapsed));
        sb.AppendLine($"显存峰值    {_monitor.PeakMb:0} MB（{_monitor.PeakPct:0.0}%）");
        sb.AppendLine("首次警告    " + (_monitor.FirstWarnAt?.ToString("HH:mm:ss") ?? "未触发"));
        sb.AppendLine("首次危急    " + (_monitor.FirstCritAt?.ToString("HH:mm:ss") ?? "未触发"));
        sb.AppendLine();
        sb.AppendLine("记录文件");
        sb.AppendLine("  " + (Path.GetFileName(_monitor.CsvPath) ?? "-"));
        if (_last.Gpu.HasData)
        {
            sb.AppendLine();
            sb.AppendLine($"当前显存    {_last.Gpu.MemUsedMb:0} / {_last.Gpu.MemTotalMb:0} MB");
            sb.AppendLine($"游戏显存    {_last.GameVramMb:0.0} MB");
            sb.AppendLine($"后台显存    {_last.OtherVramMb:0.0} MB");
            if (!string.IsNullOrEmpty(_last.Throttle) && _last.Throttle != "none" && _last.Throttle != "unknown")
            {
                sb.AppendLine();
                sb.AppendLine("降频原因");
                foreach (var t in _last.Throttle.Split('|'))
                {
                    sb.AppendLine("  " + t);
                }
            }
        }

        _lblMonStats.Text = sb.ToString();
    }

    /// <summary>
    /// 没有帧数可显示时的占位。
    ///
    /// 用 11F 而不是 20F：20F 下的「—」或「…」会被渲染成一条横线或三个点，
    /// 看上去像控件画错了，而不像「没有数据」。放一行明确的短词才读得懂。
    /// </summary>
    private void ShowFpsPlaceholder(string title, string detail)
    {
        _lblFpsValue.Font = Theme.H1;
        _lblFpsValue.Text = title;
        _lblFpsValue.ForeColor = Theme.Sub;
        _lblFpsDetail.ForeColor = Theme.Sub;
        _lblFpsDetail.Text = detail;
    }

    /// <summary>
    /// 刷新帧数卡片。
    ///
    /// 三条显示纪律：
    ///   1. 拿不到就写原因，不写 0 —— 显示「0 fps」是在编一个不存在的测量。
    ///   2. 颜色只按「能不能玩」分档（60 / 30），不用本机标定值 ——
    ///      这台机器的合适阈值要等真跑过数据才知道，现在标就是假的。
    ///   3. 1% Low 样本不足时直说不足，不拿两三帧算一个假极值糊过去。
    /// </summary>
    private void UpdateFps(Snapshot s)
    {
        if (_lblFpsValue is null || _lblFpsValue.IsDisposed)
        {
            return;
        }

        var f = s.Frames;

        // 先判「游戏在不在跑」，再判「帧数拿没拿到」。
        // 反过来的话，「功能开着、只是游戏没启动」会被显示成
        // 「未启用」，用户会以为开关坏了，跑去乱翻设置。
        if (!s.GameRunning)
        {
            // 非管理员时**在开局前**就说清楚。
            // 不然用户会挂着工具打完一整局，事后才发现一行帧数都没有 ——
            // 那是最让人恼火的一种失败：不报错，只是白等。
            // 依据：实测 PresentMon 1.10 起 ETW 会话必须管理员，
            // 权限不够会直接 "failed to start trace session: access denied" 退出。
            if (!_isAdmin)
            {
                ShowFpsPlaceholder("需管理员", "帧数采集需要管理员权限。\r\n请用「工具 → 以管理员身份重启…」。");
            }
            else
            {
                // 待机文案要跟着目标走：外部目标下还写「启动游戏」会让人以为
                // 工具只会认《永劫无间》。
                var want = _sampler.ExternalTarget;
                ShowFpsPlaceholder("待机", want is null
                    ? "启动游戏后自动开始采集。"
                    : "等待 " + want.ProcessName + " 运行…");
            }
            return;
        }

        if (!f.Available)
        {
            ShowFpsPlaceholder("不可用", string.IsNullOrEmpty(f.Error) ? "帧数未启用。" : f.Error);
            return;
        }

        // 已挂上 PresentMon 但还没收到帧：多半还在加载界面。
        if (f.Fps <= 0)
        {
            ShowFpsPlaceholder("连接中", "已挂上采集器，等待游戏出画面…");
            return;
        }

        _lblFpsValue.Font = Theme.Big;
        _lblFpsValue.Text = f.Fps.ToString("0") + " fps";
        _lblFpsValue.ForeColor = f.Fps >= 60 ? Theme.Green
                               : f.Fps >= 30 ? Theme.Amber
                               : Theme.Red;

        string low = f.Fps1Low > 0
            ? "1% Low " + f.Fps1Low.ToString("0") + " fps"
            : "1% Low 样本不足";

        _lblFpsDetail.ForeColor = Theme.Text;
        _lblFpsDetail.Text = low + "\r\n窗口内 " + f.Frames + " 帧";

        // 平均帧率漂亮但 1% Low 难看 —— 这正是「卡」的典型特征，
        // 在这里点破，比让用户自己去对两个数字更有用。
        if (f.Fps1Low > 0 && f.Fps1Low < f.Fps * 0.5)
        {
            _lblFpsDetail.Text += "\r\n掉帧明显：偶发长帧";
        }
    }

    /// <summary>
    /// 弹出「选择监控目标」。
    ///
    /// 取消或没选时保持原状。选了一个外部进程后：
    ///   - 采集器换成盯它（帧数、进程内存都跟着换；显存/温度是显卡级的，不受影响）
    ///   - 标签显示目标名
    ///   - 汇总里判定与建议整体闭嘴 —— 见 <see cref="MonitorSession"/> 的口径分叉
    ///
    /// 切回《永劫无间》的入口也在同一个列表里（「默认」按钮），
    /// 不让用户选了外部目标后找不到回去的路。
    /// </summary>
    private void PickTarget()
    {
        var candidates = Inventory.FindTargetCandidates();
        MonitorTarget? picked = TargetPickerForm.Pick(this, candidates);
        if (picked is null)
        {
            return;
        }

        // 恢复默认不能走「设一个 Naraka 目标」这条路：
        // 那个目标的 Pid 是 0，TryOpen 必然失败，会被误判成
        // 「监控目标已退出」还弹一条 Problem。必须清空 ExternalTarget，
        // 让采集线程回到自动探测《永劫无间》。
        if (picked.IsNaraka)
        {
            ResetTarget();
            if (_lastSnapshot is not null)
            {
                UpdateFps(_lastSnapshot);
            }
            return;
        }

        _sampler.ExternalTarget = picked;
        _lblTarget.Text = picked.DisplayName;
        _lblTarget.ForeColor = Theme.Blue;
        AddLog("监控目标已切换为：" + picked.DisplayName
            + "（只报观测值，不给判定与建议）");

        // 目标变了，帧数卡片立刻重画一次。
        // 不等到下一个采样周期：切换后那 1.5 秒里卡片还显示着上一个目标的
        // 帧率，用户会以为切换没生效。
        if (_lastSnapshot is not null)
        {
            UpdateFps(_lastSnapshot);
        }
    }

    /// <summary>把目标重置回《永劫无间》。</summary>
    private void ResetTarget()
    {
        _sampler.ExternalTarget = null;
        _lblTarget.Text = "永劫无间";
        _lblTarget.ForeColor = Theme.Sub;
        AddLog("监控目标已恢复为《永劫无间》（判定与建议可用）。");
    }

    /// <summary>
    /// 按采样线程解析出的目标刷新标签。
    /// <see cref="Sampler.CurrentTarget"/> 是不可变 record，跨线程读到的
    /// 要么是旧值要么是新值，不会读到半个 —— 所以这里不加锁。
    /// </summary>
    private void UpdateTargetLabel()
    {
        if (_lblTarget is null || _lblTarget.IsDisposed)
        {
            return;
        }

        var wanted = _sampler.ExternalTarget;
        var actual = _sampler.CurrentTarget;

        // 没设外部目标：正常显示《永劫无间》。
        if (wanted is null)
        {
            if (_lblTarget.Text != "永劫无间")
            {
                _lblTarget.Text = "永劫无间";
                _lblTarget.ForeColor = Theme.Sub;
            }
            return;
        }

        // 设了外部目标但进程已退出：明说，不要留着一个假装在盯的标签。
        if (actual is null || actual.Pid != wanted.Pid)
        {
            _lblTarget.Text = wanted.ProcessName + "（已退出）";
            _lblTarget.ForeColor = Theme.Red;
            return;
        }

        if (_lblTarget.Text != actual.DisplayName)
        {
            _lblTarget.Text = actual.DisplayName;
            _lblTarget.ForeColor = actual.IsNaraka ? Theme.Sub : Theme.Blue;
        }
    }

    private void UpdateMonitor(Snapshot s)
    {
        _lastSnapshot = s;

        // 目标标签跟着**实际盯上的**目标走，而不是用户设的值。
        // 两者会不一致：设了外部目标、但那个进程已经退出时，
        // 标签该显示「已退出」，而不是假装还在盯它。
        UpdateTargetLabel();
        if (_chart is null || _chart.IsDisposed)
        {
            return;
        }

        UpdateFps(s);

        // 曲线中央那句话也按实际情况给。没有 NVIDIA 显卡时若还写
        // 「等待显卡数据…」，等于让人一直等一个不会来的数字。
        _chart.EmptyText = NvidiaSmi.Path is null ? NvidiaSmi.UnavailableReason : "正在读取显卡数据…";

        // 曲线一直都在画 —— 记录到文件是另一回事。先看得见，再谈留档。
        if (s.Gpu.HasData)
        {
            _chart.Push(s.Gpu.MemUsedMb, s.Gpu.MemTotalMb);

            // 提醒也一直都在评估，与「是否在记录」无关。
            // 放在这里是因为 UpdateMonitor 每个采样周期都会被调用，
            // 而且窗口最小化时 OnSnapshot 照样会走到 —— 挂着打游戏正是这个场景。
            _notifier.Update(s);
        }

        if (_monitor.Active)
        {
            _monitor.Record(s);
        }

        if (_lblMonBanner is null || _lblMonBanner.IsDisposed)
        {
            return;
        }

        if (!s.Gpu.HasData)
        {
            // 三种情况必须分开说，不能一律「等待显卡数据…」：
            //  · 还没有 NVIDIA 显卡 → 那是环境限制，等也等不来，要说清楚
            //  · 刚启动还没采到    → 这才是「等待」
            //  · 采到过又没了      → 驱动异常，值得排查
            _lblMonBanner.Text = NvidiaSmi.Path is null ? NvidiaSmi.UnavailableHint
                               : _gpuSeen ? "显卡数据中断（驱动未响应？）。上一次是能读到的。"
                               : "正在读取显卡数据…";
            _lblMonBanner.ForeColor = NvidiaSmi.Path is null ? Theme.Amber : Theme.Sub;
            return;
        }

        double pct = s.Gpu.Pct;

        // 提醒关着的时候明确说一句，否则用户会觉得「越线了怎么没气泡」。
        string off = _notifier.Enabled ? string.Empty : "（提醒已关闭）";

        if (pct >= _monitor.CritPct)
        {
            _lblMonBanner.ForeColor = Theme.Red;
            _lblMonBanner.Text = $"显存告急：{pct:0.0}%（{s.Gpu.MemUsedMb:0}/{s.Gpu.MemTotalMb:0} MB）{off}。"
                               + "打完这一场就退回大厅刷新 —— 这是唯一安全归还显存的方式。";
        }
        else if (pct >= _monitor.WarnPct)
        {
            _lblMonBanner.ForeColor = Theme.Amber;
            _lblMonBanner.Text = $"显存接近上限：{pct:0.0}%{off}。再打一两局就该回大厅刷新了。";
        }
        else
        {
            _lblMonBanner.ForeColor = Theme.Green;
            _lblMonBanner.Text = $"显存正常：{pct:0.0}%，剩余 {s.Gpu.FreeMb:0} MB。"
                               + (s.GameRunning ? string.Empty : "（游戏未运行）");
        }
    }
}
