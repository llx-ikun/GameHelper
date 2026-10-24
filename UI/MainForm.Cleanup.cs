using System.Text;
using GameHelper.Core;

namespace GameHelper.UI;

/// <summary>清理页：扫描 / 隔离 / 还原游戏目录里的第三方文件。</summary>
internal sealed partial class MainForm
{
    private readonly Settings _settings = new();

    private TextBox _txtGameDir = null!;
    private TextBox _txtBackupRoot = null!;
    private Label _lblDirHint = null!;
    private Button _btnScan = null!;
    private Button _btnQuarantine = null!;
    private Button _btnRestore = null!;
    private ComboBox _cmbBackups = null!;
    private DataGridView _grid = null!;
    private Label _lblGate = null!;
    private Label _lblCleanSummary = null!;

    private List<ScanEntry> _scanRows = new();
    private bool _cleanBusy;
    private bool _gateOpen;
    private DateTime _lastGateCheck = DateTime.MinValue;

    private TabPage BuildCleanupTab()
    {
        // 目录按**本机实际情况**推断，不写死盘符（详见 GameLocator 的注释）。
        // 以前这两个默认值直接写成了某一台机器的「G:\...」，换台电脑就是错的，
        // 而且不报错 —— 扫描结果一片「不存在」，看着像游戏很干净。
        //
        // 顺序：用户手动设过的 > 自动识别到的 > 留空（留空是安全的，
        // 扫描与隔离都有 GameDirExists 门禁，不会去动当前目录）。
        string saved = _settings.GetString("游戏目录", string.Empty);
        string? found = GameLocator.FindGameDir();

        // 保存过的路径优先 —— 但**只在它确实存在时**才优先。
        // 否则把设置文件带到另一台电脑、或游戏换过盘之后，
        // 它会一直指向一个不存在的位置，而界面只会显示「都不存在」，
        // 这正是「安静地给错结论」的源头。存在的才叫偏好，不存在的只是过期数据。
        bool usedSaved = saved.Length > 0 && Directory.Exists(saved);
        _cleaner.GameDir = usedSaved ? saved : (found ?? saved);

        _cleaner.BackupRoot = _settings.GetString(
            "备份目录", GameLocator.SuggestBackupRoot(_cleaner.GameDir));

        var page = new TabPage("清理")
        {
            BackColor = Theme.Bg,
            Padding = new Padding(8),
        };

        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Theme.Bg,
        };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        // 66 → 92：路径区多一行「自动识别结果」的说明。
        // 不写这行，自动识别失败时用户只会看到两个空框，无从判断是没找到
        // 还是程序坏了 —— 安静地失败最浪费时间。
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 92F));
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 78F));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));

        // ---- 路径配置 ----
        var cfg = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            BackColor = Theme.Bg,
            Margin = new Padding(0, 0, 0, 4),
        };
        cfg.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76F));
        cfg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        cfg.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
        cfg.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        cfg.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        cfg.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));

        _txtGameDir = MakePathBox(_cleaner.GameDir);
        _txtBackupRoot = MakePathBox(_cleaner.BackupRoot);

        var btnBrowseGame = Theme.MakeGhostButton("浏览…", 88, 26);
        btnBrowseGame.Click += (_, _) => BrowseInto(_txtGameDir, "选择《永劫无间》的游戏目录");

        var btnBrowseBackup = Theme.MakeGhostButton("浏览…", 88, 26);
        btnBrowseBackup.Click += (_, _) => BrowseInto(_txtBackupRoot, "选择备份目录（隔离的文件会移到这里）");

        cfg.Controls.Add(MakeFormLabel("游戏目录"), 0, 0);
        cfg.Controls.Add(_txtGameDir, 1, 0);
        cfg.Controls.Add(btnBrowseGame, 2, 0);
        cfg.Controls.Add(MakeFormLabel("备份目录"), 0, 1);
        cfg.Controls.Add(_txtBackupRoot, 1, 1);
        cfg.Controls.Add(btnBrowseBackup, 2, 1);

        // 自动识别的结果就写在这一行：找到了说一声，没找到说清怎么办。
        _lblDirHint = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ForeColor = Theme.Sub,
            BackColor = Theme.Bg,
            Font = Theme.UiSm,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
        };
        cfg.Controls.Add(_lblDirHint, 0, 2);
        cfg.SetColumnSpan(_lblDirHint, 3);

        // 把自动识别的结果明确说出来：找到了说清楚是「识别到的」还是「沿用上次的」，
        // 没找到就告诉用户下一步该怎么办。安静地失败最浪费时间。
        if (_cleaner.GameDirExists)
        {
            _lblDirHint.Text = usedSaved
                ? "沿用上次设置的游戏目录。"
                : "已自动识别到游戏目录（从运行中的游戏 / Steam 库 / 各盘常见位置）。";
            _lblDirHint.ForeColor = Theme.Green;
        }
        else if (_cleaner.GameDir.Length == 0)
        {
            _lblDirHint.Text = "未找到《永劫无间》安装目录。点上方「浏览…」手动选择；"
                             + "清理功能用不到时也可以不管它。";
            _lblDirHint.ForeColor = Theme.Amber;
        }
        else
        {
            _lblDirHint.Text = "这个目录当前不存在（可能是别的电脑上设置的，或游戏已卸载）。请重新选择。";
            _lblDirHint.ForeColor = Theme.Amber;
        }
        outer.Controls.Add(cfg, 0, 0);

        // ---- 操作 ----
        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Theme.Bg,
            Margin = new Padding(0, 0, 0, 6),
        };

        _btnScan = Theme.MakeButton("扫描（只读）", Theme.Blue, 116, 32);
        _btnScan.Click += (_, _) => RunScan();

        _btnQuarantine = Theme.MakeButton("隔离勾选项", Theme.Red, 116, 32);
        _btnQuarantine.Click += (_, _) => RunQuarantine();

        var btnRefreshBackups = Theme.MakeGhostButton("刷新备份列表", 116, 32);
        btnRefreshBackups.Click += (_, _) => RefreshBackupList(true);

        _cmbBackups = new ComboBox
        {
            Width = 220,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = Theme.Ui,
            Margin = new Padding(0, 5, 6, 0),
        };

        _btnRestore = Theme.MakeGhostButton("还原选中备份集", 128, 32);
        _btnRestore.Click += (_, _) => RunRestore();

        var btnOpenBackup = Theme.MakeGhostButton("打开备份目录", 116, 32);
        btnOpenBackup.Click += (_, _) => OpenFolder(_cleaner.BackupRoot);

        tools.Controls.Add(_btnScan);
        tools.Controls.Add(_btnQuarantine);
        tools.Controls.Add(btnRefreshBackups);
        tools.Controls.Add(MakeInlineLabel("备份集"));
        tools.Controls.Add(_cmbBackups);
        tools.Controls.Add(_btnRestore);
        tools.Controls.Add(btnOpenBackup);
        outer.Controls.Add(tools, 0, 1);

        // ---- 结果表 ----
        var gridGroup = Theme.MakeGroup("  扫描结果  ");
        gridGroup.Dock = DockStyle.Fill;
        gridGroup.Margin = new Padding(0, 0, 0, 6);

        _grid = new DataGridView { Dock = DockStyle.Fill };
        Theme.StyleGrid(_grid);
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "pick",
            HeaderText = "选择",
            Width = 52,
            FillWeight = 26,
            ValueType = typeof(bool),
            FlatStyle = FlatStyle.Standard,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "name", HeaderText = "名称", FillWeight = 190, ReadOnly = true,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "kind", HeaderText = "类型", FillWeight = 50, ReadOnly = true,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "size", HeaderText = "大小", FillWeight = 70, ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "marker", HeaderText = "标记校验", FillWeight = 150, ReadOnly = true,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "status", HeaderText = "状态", FillWeight = 140, ReadOnly = true,
        });

        // 勾选框必须立刻提交，否则读到的还是旧值。
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };

        // 勾选变化后立刻决定「隔离」按钮能不能按 —— 没勾任何东西时它必须是灰的。
        int pickColumn = _grid!.Columns["pick"]!.Index;
        _grid.CellValueChanged += (_, e) =>
        {
            if (e.ColumnIndex == pickColumn)
            {
                UpdateQuarantineButton();
            }
        };

        gridGroup.Controls.Add(_grid);
        outer.Controls.Add(gridGroup, 0, 2);

        // ---- 门禁与汇总 ----
        var foot = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        foot.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
        foot.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));

        _lblCleanSummary = Theme.MakeLabel("尚未扫描。点「扫描（只读）」开始。", Theme.Sub, Theme.Ui);
        _lblCleanSummary.Dock = DockStyle.Fill;

        _lblGate = Theme.MakeLabel("门禁检查中…", Theme.Sub, Theme.Ui);
        _lblGate.Dock = DockStyle.Fill;

        foot.Controls.Add(_lblCleanSummary, 0, 0);
        foot.Controls.Add(_lblGate, 0, 1);
        outer.Controls.Add(foot, 0, 3);

        page.Controls.Add(outer);

        RefreshBackupList(false);
        return page;
    }

    private static Label MakeFormLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Theme.Text,
        Font = Theme.Ui,
        BackColor = Color.Transparent,
        Margin = new Padding(0),
    };

    private static TextBox MakePathBox(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        Font = Theme.Ui,
        Margin = new Padding(0, 2, 6, 2),
        BorderStyle = BorderStyle.FixedSingle,
    };

    private static void BrowseInto(TextBox box, string description)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = description,
            SelectedPath = Directory.Exists(box.Text) ? box.Text : string.Empty,
            ShowNewFolderButton = true,
        };
        if (dlg.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            box.Text = dlg.SelectedPath;
        }
    }

    // ------------------------------------------------------------------ 门禁

    private void UpdateGate(Snapshot s)
    {
        // 枚举全部进程有一定开销，不必跟着采样频率跑。
        if ((DateTime.Now - _lastGateCheck).TotalSeconds < 4)
        {
            return;
        }
        _lastGateCheck = DateTime.Now;
        RefreshGate();
    }

    private void RefreshGate()
    {
        if (_lblGate is null || _lblGate.IsDisposed)
        {
            return;
        }

        var blockers = _cleaner.Blockers();
        _gateOpen = blockers.Count == 0;

        if (blockers.Count > 0)
        {
            _lblGate.ForeColor = Theme.Red;
            _lblGate.Text = "门禁：游戏或反作弊正在运行（" + string.Join("、", blockers)
                          + "），隔离已被禁用。请完全退出游戏后再试。";
        }
        else if (!_cleaner.GameDirExists)
        {
            _lblGate.ForeColor = Theme.Red;
            _lblGate.Text = "门禁：找不到游戏目录 —— " + _cleaner.GameDir;
        }
        else if (Inventory.IsSteamRunning())
        {
            _lblGate.ForeColor = Theme.Amber;
            _lblGate.Text = "门禁：可以隔离，但 Steam 正在运行。若移动失败，请先关闭 Steam。"
                          + "（这本身不是安全问题）";
        }
        else
        {
            _lblGate.ForeColor = Theme.Green;
            _lblGate.Text = "门禁：游戏与反作弊均未运行，可以安全执行隔离。";
        }

        UpdateQuarantineButton();
    }

    /// <summary>
    /// 「隔离」按钮只有在两件事同时成立时才能按：门禁放行、且确实勾了行。
    /// 之前只要扫描出可隔离项它就亮着，用户点了才发现没勾东西。
    /// </summary>
    private void UpdateQuarantineButton()
    {
        if (_btnQuarantine is null || _btnQuarantine.IsDisposed)
        {
            return;
        }

        bool anyChecked = false;
        if (_grid is not null && !_grid.IsDisposed)
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Cells["pick"].Value is true)
                {
                    anyChecked = true;
                    break;
                }
            }
        }

        _btnQuarantine.Enabled = !_cleanBusy && _gateOpen && _cleaner.GameDirExists && anyChecked;
    }

    // ------------------------------------------------------------------ 扫描

    private void RunScan()
    {
        if (_cleanBusy)
        {
            return;
        }

        _cleaner.GameDir = _txtGameDir.Text.Trim();
        _cleaner.BackupRoot = _txtBackupRoot.Text.Trim();
        PersistCleanupPaths();

        if (!_cleaner.GameDirExists)
        {
            AddLog("找不到游戏目录：" + _cleaner.GameDir);
            RefreshGate();
            return;
        }

        SetCleanBusy(true);
        AddLog("开始扫描游戏目录（只读，不会改动任何文件）…");

        Task.Run(() => _cleaner.Scan()).ContinueWith(t =>
        {
            Post(() =>
            {
                SetCleanBusy(false);

                if (t.IsFaulted)
                {
                    AddLog("扫描失败：" + (t.Exception?.GetBaseException().Message ?? "未知错误"));
                    return;
                }

                ApplyScanResult(t.Result);
            });
        }, TaskScheduler.Default);
    }

    private void ApplyScanResult(List<ScanEntry> rows)
    {
        _scanRows = rows;

        _grid.Rows.Clear();
        double totalMb = 0;
        int confirmed = 0;
        int review = 0;

        foreach (var r in rows)
        {
            int idx = _grid.Rows.Add(
                r.CanQuarantine,
                r.Rel,
                r.KindText,
                r.Exists ? r.SizeMb.ToString("0.0") + " MB" : "-",
                string.IsNullOrEmpty(r.Marker) ? "（无标记要求）" : r.Marker,
                r.StatusText);

            var row = _grid.Rows[idx];

            if (!r.Exists)
            {
                row.DefaultCellStyle.ForeColor = Theme.Sub;
            }
            else if (r.Blocked)
            {
                row.DefaultCellStyle.ForeColor = Theme.Blue;
            }
            else if (r.Verified)
            {
                row.DefaultCellStyle.ForeColor = Theme.Text;
                confirmed++;
                totalMb += r.SizeMb;
            }
            else
            {
                row.DefaultCellStyle.ForeColor = Theme.Amber;
                review++;
            }

            // 不可隔离的行，勾选框直接置灰，避免用户以为是程序坏了。
            if (!r.CanQuarantine)
            {
                row.Cells["pick"].ReadOnly = true;
                row.Cells["pick"].Style.BackColor = Color.FromArgb(244, 245, 247);
                row.Cells["pick"].Style.SelectionBackColor = Color.FromArgb(244, 245, 247);
            }
        }

        var sb = new StringBuilder();
        sb.Append($"扫描完成：已确认可隔离 {confirmed} 项（合计 {totalMb:0.0} MB）");
        if (review > 0)
        {
            sb.Append($"；{review} 项标记不符，需人工核对，不会被处理");
        }
        var protectedPresent = rows.Count(r => r.Exists && r.Blocked);
        if (protectedPresent > 0)
        {
            sb.Append($"；{protectedPresent} 项命中保护名单，永不处理");
        }

        _lblCleanSummary.Text = sb.ToString();
        AddLog(sb.ToString());
        AddLog($"保护名单共 {CleanerService.Protected.Count} 项游戏文件，任何情况下都不会被本工具触碰。");

        RefreshBackupList(false);
        RefreshGate();
    }

    private void SetCleanBusy(bool busy)
    {
        _cleanBusy = busy;
        _btnScan.Enabled = !busy;
        _btnRestore.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        UpdateQuarantineButton();
    }

    // ------------------------------------------------------------------ 隔离

    private void RunQuarantine()
    {
        if (_cleanBusy)
        {
            return;
        }

        var picked = new List<ScanEntry>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            bool chk = row.Cells["pick"].Value is true;
            if (!chk)
            {
                continue;
            }

            string name = row.Cells["name"].Value?.ToString() ?? string.Empty;
            var match = _scanRows.FirstOrDefault(r => r.Rel == name);
            if (match is not null && match.CanQuarantine)
            {
                picked.Add(match);
            }
        }

        if (picked.Count == 0)
        {
            AddLog("没有勾选任何可隔离的项目。请先扫描，再勾选状态为「已确认，可隔离」的行。");
            return;
        }

        var live = _cleaner.Blockers();
        if (live.Count > 0)
        {
            MessageBox.Show(
                this,
                "游戏或其反作弊仍在运行：\r\n\r\n    " + string.Join("\r\n    ", live) + "\r\n\r\n"
                + "这些进程存活期间不要改动游戏目录。两个原因：\r\n"
                + "  1. 文件被锁，移动操作本来就无法成功；\r\n"
                + "  2. 在运行中的内核级反作弊眼皮底下改动游戏文件，可能被视为篡改，令账号陷入风险。\r\n\r\n"
                + "请完全退出游戏后再运行本工具。",
                "已拒绝：游戏正在运行",
                MessageBoxButtons.OK,
                MessageBoxIcon.Stop);
            AddLog("隔离被门禁拦下：游戏或反作弊正在运行。");
            return;
        }

        double totalMb = picked.Sum(p => p.SizeMb);
        var sb = new StringBuilder();
        sb.AppendLine($"即将把以下 {picked.Count} 项（{totalMb:0.0} MB）移出游戏目录：");
        sb.AppendLine();
        foreach (var p in picked.Take(20))
        {
            sb.AppendLine("    " + p.Rel);
        }
        if (picked.Count > 20)
        {
            sb.AppendLine($"    …以及另外 {picked.Count - 20} 项");
        }
        sb.AppendLine();
        sb.AppendLine("目标是【移动】而不是删除，并在备份目录里写入一份可独立运行的还原脚本。");
        sb.AppendLine("源目录：" + _cleaner.GameDir);
        sb.AppendLine("备份目录：" + _cleaner.BackupRoot);
        sb.AppendLine();
        sb.AppendLine("继续吗？");

        if (MessageBox.Show(this, sb.ToString(), "隔离确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.OK)
        {
            AddLog("已取消隔离。");
            return;
        }

        SetCleanBusy(true);
        AddLog($"开始隔离 {picked.Count} 项…");

        Task.Run(() => _cleaner.Quarantine(picked)).ContinueWith(t =>
        {
            Post(() =>
            {
                SetCleanBusy(false);

                if (t.IsFaulted)
                {
                    AddLog("隔离失败：" + (t.Exception?.GetBaseException().Message ?? "未知错误"));
                    return;
                }

                var result = t.Result;
                foreach (var m in result.Messages)
                {
                    AddLog(m);
                }

                if (result.Destination is not null)
                {
                    AddLog("建议的后续步骤：在 Steam 中右键游戏 -> 属性 -> 已安装文件 -> 验证游戏文件完整性。");
                }

                RefreshBackupList(false);
                RunScan();
            });
        }, TaskScheduler.Default);
    }

    // ------------------------------------------------------------------ 还原

    private void RefreshBackupList(bool announce)
    {
        if (_cmbBackups is null || _cmbBackups.IsDisposed)
        {
            return;
        }

        var sets = _cleaner.ListBackups();
        string? previous = _cmbBackups.SelectedItem as string;

        _cmbBackups.Items.Clear();
        foreach (var s in sets)
        {
            _cmbBackups.Items.Add(s);
        }

        if (sets.Count > 0)
        {
            int idx = previous is not null ? sets.IndexOf(previous) : 0;
            _cmbBackups.SelectedIndex = idx >= 0 ? idx : 0;
        }

        if (announce)
        {
            AddLog(sets.Count == 0
                ? "没有找到任何备份集。"
                : $"找到 {sets.Count} 个备份集：" + string.Join("、", sets.Take(5)));
        }
    }

    private void RunRestore()
    {
        if (_cleanBusy)
        {
            return;
        }

        if (_cmbBackups.SelectedItem is not string set || string.IsNullOrWhiteSpace(set))
        {
            AddLog("没有可还原的备份集。");
            return;
        }

        _cleaner.BackupRoot = _txtBackupRoot.Text.Trim();
        _cleaner.GameDir = _txtGameDir.Text.Trim();
        PersistCleanupPaths();

        if (!_cleaner.GameDirExists)
        {
            AddLog("游戏目录不存在，无法还原：" + _cleaner.GameDir);
            return;
        }

        if (MessageBox.Show(
                this,
                $"将把备份集「{set}」里的文件移回：\r\n\r\n    {_cleaner.GameDir}\r\n\r\n"
                + "游戏目录里已存在的同名项目会被跳过，不会被覆盖。\r\n\r\n继续吗？",
                "还原确认",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        SetCleanBusy(true);
        AddLog("开始还原备份集 " + set + " …");

        Task.Run(() => _cleaner.Restore(set)).ContinueWith(t =>
        {
            Post(() =>
            {
                SetCleanBusy(false);

                if (t.IsFaulted)
                {
                    AddLog("还原失败：" + (t.Exception?.GetBaseException().Message ?? "未知错误"));
                    return;
                }

                foreach (var m in t.Result.Messages)
                {
                    AddLog(m);
                }

                RefreshBackupList(false);
                RunScan();
            });
        }, TaskScheduler.Default);
    }

    private void PersistCleanupPaths()
    {
        _settings.Set("游戏目录", _cleaner.GameDir);
        _settings.Set("备份目录", _cleaner.BackupRoot);
        _settings.Save();
    }
}
