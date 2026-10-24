using System.Drawing;
using System.Drawing.Drawing2D;

namespace GameHelper.UI;

/// <summary>
/// 配色与控件工厂。
///
/// 颜色取自脚本版工具箱，保证 exe 版和脚本版看起来是同一个东西；
/// 字体按本机实际安装的来选（见 PickFontFamily），不写死某一个。
/// </summary>
internal static class Theme
{
    internal static readonly Color Bg = Color.FromArgb(245, 246, 248);
    internal static readonly Color Card = Color.White;
    internal static readonly Color Text = Color.FromArgb(31, 35, 41);
    internal static readonly Color Sub = Color.FromArgb(100, 106, 115);
    internal static readonly Color Blue = Color.FromArgb(22, 100, 255);
    internal static readonly Color Green = Color.FromArgb(46, 161, 33);
    internal static readonly Color Amber = Color.FromArgb(217, 119, 6);
    internal static readonly Color Red = Color.FromArgb(216, 57, 49);
    internal static readonly Color Line = Color.FromArgb(222, 226, 230);
    internal static readonly Color Gray = Color.FromArgb(134, 144, 156);
    internal static readonly Color Slate = Color.FromArgb(88, 101, 125);
    internal static readonly Color LogBg = Color.FromArgb(250, 250, 251);
    internal static readonly Color TrackBg = Color.FromArgb(233, 236, 240);

    /// <summary>等宽字体（日志用）。</summary>
    internal const string MonoFamily = "Consolas";

    /// <summary>
    /// 界面字体族。
    ///
    /// 不再写死一个名字：本程序要拿到别的电脑上跑，而那台机器未必装了
    /// Microsoft YaHei UI。GDI+ 遇到不存在的字体会静默替换成一个默认族
    /// （不抛异常），但替换后的字宽不同，可能把已经调好的布局挤坏。
    /// 所以这里主动挑一个**确实存在**的，让回退可控。
    /// </summary>
    internal static readonly string FontFamily = PickFontFamily(
        "Microsoft YaHei UI", "Microsoft YaHei", "微软雅黑", "Segoe UI", "Tahoma");

    private static string PickFontFamily(params string[] candidates)
    {
        foreach (string name in candidates)
        {
            try
            {
                using var probe = new Font(name, 9F);

                // 关键：GDI+ 找不到字体会**静默替换**，所以创建成功不代表用上了它。
                // 必须回头确认实际生效的是不是我们要的那个。
                if (probe.FontFamily.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
            catch
            {
                // 这个字体有问题，换下一个
            }
        }

        return System.Drawing.FontFamily.GenericSansSerif.Name;
    }

    internal static readonly Font Ui = new(FontFamily, 9F);
    internal static readonly Font UiSm = new(FontFamily, 8.5F);
    internal static readonly Font UiBold = new(FontFamily, 9F, FontStyle.Bold);
    internal static readonly Font Title = new(FontFamily, 13F, FontStyle.Bold);
    internal static readonly Font H1 = new(FontFamily, 11F, FontStyle.Bold);
    internal static readonly Font H2 = new(FontFamily, 9.5F, FontStyle.Bold);
    internal static readonly Font Mono = new(MonoFamily, 8.75F);
    internal static readonly Font Big = new(FontFamily, 20F, FontStyle.Bold);

    internal static Icon AppIcon()
    {
        try
        {
            using var stream = typeof(Theme).Assembly.GetManifestResourceStream("app.ico");
            if (stream is not null)
            {
                return new Icon(stream, new Size(32, 32));
            }
        }
        catch
        {
            // 图标缺失不是致命问题
        }

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var ic = Icon.ExtractAssociatedIcon(exe);
                if (ic is not null)
                {
                    return ic;
                }
            }
        }
        catch
        {
            // 忽略
        }

        return SystemIcons.Application;
    }

    internal static Button MakeButton(string text, Color back, int width, int height)
    {
        var b = new Button
        {
            Text = text,
            Size = new Size(width, height),
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            Font = Ui,
            UseVisualStyleBackColor = false,
            Margin = new Padding(0),
        };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Line;
        b.FlatAppearance.MouseOverBackColor = Lighten(back, 0.10f);
        b.FlatAppearance.MouseDownBackColor = Darken(back, 0.10f);
        AttachDisabledLook(b, back, Color.White, DisabledFill, DisabledText);
        return b;
    }

    internal static readonly Color DisabledFill = Color.FromArgb(206, 211, 218);
    internal static readonly Color DisabledText = Color.FromArgb(250, 250, 251);

    /// <summary>
    /// 让「禁用」看起来就是禁用的。
    ///
    /// FlatStyle 自绘按钮在禁用时只把文字变灰，底色仍是原色 ——
    /// 一个红色的「结束进程」按钮看起来照常可按，用户会以为程序坏了。
    /// </summary>
    private static void AttachDisabledLook(Button b, Color normalFill, Color normalText, Color disabledFill, Color disabledText)
    {
        void Apply()
        {
            b.BackColor = b.Enabled ? normalFill : disabledFill;
            b.ForeColor = b.Enabled ? normalText : disabledText;
            b.Cursor = b.Enabled ? Cursors.Hand : Cursors.Default;
        }

        b.EnabledChanged += (_, _) => Apply();
        Apply();
    }

    /// <summary>次要按钮：浅底深字，用于「打开目录」「刷新」这类无风险操作。</summary>
    internal static Button MakeGhostButton(string text, int width, int height)
    {
        var b = new Button
        {
            Text = text,
            Size = new Size(width, height),
            FlatStyle = FlatStyle.Flat,
            BackColor = Card,
            ForeColor = Text,
            Cursor = Cursors.Hand,
            Font = Ui,
            UseVisualStyleBackColor = false,
            Margin = new Padding(0),
        };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Line;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(238, 240, 244);
        b.FlatAppearance.MouseDownBackColor = Color.FromArgb(226, 230, 236);
        AttachDisabledLook(b, Card, Text, Color.FromArgb(246, 247, 249), Color.FromArgb(178, 184, 192));
        return b;
    }

    internal static GroupBox MakeGroup(string text)
    {
        return new GroupBox
        {
            Text = text,
            ForeColor = Text,
            BackColor = Card,
            Font = Ui,
            Padding = new Padding(10, 6, 10, 10),
        };
    }

    internal static Label MakeLabel(string text, Color color, Font? font = null)
    {
        return new Label
        {
            Text = text,
            ForeColor = color,
            Font = font ?? Ui,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
    }

    internal static Color Lighten(Color c, float amount) =>
        Color.FromArgb(
            c.A,
            (int)Math.Min(255, c.R + (255 - c.R) * amount),
            (int)Math.Min(255, c.G + (255 - c.G) * amount),
            (int)Math.Min(255, c.B + (255 - c.B) * amount));

    internal static Color Darken(Color c, float amount) =>
        Color.FromArgb(
            c.A,
            (int)Math.Max(0, c.R * (1 - amount)),
            (int)Math.Max(0, c.G * (1 - amount)),
            (int)Math.Max(0, c.B * (1 - amount)));

    /// <summary>把颜色调成告警色：绿 -> 琥珀 -> 红。</summary>
    internal static Color StateColor(double pct, double warnPct, double critPct)
    {
        if (pct >= critPct)
        {
            return Red;
        }
        if (pct >= warnPct)
        {
            return Amber;
        }
        return Green;
    }

    /// <summary>给 DataGridView 套上统一的观感。</summary>
    internal static void StyleGrid(DataGridView g)
    {
        g.BackgroundColor = Card;
        g.BorderStyle = BorderStyle.FixedSingle;
        g.EnableHeadersVisualStyles = false;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(242, 244, 247);
        g.ColumnHeadersDefaultCellStyle.ForeColor = Text;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(242, 244, 247);
        g.ColumnHeadersDefaultCellStyle.SelectionForeColor = Text;
        g.ColumnHeadersDefaultCellStyle.Font = UiBold;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = 28;
        g.DefaultCellStyle.Font = Ui;
        g.DefaultCellStyle.BackColor = Card;
        g.DefaultCellStyle.ForeColor = Text;
        g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 231, 255);
        g.DefaultCellStyle.SelectionForeColor = Text;
        g.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 251, 252);
        g.RowHeadersVisible = false;
        g.AllowUserToAddRows = false;
        g.AllowUserToDeleteRows = false;
        g.AllowUserToResizeRows = false;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        g.MultiSelect = false;
        g.RowTemplate.Height = 24;
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
    }

    internal static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(r);
            return path;
        }

        int d = radius * 2;
        d = Math.Min(d, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
