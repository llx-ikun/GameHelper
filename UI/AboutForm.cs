namespace GameHelper.UI;

/// <summary>
/// 功能导览 / 介绍与能力边界。
///
/// 这个窗口同时充当两个角色：**首次启动的导览**和**帮助菜单里的说明**。
/// 刻意不做成两个窗口 —— 内容一旦分家，改了这边忘那边，
/// 最后用户看到的说明和实际功能对不上，那比没有说明更糟。
///
/// 存在的理由很具体：工具能做的事和做不到的事差别很大，
/// 而用户往往默认「显存清理软件」能凭空变出显存。把边界写清楚，
/// 比让用户猜要诚实得多 —— 也能避免有人以为这工具没生效。
/// </summary>
internal sealed class AboutForm : Form
{
    private readonly CheckBox _chkNoMore = new();

    /// <summary>导览模式下：用户是否希望以后不再自动弹出。</summary>
    internal bool DontShowAgain => _chkNoMore.Checked;

    /// <param name="tour">
    /// true = 首次启动的导览（标题不同、多一个「不再自动显示」勾选、按钮写「开始使用」）；
    /// false = 从帮助菜单打开的普通说明。
    /// </param>
    internal AboutForm(bool tour = false)
    {
        SuspendLayout();

        Text = tour ? "功能导览 · 欢迎使用游戏助手" : "功能导览与能力边界 · 游戏助手";
        Icon = Theme.AppIcon();
        ClientSize = tour ? new Size(780, 660) : new Size(720, 600);
        MinimumSize = new Size(600, 440);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Bg;
        Font = Theme.Ui;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ShowInTaskbar = false;
        MinimizeBox = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Bg,
            Padding = new Padding(12),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));

        var box = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Card,
            ForeColor = Theme.Text,
            Font = Theme.Ui,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false,
        };
        Fill(box, tour);
        box.Select(0, 0);
        box.SelectionLength = 0;
        root.Controls.Add(box, 0, 0);

        // 占满整行、高度 0 的空行：勾选项只在导览模式出现，
        // 普通模式下这行留着也看不见，省得两套布局各写一遍。
        root.RowStyles[1] = new RowStyle(SizeType.Absolute, tour ? 30F : 0F);
        if (tour)
        {
            _chkNoMore.Text = "启动时不再自动显示（帮助菜单里随时可以再打开）";
            _chkNoMore.AutoSize = true;
            _chkNoMore.Checked = true;      // 默认「只弹一次」—— 这正是想要的行为
            _chkNoMore.ForeColor = Theme.Sub;
            _chkNoMore.Font = Theme.Ui;
            _chkNoMore.BackColor = Theme.Bg;
            _chkNoMore.Margin = new Padding(0, 6, 0, 0);
            root.Controls.Add(_chkNoMore, 0, 1);
        }

        var btnClose = Theme.MakeButton(tour ? "开始使用" : "知道了", Theme.Blue, 110, 32);
        btnClose.Click += (_, _) => Close();

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Bg,
            Padding = new Padding(0, 8, 0, 0),
            Margin = new Padding(0),
        };
        bar.Controls.Add(btnClose);
        root.Controls.Add(bar, 0, 2);

        Controls.Add(root);
        AcceptButton = btnClose;
        CancelButton = btnClose;

        ResumeLayout(false);
    }

    private static void Fill(RichTextBox box, bool tour)
    {
        H(box, "游戏助手");
        P(box, "一个给游戏玩家用的显存看护工具：实时显示显卡显存占用，"
              + "在显存将满时提醒你，并帮你结束占用显存的无关后台程序。");
        P(box, "它不能凭空变出显存，只能帮你把该省的省下来、该提醒的提醒到。");

        if (tour)
        {
            H(box, "【先说结论：我该看哪里】");
            P(box, "· 只想知道显存还够不够 → 看「总览」", 2);
            P(box, "· 想弄清「越玩越卡」到底是什么病 → 用「监控」，并留意右上角的帧数", 2);
            P(box, "· 想清掉游戏目录里的第三方残留 → 用「清理」", 2);
            P(box, "· 想知道刚才发生了什么 → 看最下方的「运行日志」", 2);
        }

        H(box, "【四个页签分别做什么】");
        P(box, "总览　显示显卡、显存、温度、占用，以及每个进程占了多少显存。"
              + "勾选「其他后台程序」后可以一键结束它们，把显存拿回来。");
        P(box, "监控　实时画出显存占用曲线，可以设置警告/危急两个阈值。"
              + "点「开始记录」会把每次采样写进 CSV，供事后排查。"
              + "右上角同时显示**真实帧数**（当前 FPS 与 1% Low）。");
        P(box, "清理　扫描游戏目录里属于第三方开源项目的残留文件，"
              + "把它们移到备份目录（移动，不删除），随时可以还原。"
              + "游戏目录会自动识别，一般不用手填。");
        P(box, "日志　所有操作的记录，可以导出成文本文件。");

        H(box, "【帧数：判断「越玩越卡」到底是什么原因的关键】");
        P(box, "这是本工具唯一能直接区分病因的数据。三种情况对应三个完全不同的结论：", 2);
        P(box, "· 平均帧率不低，但 1% Low 很低 → 卡顿来自偶发长帧（着色器编译、读盘）。"
              + "清后台程序治不好这个。", 2);
        P(box, "· 帧率随对局时长下降，且同期显存也在涨 → 显存随对局累积。"
              + "退回大厅刷新即可，这正是本工具对口的场景。", 2);
        P(box, "· 帧率下降，但显存、温度、频率都正常 → 瓶颈不在本机，更可能是网络或服务器。"
              + "这种情况下请不要再折腾显卡设置。", 2);
        P(box, "注意：读取帧数需要管理员权限（Windows 只允许管理员启动性能跟踪会话）。"
              + "普通权限下帧数那里会显示「需管理员」—— 用菜单「工具 → 以管理员身份重启…」即可。", 2);

        H(box, "【三条技术边界（务必看清）】");
        P(box, "一、游戏自身的显存无法从外部释放。");
        P(box, "　　Windows 的 WDDM 显存管理器只允许「拥有该显存的进程」自己归还显存。"
              + "外部强行释放的唯一手段是注入游戏进程，而《永劫无间》的内核级反作弊"
              + "会判定为篡改，存在封号风险。所以本工具绝不做这件事。", 14);
        P(box, "二、「挂起」进程不会释放任何显存。");
        P(box, "　　这是实测结论，不是推测：一个持有 88.67 MB 显存的后台程序被冻结 5 秒，"
              + "占用始终是 88.67 MB，一点没少。只有进程真正退出，显存才会释放。"
              + "所以本工具用「结束进程」，而不是「挂起」。", 14);
        P(box, "三、因此，唯一有效且安全的手段只有两条：");
        P(box, "　　· 关闭无关后台程序 —— 进程退出后才释放显存；", 14);
        P(box, "　　· 显存将满时退回大厅刷新 —— 让游戏引擎自己拆掉渲染目标、重新分配。", 14);

        H(box, "【关于越线提醒】");
        P(box, "提醒走的是系统托盘气泡，不是弹出对话框。这是有意设计的："
              + "对话框会抢走焦点，在全屏游戏里足以把游戏踢出全屏，"
              + "对一个「给游戏用的工具」来说反而帮倒忙。");
        P(box, "提醒默认开启。不点「开始记录」也会照常提醒 —— 随手挂着工具打游戏，"
              + "恰恰是最需要被提醒的场景。");

        H(box, "【清理功能的风险提示】");
        P(box, "清理游戏目录务必在游戏与反作弊完全退出后进行，否则可能损坏文件。"
              + "本工具已内置硬门禁：只要 NarakaBladepoint 或 Neac* 进程还活着，"
              + "隔离功能就会被直接禁用。");
        P(box, "另外，" + Core.CleanerService.Protected.Count
              + " 项已确认的游戏文件被列进硬保护名单，即使误操作也不会被触碰。"
              + "清单里有些看着像第三方组件（例如 nvngx_dlss.dll），"
              + "但它们其实是游戏随包分发的，删掉会导致游戏起不来。");

        H(box, "【换一台电脑也能用】");
        P(box, "本程序是单个 exe，不需要安装，也不要求装 .NET 运行环境 —— 拷过去双击就跑。");
        P(box, "游戏目录会自动识别（先看运行中的游戏进程，再查 Steam 的库登记，"
              + "最后扫各盘的常见位置），不依赖写死的盘符；备份目录跟着游戏所在的盘走。"
              + "识别不到时会在「清理」页明确提示，而不是悄悄给一个错路径。");
        P(box, "需要说明的是：显存曲线、温度、占用这些来自 NVIDIA 的 nvidia-smi。"
              + "在 AMD / Intel 显卡的机器上读不到，界面会直接说明原因，"
              + "其余功能（清理、日志、帧数）不受影响。");

        H(box, "【典型用法】");
        P(box, "· 开打前：切到「监控」，点「开始记录」，然后开游戏。显存贴顶时会收到托盘提醒。");
        P(box, "· 想定性卡顿原因：以管理员身份运行，打完一局后看汇总里的「平均帧率 / 1% Low」。");
        P(box, "· 想查谁在占显存：「总览」页随时看，一秒刷新一次。");
        P(box, "· 清完游戏目录后：在 Steam 里右键游戏 -> 属性 -> 已安装文件 -> 验证游戏文件完整性。");

        P(box, "");
        P(box, "（按 F1 可以随时重新打开这份说明。）");
    }

    private static void H(RichTextBox box, string text) =>
        Append(box, text + Environment.NewLine, Theme.H2, Theme.Blue);

    private static void P(RichTextBox box, string text, int indent = 0) =>
        Append(box, new string(' ', indent) + text + Environment.NewLine, Theme.Ui, Theme.Text);

    private static void Append(RichTextBox box, string text, Font font, Color color)
    {
        int start = box.TextLength;
        box.AppendText(text);
        box.Select(start, text.Length);
        box.SelectionFont = font;
        box.SelectionColor = color;
    }
}
