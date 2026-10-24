using System.Diagnostics;

using GameHelper.Core;

namespace GameHelper.UI;

/// <summary>
/// 选择监控目标的对话框。
///
/// 为什么需要它：帧数采集本身不挑进程，但工具的判定与建议是在
/// 《永劫无间》上标定的。所以这里除了「选哪个进程」，
/// 还必须让人在选之前就看见「选别的游戏意味着没有结论」——
/// 事到临头才发现报告变短了，比一开始就说明白要糟得多。
/// </summary>
internal sealed class TargetPickerForm : Form
{
    private readonly ListView _list = new();
    private readonly Label _hint = new();
    private Button _ok = null!;
    private Button _cancel = null!;

    private TargetPickerForm(IReadOnlyList<TargetCandidate> candidates)
    {
        Text = "选择监控目标";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(520, 380);
        BackColor = Theme.Bg;
        Font = Theme.Ui;

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 46,
            ForeColor = Theme.Text,
            BackColor = Theme.Bg,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 12, 0),
            Text = "选择要监控帧数的程序。\r\n默认是《永劫无间》——只有它享有完整的判定与建议。",
        };
        Controls.Add(title);

        _hint.Dock = DockStyle.Bottom;
        _hint.Height = 62;
        _hint.ForeColor = Theme.Sub;
        _hint.BackColor = Theme.Bg;
        _hint.Padding = new Padding(12, 6, 12, 6);
        _hint.TextAlign = ContentAlignment.TopLeft;
        Controls.Add(_hint);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Theme.Bg,
            Padding = new Padding(12, 4, 12, 4),
        };
        _cancel = Theme.MakeGhostButton("取消", 88, 28);
        _cancel.DialogResult = DialogResult.Cancel;

        // 「恢复默认」放在这一行，而不是只藏在主界面某处 ——
        // 用户选了外部目标后如果找不到回去的路，就会一直用着没有结论的模式，
        // 还以为是工具坏了。
        var back = Theme.MakeGhostButton("恢复默认", 88, 28);
        back.Click += (_, _) =>
        {
            DialogResult = DialogResult.Retry;
            Close();
        };

        _ok = Theme.MakeButton("监控它", Theme.Blue, 88, 28);
        _ok.DialogResult = DialogResult.OK;
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(back);
        buttons.Controls.Add(_ok);
        Controls.Add(buttons);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _list.HideSelection = false;
        _list.BackColor = Theme.Card;
        _list.ForeColor = Theme.Text;
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.Columns.Add("程序", 190);
        _list.Columns.Add("窗口标题", 230);
        _list.Columns.Add("显存", 80, HorizontalAlignment.Right);
        _list.SelectedIndexChanged += (_, _) => RefreshHint();
        _list.MouseDoubleClick += (_, e) =>
        {
            // 双击已选项等于「确认」—— 列表操作里这是最顺手的确认方式。
            if (_list.HitTest(e.Location)?.Item is not null && ValidateSelection())
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        };
        Controls.Add(_list);

        foreach (var c in candidates)
        {
            var item = new ListViewItem(c.ProcessName) { Tag = c };
            item.SubItems.Add(c.WindowTitle);
            item.SubItems.Add(c.VramText);
            _list.Items.Add(item);
        }

        if (_list.Items.Count > 0)
        {
            _list.Items[0].Selected = true;
            _list.Items[0].Focused = true;
        }

        RefreshHint();
        AcceptButton = _ok;
        CancelButton = _cancel;
    }

    private void RefreshHint()
    {
        var c = Selected;
        if (c is null)
        {
            _hint.Text = "当前没有可作为目标的程序。\r\n"
                       + "只列出有窗口标题、且不是系统组件或安全软件的前台程序。";
            _ok.Enabled = false;
            return;
        }

        _ok.Enabled = true;
        _hint.Text = "监控 " + c.ProcessName + " 时，报告只包含帧率、1% Low、显存、"
                   + "温度、占用、频率这些观测值。\r\n"
                   + "自动判定、对局拆分与针对性建议不会出现 —— 那些阈值是在"
                   + "《永劫无间》上标定的，换游戏不适用。";
    }

    /// <summary>当前选中的候选；没有则 null。</summary>
    private TargetCandidate? Selected =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as TargetCandidate : null;

    /// <summary>按下「监控它」时的最后一道校验。</summary>
    private bool ValidateSelection() => Selected is not null;

    /// <summary>
    /// 弹出选择框。
    /// 返回用户选中的目标；按「恢复默认」返回《永劫无间》目标；
    /// 取消则返回 null（调用方应保持原状）。
    /// </summary>
    public static MonitorTarget? Pick(IWin32Window owner, IReadOnlyList<TargetCandidate> candidates)
    {
        using var dlg = new TargetPickerForm(candidates);
        var r = dlg.ShowDialog(owner);
        if (r == DialogResult.OK)
        {
            return dlg.SelectedToTarget();
        }

        // Retry 是「恢复默认」：给一个显式的 Naraka 目标，
        // 让调用方走同一条赋值路径，而不是另一套 if/else。
        return r == DialogResult.Retry
            ? new MonitorTarget(TargetKind.Naraka, 0, "NarakaBladepoint", null)
            : null;
    }

    private MonitorTarget? SelectedToTarget()
    {
        var c = Selected;
        return c is null
            ? null
            : new MonitorTarget(TargetKind.External, c.Pid, c.ProcessName, c.WindowTitle);
    }
}
