using System.Text;
using GameHelper.Core;

namespace GameHelper.UI;

/// <summary>
/// 界面结构检查（--dryrun）。
///
/// 覆盖的是那种「编译能过、打开就崩」的问题：控件没建出来、
/// 表格列数不对、右对齐元素在小窗口下和左边的内容叠在一起。
/// 这些都不会被编译器发现，但都发生过。
/// </summary>
internal sealed partial class MainForm
{
    internal string BuildDryRunReport()
    {
        var sb = new StringBuilder();
        bool allOk = true;

        sb.AppendLine("游戏助手 · 界面构建检查（--dryrun）");
        sb.AppendLine(new string('=', 64));
        sb.AppendLine("时间      : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("窗口尺寸  : " + ClientSize.Width + " x " + ClientSize.Height);
        sb.AppendLine("最小尺寸  : " + MinimumSize.Width + " x " + MinimumSize.Height);
        sb.AppendLine();

        sb.AppendLine("【结构】");
        sb.AppendLine("  根布局行数     : " + _root.RowCount + " 行");
        sb.AppendLine("  页签           : " + _tabs.TabPages.Count + " 个 -> " + DescribeTabs());
        sb.AppendLine("  控件总数       : " + CountControls(this) + " 个");
        sb.AppendLine("  按钮总数       : " + CountButtons(this) + " 个");
        sb.AppendLine("  进程列表列数   : " + _procList.Columns.Count);
        sb.AppendLine("  扫描表列数     : " + _grid.Columns.Count);
        sb.AppendLine("  显存曲线控件   : " + (_chart is not null ? "已创建" : "缺失"));
        sb.AppendLine("  显存占用条     : " + (_bar is not null ? "已创建" : "缺失"));
        sb.AppendLine("  日志文本框     : " + (_logBox is not null ? "已创建" : "缺失"));
        sb.AppendLine("  托盘图标       : " + (_tray is not null ? "已创建" : "缺失"));
        sb.AppendLine("  路径输入框     : " + (_txtGameDir is not null && _txtBackupRoot is not null ? "已创建" : "缺失"));
        sb.AppendLine("  显卡筛选复选框 : " + (_chkPrimaryOnly is not null && _chkOnlyOther is not null ? "已创建" : "缺失"));
        sb.AppendLine("  显存提醒开关   : " + (_chkVramNotify is not null ? "已创建（勾选=" + _chkVramNotify.Checked + "）" : "缺失"));
        sb.AppendLine("  托盘提醒勾选项 : " + (_miTrayNotify is not null ? "已创建（勾选=" + _miTrayNotify.Checked + "）" : "缺失"));
        sb.AppendLine();

        Check(sb, "页签数量为 3（总览 / 监控 / 清理）", _tabs.TabPages.Count == 3, ref allOk);
        Check(sb, "每个页签都有内容", _tabs.TabPages.Cast<TabPage>().All(p => CountControls(p) > 1), ref allOk);
        Check(sb, "进程列表 6 列（进程/显卡/本机/共享/类别/说明）", _procList.Columns.Count == 6, ref allOk);
        Check(sb, "扫描表 6 列（选择/名称/类型/大小/标记/状态）", _grid.Columns.Count == 6, ref allOk);
        Check(sb, "按钮数量不少于 12 个", CountButtons(this) >= 12, ref allOk);
        Check(sb, "显存提醒的界面开关与托盘勾选项状态一致",
            _chkVramNotify is not null && _miTrayNotify is not null
            && _chkVramNotify.Checked == _miTrayNotify.Checked
            && _chkVramNotify.Checked == _notifier.Enabled, ref allOk);
        Check(sb, "显存提醒阈值与记录阈值一致（不出现两套数字）",
            Math.Abs(_notifier.WarnPct - _monitor.WarnPct) < 0.001
            && Math.Abs(_notifier.CritPct - _monitor.CritPct) < 0.001, ref allOk);

        var badSize = new List<string>();
        CollectBadSizes(this, badSize);
        sb.AppendLine("  尺寸异常的控件 : " + (badSize.Count == 0 ? "无" : string.Join("、", badSize)));
        Check(sb, "没有尺寸异常的控件", badSize.Count == 0, ref allOk);

        var emptyButtons = new List<string>();
        CollectEmptyButtons(this, emptyButtons);
        Check(sb, "所有按钮都有文字", emptyButtons.Count == 0, ref allOk);
        sb.AppendLine();

        // ---- 概览卡片的重排 ----
        sb.AppendLine("【概览卡片重排（右对齐元素不越界、不重叠）】");

        // 卡片平时是 Dock=Fill，直接赋 Size 会被布局覆盖掉。先摘掉 Dock
        // 才能真正测到各种宽度下的排布 —— 否则三种宽度量出来的结果完全一样，
        // 检查看着通过、其实什么都没测。
        var savedDock = _header.Dock;
        var savedSize = _header.Size;
        _header.Dock = DockStyle.None;

        int[] widths = { 1200, 976, 820, 700 };
        bool layoutOk = true;
        try
        {
            foreach (int w in widths)
            {
                _header.Size = new Size(w, HeaderHeight);
                LayoutHeader();

                int leftRight = Math.Max(_lblGpu.Right, Math.Max(_lblVram.Right, _lblSub.Right));
                int rightLeft = Math.Min(_lblAdmin.Left, Math.Min(_lblGame.Left, _lblRam.Left));
                bool noOverlap = leftRight <= rightLeft;
                bool barFits = _bar!.Right <= w;
                bool inHeight = _lblSub.Bottom <= HeaderHeight;

                if (!noOverlap || !barFits || !inHeight)
                {
                    layoutOk = false;
                }

                sb.AppendLine($"  宽度 {w,4} : 左侧最右 {leftRight,4} / 右侧最左 {rightLeft,4}"
                              + $"  进度条右缘 {_bar.Right,4}"
                              + $"  ->  {(noOverlap && barFits && inHeight ? "正常" : "有问题")}");
            }
        }
        finally
        {
            _header.Dock = savedDock;
            _header.Size = savedSize;
            LayoutHeader();
        }

        Check(sb, $"概览卡片在 {string.Join(" / ", widths)} 四种宽度下都不重叠", layoutOk, ref allOk);
        sb.AppendLine();

        // ---- 子窗体 ----
        sb.AppendLine("【子窗体构建】");
        try
        {
            using var about = new AboutForm();
            int n = CountControls(about) + 1;
            sb.AppendLine("  介绍窗口       : 控件 " + n + " 个，"
                          + about.ClientSize.Width + " x " + about.ClientSize.Height);
            Check(sb, "介绍窗口构建成功", n >= 5, ref allOk);
        }
        catch (Exception ex)
        {
            sb.AppendLine("  介绍窗口构建失败 : " + ex.Message);
            Check(sb, "介绍窗口构建成功", false, ref allOk);
        }

        try
        {
            using var report = new TextReportForm("测试报告", "第一行\n第二行\n", null);
            int n = CountControls(report) + 1;
            sb.AppendLine("  文本查看窗口   : 控件 " + n + " 个");
            Check(sb, "文本查看窗口构建成功", n >= 5, ref allOk);
        }
        catch (Exception ex)
        {
            sb.AppendLine("  文本查看窗口构建失败 : " + ex.Message);
            Check(sb, "文本查看窗口构建成功", false, ref allOk);
        }
        sb.AppendLine();

        // ---- 数据通路 ----
        sb.AppendLine("【数据通路】");
        sb.AppendLine("  nvidia-smi     : " + (NvidiaSmi.Path ?? "未找到"));
        sb.AppendLine("  探针可用性     : " + _sampler.ProbeAvailable
                          + (_sampler.ProbeAvailable ? string.Empty : "（" + (_sampler.ProbeError ?? "未知") + "）"));
        sb.AppendLine("  采样间隔       : " + _sampler.IntervalMs + " ms");
        sb.AppendLine("  输出目录       : " + Core.AppPaths.OutputDir);
        sb.AppendLine("  设置文件       : " + _settings.Path);
        sb.AppendLine();

        sb.AppendLine(new string('=', 64));
        sb.AppendLine(allOk ? "界面检查完成：全部通过。" : "界面检查完成：存在未通过项，见上方 [失败] 行。");

        return sb.ToString();
    }

    private string DescribeTabs()
    {
        var names = new List<string>();
        foreach (TabPage p in _tabs.TabPages)
        {
            names.Add(p.Text + "(" + CountControls(p) + ")");
        }
        return string.Join("、", names);
    }

    private static int CountControls(Control root)
    {
        int n = 0;
        foreach (Control c in root.Controls)
        {
            n++;
            n += CountControls(c);
        }
        return n;
    }

    private static int CountButtons(Control root)
    {
        int n = 0;
        foreach (Control c in root.Controls)
        {
            if (c is Button)
            {
                n++;
            }
            n += CountButtons(c);
        }
        return n;
    }

    private static void CollectBadSizes(Control root, List<string> bad)
    {
        foreach (Control c in root.Controls)
        {
            if (c.Width < 0 || c.Height < 0)
            {
                bad.Add(c.GetType().Name + "(" + c.Name + ")");
            }
            CollectBadSizes(c, bad);
        }
    }

    private static void CollectEmptyButtons(Control root, List<string> bad)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Button b && string.IsNullOrWhiteSpace(b.Text))
            {
                bad.Add(b.Name.Length > 0 ? b.Name : "(未命名按钮)");
            }
            CollectEmptyButtons(c, bad);
        }
    }

    private static void Check(StringBuilder sb, string name, bool ok, ref bool allOk)
    {
        if (!ok)
        {
            allOk = false;
        }
        sb.AppendLine($"  [{(ok ? "通过" : "失败")}] {name}");
    }

    // ------------------------------------------------------------------ 截图模式

    internal int TabPageCount => _tabs.TabPages.Count;

    internal string TabPageTitle(int index) =>
        index >= 0 && index < _tabs.TabPages.Count ? _tabs.TabPages[index].Text : "?";

    internal void SelectTabPage(int index)
    {
        if (index >= 0 && index < _tabs.TabPages.Count)
        {
            _tabs.SelectedIndex = index;
        }
    }

    internal bool HasLiveData => _last != Snapshot.Empty && _last.Gpu.HasData;

    /// <summary>
    /// 逐个页签截图并保存。
    ///
    /// 用 DrawToBitmap 而不是抓屏：这样截的是控件自己画出来的内容，
    /// 不依赖窗口在最前、不受其他窗口遮挡影响，也不需要屏幕捕获权限。
    /// 采样线程的事件靠 DoEvents 泵出来，所以截到的数字是真实的。
    /// </summary>
    internal void CaptureScreenshots(string dir)
    {
        Directory.CreateDirectory(dir);

        StartPosition = FormStartPosition.Manual;
        Location = new Point(60, 40);
        Show();

        // 等第一份真实快照到达（最多 8 秒）
        var deadline = DateTime.Now.AddSeconds(8);
        while (DateTime.Now < deadline && !HasLiveData)
        {
            Application.DoEvents();
            Thread.Sleep(40);
        }

        // 再泵一会儿，让占用列表也填上
        Pump(900);

        for (int i = 0; i < TabPageCount; i++)
        {
            SelectTabPage(i);
            Pump(700);

            string path = Path.Combine(dir, $"{i + 1}-{TabPageTitle(i)}.png");
            CaptureTo(path);
        }

        // 介绍窗口（帮助菜单打开时的样子）
        try
        {
            using var about = new AboutForm();
            about.StartPosition = FormStartPosition.Manual;
            about.Location = new Point(60, 40);
            about.Show();
            Pump(500);
            using var bmp = new Bitmap(about.Width, about.Height);
            about.DrawToBitmap(bmp, new Rectangle(0, 0, about.Width, about.Height));
            bmp.Save(Path.Combine(dir, "4-介绍与边界.png"), System.Drawing.Imaging.ImageFormat.Png);
            about.Close();
        }
        catch
        {
            // 截图失败不影响主流程
        }

        // 首次启动的导览窗口（tour 模式：多一个勾选、按钮写「开始使用」）。
        // 单独截一张是因为它只出现一次，出问题时最难被发现 ——
        // 布局被切掉的话，用户看到的就是一句没说完的话。
        try
        {
            using var tour = new AboutForm(tour: true);
            tour.StartPosition = FormStartPosition.Manual;
            tour.Location = new Point(60, 40);
            tour.Show();
            Pump(500);
            using var bmp = new Bitmap(tour.Width, tour.Height);
            tour.DrawToBitmap(bmp, new Rectangle(0, 0, tour.Width, tour.Height));
            bmp.Save(Path.Combine(dir, "5-首次导览.png"), System.Drawing.Imaging.ImageFormat.Png);
            tour.Close();
        }
        catch
        {
            // 同上
        }

        Pump(200);
        Close();
    }

    private void CaptureTo(string path)
    {
        using var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void Pump(int milliseconds)
    {
        var until = DateTime.Now.AddMilliseconds(milliseconds);
        while (DateTime.Now < until)
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }
    }
}
