using System.Diagnostics;
using System.Text;
using GameHelper.Core;

namespace GameHelper.UI;

/// <summary>总览页：显存占用者列表、分类汇总、以及几个即时操作。</summary>
internal sealed partial class MainForm
{
    private ListView _procList = null!;
    private Label _lblSum = null!;
    private Label _lblAdapterNote = null!;
    private CheckBox _chkOnlyOther = null!;
    private CheckBox _chkPrimaryOnly = null!;
    private Button _btnKill = null!;

    private void BuildTabs()
    {
        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = Theme.Ui,
            Margin = new Padding(10, 0, 10, 6),
            Padding = new Point(18, 6),
        };

        _tabs.TabPages.Add(BuildOverviewTab());
        _tabs.TabPages.Add(BuildMonitorTab());
        _tabs.TabPages.Add(BuildCleanupTab());

        _root.Controls.Add(_tabs, 0, 2);
    }

    private TabPage BuildOverviewTab()
    {
        var page = new TabPage("总览")
        {
            BackColor = Theme.Bg,
            Padding = new Padding(8),
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Bg,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 106F));

        // ---- 进程列表 ----
        var grp = Theme.MakeGroup("  谁在占用显存（实时）  ");
        grp.Dock = DockStyle.Fill;
        grp.Margin = new Padding(0, 0, 0, 6);

        _procList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BorderStyle = BorderStyle.FixedSingle,
            Scrollable = true,
            CheckBoxes = true,
            Font = Theme.Ui,
            BackColor = Theme.Card,
            ForeColor = Theme.Text,
        };
        _procList.Columns.Add("进程", 270);
        _procList.Columns.Add("显卡", 152);
        _procList.Columns.Add("本机显存", 100, HorizontalAlignment.Right);
        _procList.Columns.Add("共享显存", 100, HorizontalAlignment.Right);
        _procList.Columns.Add("类别", 70);
        _procList.Columns.Add("说明", -2);

        // 复选框只对「其他后台程序」生效。游戏和 Windows 组件的勾选会被挡回，
        // 并给出原因 —— 静默忽略会让人以为程序失灵了。
        _procList.ItemCheck += (_, e) =>
        {
            if (e.NewValue != CheckState.Checked)
            {
                return;
            }

            if (_procList.Items[e.Index].Tag is not ProcVram p)
            {
                return;
            }

            if (p.Class != ProcClass.Other)
            {
                e.NewValue = CheckState.Unchecked;
                AddLog(p.Class == ProcClass.Game
                    ? $"「{p.Name}」是游戏自身，结束它只会让游戏崩掉，且显存一样不会因此释放。"
                    : $"「{p.Name}」是 Windows 组件，结束它会出问题，已阻止勾选。");
                return;
            }

            if (!p.OnPrimaryAdapter)
            {
                // 这是本工具最容易让人误解的地方，必须说清楚：
                // 进程的显存在哪块显卡上，决定了结束它有没有意义。
                e.NewValue = CheckState.Unchecked;
                AddLog($"「{p.Name}」占用的是「{p.AdapterName}」的显存，"
                     + "结束它不会释放游戏所用显卡的显存，所以不予勾选。");
            }
        };

        grp.Controls.Add(_procList);
        grid.Controls.Add(grp, 0, 0);

        // ---- 汇总与按钮 ----
        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _lblSum = Theme.MakeLabel("分类汇总：读取中…", Theme.Sub, Theme.Ui);
        _lblSum.AutoSize = false;
        _lblSum.AutoEllipsis = true;
        _lblSum.Dock = DockStyle.Fill;
        _lblSum.TextAlign = ContentAlignment.MiddleLeft;

        _lblAdapterNote = Theme.MakeLabel(string.Empty, Theme.Sub, Theme.UiSm);
        _lblAdapterNote.AutoSize = false;
        _lblAdapterNote.AutoEllipsis = true;
        _lblAdapterNote.Dock = DockStyle.Fill;
        _lblAdapterNote.TextAlign = ContentAlignment.MiddleLeft;

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };

        _btnKill = Theme.MakeButton("结束勾选的进程（释放显存）", Theme.Red, 226, 32);
        _btnKill.Click += (_, _) => KillSelected();
        _btnKill.Enabled = false;

        var btnCheckAll = Theme.MakeGhostButton("勾选全部后台程序", 150, 32);
        btnCheckAll.Click += (_, _) => CheckAllOthers();

        var btnUncheck = Theme.MakeGhostButton("取消勾选", 96, 32);
        btnUncheck.Click += (_, _) =>
        {
            _checkedPids.Clear();
            foreach (ListViewItem it in _procList.Items)
            {
                it.Checked = false;
            }
        };

        var btnReport = Theme.MakeGhostButton("生成显存体检报告…", 170, 32);
        btnReport.Click += (_, _) => ShowHealthReport();

        _chkPrimaryOnly = new CheckBox
        {
            Text = "只显示游戏所用显卡",
            Checked = true,
            AutoSize = true,
            ForeColor = Theme.Text,
            Font = Theme.Ui,
            Margin = new Padding(10, 7, 0, 0),
            BackColor = Theme.Bg,
        };
        _chkPrimaryOnly.CheckedChanged += (_, _) => RepaintOverview();

        _chkOnlyOther = new CheckBox
        {
            Text = "仅显示后台程序",
            AutoSize = true,
            ForeColor = Theme.Text,
            Font = Theme.Ui,
            Margin = new Padding(10, 7, 0, 0),
            BackColor = Theme.Bg,
        };
        _chkOnlyOther.CheckedChanged += (_, _) => RepaintOverview();

        flow.Controls.Add(_btnKill);
        flow.Controls.Add(btnCheckAll);
        flow.Controls.Add(btnUncheck);
        flow.Controls.Add(btnReport);
        flow.Controls.Add(_chkPrimaryOnly);
        flow.Controls.Add(_chkOnlyOther);

        bottom.Controls.Add(_lblSum, 0, 0);
        bottom.Controls.Add(_lblAdapterNote, 0, 1);
        bottom.Controls.Add(flow, 0, 2);
        grid.Controls.Add(bottom, 0, 1);

        page.Controls.Add(grid);
        return page;
    }

    private void RepaintOverview()
    {
        if (_last != Snapshot.Empty)
        {
            UpdateOverview(_last);
        }
    }

    private void UpdateOverview(Snapshot s)
    {
        if (_procList is null || _procList.IsDisposed)
        {
            return;
        }

        bool onlyOther = _chkOnlyOther?.Checked == true;
        bool primaryOnly = _chkPrimaryOnly?.Checked != false;

        var rows = new List<ProcVram>(s.Procs.Count);
        foreach (var p in s.Procs)
        {
            if (primaryOnly && !p.OnPrimaryAdapter)
            {
                continue;
            }
            if (onlyOther && p.Class != ProcClass.Other)
            {
                continue;
            }
            rows.Add(p);
        }

        // 保留仍然存在的勾选项，避免每秒重建列表时把用户的勾选清掉。
        var alive = new HashSet<int>(
            s.Procs.Where(p => p.Class == ProcClass.Other && p.OnPrimaryAdapter).Select(p => p.Pid));
        _checkedPids.RemoveWhere(pid => !alive.Contains(pid));

        using (new RedrawSuspender(_procList))
        {
            _procList.BeginUpdate();
            try
            {
                _procList.Items.Clear();

                int n = 0;
                foreach (var p in rows)
                {
                    n++;
                    if (n > 60)
                    {
                        break;
                    }

                    var it = new ListViewItem(p.Name) { Tag = p };
                    it.SubItems.Add(p.AdapterName);
                    it.SubItems.Add(p.LocalMb.ToString("0.0") + " MB");
                    it.SubItems.Add(p.SharedMb.ToString("0.0") + " MB");
                    it.SubItems.Add(p.ClassText);
                    it.SubItems.Add(p.Note);

                    if (p.Class == ProcClass.Game)
                    {
                        it.ForeColor = Theme.Red;
                    }
                    else if (p.Class == ProcClass.System)
                    {
                        it.ForeColor = Theme.Sub;
                    }
                    else if (!p.OnPrimaryAdapter)
                    {
                        // 其他显卡上的占用一律弱化显示，避免误导。
                        it.ForeColor = Theme.Gray;
                    }

                    if (p.Class == ProcClass.Other && p.OnPrimaryAdapter && _checkedPids.Contains(p.Pid))
                    {
                        it.Checked = true;
                    }

                    _procList.Items.Add(it);
                }
            }
            finally
            {
                _procList.EndUpdate();
            }
        }

        if (_lblSum is not null && !_lblSum.IsDisposed)
        {
            var sb = new StringBuilder();
            // 用短名（"NVIDIA RTX 3050"）而不是全名，否则这一行会被挤到裁切。
            sb.Append($"分类汇总（{DescribePrimaryAdapter(s)}）：游戏自身 {s.GameVramMb:0.0} MB（不可外部释放）");
            sb.Append($"｜ Windows 组件 {s.SystemVramMb:0.0} MB（勿关）");
            sb.Append($"｜ 其他后台 {s.OtherVramMb:0.0} MB（可结束回收）");
            if (s.UnexplainedMb > 1)
            {
                sb.Append($"｜ 驱动/未归因 {s.UnexplainedMb:0.0} MB");
            }
            _lblSum.Text = sb.ToString();
        }

        if (_lblAdapterNote is not null && !_lblAdapterNote.IsDisposed)
        {
            _lblAdapterNote.Text = BuildAdapterNote(s);
            _lblAdapterNote.ForeColor = s.OtherAdapterVramMb > 1 ? Theme.Amber : Theme.Sub;
        }

        // 没勾选任何东西时，「结束进程」就该是灰的。
        if (_btnKill is not null && !_btnKill.IsDisposed)
        {
            _btnKill.Enabled = _checkedPids.Count > 0;
        }

        // 记住勾选状态
        foreach (ListViewItem it in _procList.Items)
        {
            if (it.Tag is ProcVram p && p.Class == ProcClass.Other && p.OnPrimaryAdapter)
            {
                if (it.Checked)
                {
                    _checkedPids.Add(p.Pid);
                }
                else
                {
                    _checkedPids.Remove(p.Pid);
                }
            }
        }
    }

    private static string DescribePrimaryAdapter(Snapshot s)
    {
        if (string.IsNullOrEmpty(s.PrimaryAdapterName))
        {
            return s.AdapterCount == 0 ? "未能识别显卡适配器" : "全部显卡合计";
        }

        // 短名：把 "NVIDIA GeForce RTX 3050 Laptop GPU" 收成 "NVIDIA RTX 3050"
        var primary = s.Adapters?.FirstOrDefault(a =>
            a.Name.Equals(s.PrimaryAdapterName, StringComparison.OrdinalIgnoreCase));
        return primary?.ShortName ?? s.PrimaryAdapterName;
    }

    /// <summary>
    /// 第二行提示。多显卡环境必须给出这一句，否则「可回收 X MB」会被误读成独显显存。
    /// 措辞刻意压得很短 —— 这一行只有一行的高度，长了会被右边界硬切掉。
    /// </summary>
    private string BuildAdapterNote(Snapshot s)
    {
        var parts = new List<string>();

        if (s.OtherVramMb > 0 && s.Gpu.MemTotalMb > 0)
        {
            parts.Add($"结束后台最多可回收约 {s.OtherVramMb:0} MB"
                      + $"（占 {100.0 * s.OtherVramMb / s.Gpu.MemTotalMb:0.0}% 显存）");
        }
        else
        {
            parts.Add("目前没有可回收的后台显存占用");
        }

        if (s.AdapterCount == 0)
        {
            parts.Add("未能枚举显卡适配器，以上为全部显卡合计，可能含核显占用");
        }
        else if (s.MultiAdapter)
        {
            var names = new List<string>();
            if (s.Adapters is not null)
            {
                foreach (var a in s.Adapters)
                {
                    bool isPrimary = !string.IsNullOrEmpty(s.PrimaryAdapterName) &&
                                     a.Name.Equals(s.PrimaryAdapterName, StringComparison.OrdinalIgnoreCase);
                    if (!isPrimary && !names.Contains(a.ShortName))
                    {
                        names.Add(a.ShortName);
                    }
                }
            }

            // 最多列一个名字，多了这一行放不下。
            string label = names.Count == 0
                ? "其他显卡"
                : names.Count == 1 ? names[0] : names[0] + " 等";

            parts.Add($"另有 {s.OtherAdapterVramMb:0.0} MB 属于「{label}」，"
                      + "结束这些程序不会释放游戏所用显卡的显存");
        }

        return string.Join("。", parts) + "。";
    }

    private void CheckAllOthers()
    {
        _checkedPids.Clear();
        int skippedOtherAdapter = 0;
        int skippedSensitive = 0;

        _procList.BeginUpdate();
        try
        {
            foreach (ListViewItem it in _procList.Items)
            {
                if (it.Tag is not ProcVram p)
                {
                    continue;
                }

                if (p.Class != ProcClass.Other)
                {
                    continue;
                }

                if (!p.OnPrimaryAdapter)
                {
                    skippedOtherAdapter++;
                    continue;
                }

                // 安全/输入法软件默认不勾 —— 它们省下的显存只有几十 MB，
                // 而误关的代价（防护失效、输入法失灵）明显更大。
                if (p.IsSensitive)
                {
                    skippedSensitive++;
                    continue;
                }

                it.Checked = true;
            }
        }
        finally
        {
            _procList.EndUpdate();
        }

        var notes = new List<string>();
        if (skippedOtherAdapter > 0)
        {
            notes.Add($"跳过 {skippedOtherAdapter} 个只占用其他显卡的后台程序");
        }
        if (skippedSensitive > 0)
        {
            notes.Add($"跳过 {skippedSensitive} 个安全/输入法软件（需要的话可手动勾选）");
        }

        string extra = notes.Count > 0 ? "，" + string.Join("；", notes) : string.Empty;
        AddLog($"已勾选 {_checkedPids.Count} 个后台程序{extra}。点「结束勾选的进程」才会真正结束它们。");
    }

    /// <summary>
    /// 结束勾选的后台程序以回收显存。
    ///
    /// 这是整个工具里唯一会「动手」的功能，也是把脚本版 guard 模式的手动版：
    /// 脚本版会在游戏运行时自动关闭名单里的程序，这里改成由用户看着列表亲自决定。
    /// 原因很直接 —— 自动关别人的程序，出问题时用户根本不知道发生了什么。
    /// </summary>
    private void KillSelected()
    {
        // 同一个进程可能在多块显卡上各有一行，按 PID 去重，避免重复结束。
        var targets = new Dictionary<int, ProcVram>();
        foreach (ListViewItem it in _procList.Items)
        {
            if (!it.Checked || it.Tag is not ProcVram p)
            {
                continue;
            }
            if (p.Class != ProcClass.Other || !p.OnPrimaryAdapter)
            {
                continue;
            }
            targets[p.Pid] = p;
        }

        if (targets.Count == 0)
        {
            AddLog("没有勾选任何可结束的进程。只有「其他后台程序」中、且占用游戏所用显卡的那些可以被结束。");
            return;
        }

        var list = targets.Values.OrderByDescending(t => t.LocalMb).ToList();
        double total = list.Sum(t => t.LocalMb);
        int sensitive = list.Count(t => t.IsSensitive);

        var sb = new StringBuilder();
        sb.AppendLine($"即将结束以下 {list.Count} 个后台程序：");
        sb.AppendLine();
        foreach (var t in list.Take(20))
        {
            string mark = t.IsSensitive ? "  ← 安全/输入法软件，建议保留" : string.Empty;
            sb.AppendLine($"    {t.Name}   （{t.LocalMb:0.0} MB，位于 {t.AdapterName}）{mark}");
        }
        if (list.Count > 20)
        {
            sb.AppendLine($"    …以及另外 {list.Count - 20} 个");
        }
        sb.AppendLine();
        sb.AppendLine($"它们合计占用约 {total:0.0} MB 显存。");
        if (sensitive > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"其中 {sensitive} 个是安全或输入法软件。结束它们会让防护或输入法暂时失效，"
                          + "而它们占用的显存通常只有几十 MB —— 建议先在列表里取消勾选。");
        }
        sb.AppendLine();
        sb.AppendLine("这些程序未保存的内容会丢失，请先确认没有正在编辑的东西。");
        sb.AppendLine("继续吗？");

        var r = MessageBox.Show(
            this,
            sb.ToString(),
            "结束进程确认",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (r != DialogResult.OK)
        {
            AddLog("已取消结束进程。");
            return;
        }

        int ok = 0;
        int denied = 0;
        double freed = 0;

        foreach (var t in list)
        {
            try
            {
                using var p = Process.GetProcessById(t.Pid);
                p.Kill();
                if (p.WaitForExit(4000))
                {
                    ok++;
                    freed += t.LocalMb;
                    AddLog($"已结束：{t.Name} (PID {t.Pid})，释放约 {t.LocalMb:0.0} MB");
                }
                else
                {
                    AddLog($"结束超时：{t.Name} (PID {t.Pid})");
                }
            }
            catch (Exception ex)
            {
                denied++;
                AddLog($"被拒绝：{t.Name} (PID {t.Pid}) —— {ex.Message}（可能需要管理员权限）");
            }
        }

        _checkedPids.Clear();

        AddLog($"结束完成：成功 {ok} 个，失败 {denied} 个，累计释放约 {freed:0.0} MB 显存。");
        if (denied > 0)
        {
            AddLog("部分进程以更高权限运行，请点「以管理员身份重启」后重试。");
        }
    }

    // ------------------------------------------------------------------ 报告与关于

    private void ShowHealthReport()
    {
        var snapshot = _last == Snapshot.Empty ? _sampler.Latest : _last;
        if (snapshot == Snapshot.Empty)
        {
            AddLog("还没有采到数据，稍候再试。");
            return;
        }

        string text = ReportBuilder.BuildHealthReport(snapshot);
        string? path = null;

        try
        {
            Directory.CreateDirectory(AppPaths.OutputDir);
            path = Path.Combine(AppPaths.OutputDir, "显存体检-" + DateTime.Now.ToString("MMdd-HHmm") + ".txt");
            File.WriteAllText(path, text, new UTF8Encoding(true));
            AddLog("体检报告已保存：" + path);
        }
        catch (Exception ex)
        {
            AddLog("保存报告失败：" + ex.Message);
        }

        using var dlg = new TextReportForm("显存体检报告", text, path);
        dlg.ShowDialog(this);
    }

    private void ShowAbout()
    {
        using var dlg = new AboutForm();
        dlg.ShowDialog(this);
        AddLog("已关闭介绍窗口。");
    }

    private void ShowVersion()
    {
        string text = string.Join(Environment.NewLine, new[]
        {
            "游戏助手",
            "版本 2.0.0",
            "",
            "原生 WinForms 实现，不依赖 PowerShell，不安装任何运行时。",
            "",
            "nvidia-smi : " + (NvidiaSmi.Path ?? "未找到"),
            "CPU 标识   : " + Inventory.CpuName(),
            ".NET 版本  : " + Environment.Version,
            "输出目录   : " + AppPaths.OutputDir,
        });

        MessageBox.Show(this, text, "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
