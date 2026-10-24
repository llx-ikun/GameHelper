using System.Text;

namespace GameHelper.UI;

/// <summary>只读文本查看器，用于展示体检报告与监控汇总。可以保存，也可以一键复制。</summary>
internal sealed class TextReportForm : Form
{
    private readonly RichTextBox _box;
    private readonly string _text;
    private string? _path;

    internal TextReportForm(string title, string text, string? path)
    {
        _text = text;
        _path = path;

        SuspendLayout();

        Text = title + " · 游戏助手";
        Icon = Theme.AppIcon();
        ClientSize = new Size(740, 570);
        MinimumSize = new Size(560, 400);
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
            RowCount = 2,
            BackColor = Theme.Bg,
            Padding = new Padding(12),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));

        _box = new RichTextBox
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
            Text = text,
        };
        root.Controls.Add(_box, 0, 0);

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
            Padding = new Padding(0, 8, 0, 0),
        };

        var btnSave = Theme.MakeButton("另存为…", Theme.Blue, 104, 32);
        btnSave.Click += (_, _) => SaveAs();

        var btnCopy = Theme.MakeGhostButton("复制全文", 104, 32);
        btnCopy.Click += (_, _) => CopyAll();

        var btnOpen = Theme.MakeGhostButton("打开所在目录", 128, 32);
        btnOpen.Click += (_, _) => OpenContaining();

        var btnClose = Theme.MakeGhostButton("关闭", 84, 32);
        btnClose.Click += (_, _) => Close();

        bar.Controls.Add(btnSave);
        bar.Controls.Add(btnCopy);
        bar.Controls.Add(btnOpen);
        bar.Controls.Add(btnClose);
        root.Controls.Add(bar, 0, 1);

        Controls.Add(root);

        CancelButton = btnClose;
        ResumeLayout(false);
    }

    private void SaveAs()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "保存报告",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = _path is not null ? Path.GetFileName(_path) : "报告.txt",
            InitialDirectory = Core.AppPaths.OutputDir,
            OverwritePrompt = true,
        };

        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllText(dlg.FileName, _text, new UTF8Encoding(true));
            _path = dlg.FileName;
            MessageBox.Show(this, "已保存到：\r\n" + dlg.FileName, "完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存失败：" + ex.Message, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CopyAll()
    {
        try
        {
            if (_text.Length == 0)
            {
                return;
            }
            Clipboard.SetText(_text);
            MessageBox.Show(this, "全文已复制到剪贴板。", "完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "复制失败：" + ex.Message, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenContaining()
    {
        try
        {
            string dir = _path is not null
                ? Path.GetDirectoryName(_path) ?? Core.AppPaths.OutputDir
                : Core.AppPaths.OutputDir;

            if (!Directory.Exists(dir))
            {
                MessageBox.Show(this, "目录不存在：" + dir, "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + dir + "\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开目录失败：" + ex.Message, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
