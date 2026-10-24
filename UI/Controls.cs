using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace GameHelper.UI;

/// <summary>
/// 显存占用条。比 ProgressBar 多一点东西：
///   * 按阈值变色（绿 / 琥珀 / 红），
///   * 在条上画出 82% 与 90% 两条阈值刻度，
///   * 右侧直接标出「已用 / 总量」。
/// </summary>
internal sealed class MeterBar : Control
{
    private double _used;
    private double _total;
    private double _warnPct = 82;
    private double _critPct = 90;

    public MeterBar()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        BackColor = Theme.Card;
        Height = 22;
        Font = Theme.UiSm;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double WarnPct
    {
        get => _warnPct;
        set { _warnPct = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double CritPct
    {
        get => _critPct;
        set { _critPct = value; Invalidate(); }
    }

    public void SetValue(double usedMb, double totalMb)
    {
        _used = usedMb;
        _total = totalMb;
        Invalidate();
    }

    private double Pct => _total > 0 ? Math.Clamp(100.0 * _used / _total, 0, 100) : 0;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int barH = 12;
        int y = (Height - barH) / 2;
        int right = Width - 128;
        if (right < 40)
        {
            right = Width;
        }

        var trackRect = new Rectangle(0, y, Math.Max(10, right), barH);

        using (var track = new SolidBrush(Theme.TrackBg))
        using (var path = Theme.RoundedRect(trackRect, barH / 2))
        {
            g.FillPath(track, path);
        }

        if (_total > 0)
        {
            int fillW = (int)Math.Round(trackRect.Width * Pct / 100.0);
            if (fillW > 2)
            {
                var fillRect = new Rectangle(trackRect.X, trackRect.Y, fillW, barH);
                using var brush = new SolidBrush(Theme.StateColor(Pct, _warnPct, _critPct));
                using var path = Theme.RoundedRect(fillRect, barH / 2);
                g.FillPath(brush, path);
            }

            // 阈值刻度：一条短竖线，颜色比条深一点，避免被填充色盖住
            using var tickPen = new Pen(Color.FromArgb(150, Theme.Text), 1.4f);
            foreach (var t in new[] { _warnPct, _critPct })
            {
                int x = trackRect.X + (int)Math.Round(trackRect.Width * t / 100.0);
                if (x > trackRect.X + 2 && x < trackRect.Right - 2)
                {
                    g.DrawLine(tickPen, x, y - 2, x, y + barH + 2);
                }
            }
        }

        if (right < Width)
        {
            string txt = _total > 0
                ? $"{_used:0} / {_total:0} MB"
                : "-- / -- MB";
            using var fg = new SolidBrush(_total > 0 ? Theme.Text : Theme.Sub);
            var sf = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                Alignment = StringAlignment.Far,
            };
            var rect = new Rectangle(right + 10, 0, Width - right - 10, Height);
            g.DrawString(txt, Font, fg, rect, sf);
        }
    }
}

/// <summary>
/// 显存占用时序图。
///
/// 用固定长度的环形缓冲存历史点，界面只重绘不裁剪 —— 采样率恒定，
/// 所以「点间距 = 时间间距」这个前提成立，不需要时间戳插值。
/// </summary>
internal sealed class TrendChart : Control
{
    private readonly List<float> _series = new();
    private readonly int _capacity;
    private double _totalMb;
    private double _warnPct = 82;
    private double _critPct = 90;

    /// <summary>
    /// 没有数据时中间显示什么。
    ///
    /// 做成可写的属性而不是写死一句「等待显卡数据…」：那句话在
    /// 没有 NVIDIA 显卡的机器上是**假承诺** —— 会一直等下去。
    /// 由调用方按实际情况给（区分「还没读到」与「根本读不到」）。
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EmptyText { get; set; } = "等待显卡数据…";

    public TrendChart(int capacity = 400)
    {
        _capacity = capacity;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        BackColor = Theme.Card;
        Font = Theme.UiSm;
        DoubleBuffered = true;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double WarnPct
    {
        get => _warnPct;
        set { _warnPct = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double CritPct
    {
        get => _critPct;
        set { _critPct = value; Invalidate(); }
    }

    public void Clear()
    {
        _series.Clear();
        Invalidate();
    }

    public void Push(double usedMb, double totalMb)
    {
        _totalMb = totalMb;
        _series.Add((float)usedMb);
        while (_series.Count > _capacity)
        {
            _series.RemoveAt(0);
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        var plot = new Rectangle(52, 12, Math.Max(20, Width - 52 - 14), Math.Max(20, Height - 12 - 26));

        using (var framePen = new Pen(Theme.Line))
        {
            g.DrawRectangle(framePen, plot);
        }

        if (_totalMb <= 0)
        {
            DrawCentered(g, EmptyText, plot);
            return;
        }

        // 纵轴上限取「总显存」，这样曲线和阈值的相对位置是真实比例。
        double top = _totalMb;

        using var gridPen = new Pen(Color.FromArgb(238, 241, 245));
        using var axisFont = new SolidBrush(Theme.Sub);

        for (int i = 0; i <= 4; i++)
        {
            int y = plot.Bottom - (int)Math.Round(plot.Height * i / 4.0);
            if (i > 0)
            {
                g.DrawLine(gridPen, plot.Left + 1, y, plot.Right - 1, y);
            }
            string label = (top * i / 4.0).ToString("0");
            var sz = g.MeasureString(label, Font);
            g.DrawString(label, Font, axisFont, plot.Left - sz.Width - 6, y - sz.Height / 2);
        }

        // 单位放标题里（"显存占用曲线（MB · 实时）"）。
        // 早先是在图内左上角单独画一个 "MB"，结果和纵轴顶端的刻度值叠在一起。

        // 阈值线
        DrawThreshold(g, plot, top, _warnPct, Theme.Amber, "警告 " + _warnPct.ToString("0") + "%");
        DrawThreshold(g, plot, top, _critPct, Theme.Red, "危急 " + _critPct.ToString("0") + "%");

        if (_series.Count < 2)
        {
            DrawCentered(g, "开始记录后这里会画出显存曲线", plot);
            return;
        }

        var pts = new PointF[_series.Count];
        for (int i = 0; i < _series.Count; i++)
        {
            float x = plot.Left + (float)plot.Width * i / Math.Max(1, _capacity - 1);
            double v = Math.Clamp(_series[i] / top, 0, 1);
            float y = plot.Bottom - (float)(plot.Height * v);
            pts[i] = new PointF(x, y);
        }

        // 曲线下方的渐变填充
        var area = new PointF[pts.Length + 2];
        Array.Copy(pts, area, pts.Length);
        area[pts.Length] = new PointF(pts[^1].X, plot.Bottom);
        area[pts.Length + 1] = new PointF(pts[0].X, plot.Bottom);

        using (var path = new GraphicsPath())
        {
            path.AddLines(area);
            path.CloseFigure();
            using var grad = new LinearGradientBrush(
                new Rectangle(plot.Left, plot.Top, plot.Width, plot.Height),
                Color.FromArgb(70, Theme.Blue),
                Color.FromArgb(8, Theme.Blue),
                LinearGradientMode.Vertical);
            g.FillPath(grad, path);
        }

        using (var pen = new Pen(Theme.Blue, 1.8f))
        {
            g.DrawLines(pen, pts);
        }

        // 末端高亮 + 当前值
        var last = pts[^1];
        double lastMb = _series[^1];
        Color dot = Theme.StateColor(100.0 * lastMb / top, _warnPct, _critPct);
        using (var b = new SolidBrush(dot))
        {
            g.FillEllipse(b, last.X - 3.5f, last.Y - 3.5f, 7, 7);
        }

        string cur = lastMb.ToString("0") + " MB";
        var curSz = g.MeasureString(cur, Theme.UiBold);
        float tx = Math.Min(last.X + 8, plot.Right - curSz.Width - 2);
        float ty = Math.Max(plot.Top + 2, last.Y - curSz.Height - 2);
        using (var bg = new SolidBrush(Color.FromArgb(210, Theme.Card)))
        {
            g.FillRectangle(bg, tx - 2, ty, curSz.Width + 4, curSz.Height);
        }
        using (var fg = new SolidBrush(dot))
        {
            g.DrawString(cur, Theme.UiBold, fg, tx, ty);
        }
    }

    private static void DrawThreshold(Graphics g, Rectangle plot, double top, double pct, Color color, string label)
    {
        if (pct <= 0 || pct > 100)
        {
            return;
        }

        int y = plot.Bottom - (int)Math.Round(plot.Height * (pct / 100.0));
        using var pen = new Pen(color, 1.2f) { DashStyle = DashStyle.Dash };
        g.DrawLine(pen, plot.Left + 1, y, plot.Right - 1, y);
        using var brush = new SolidBrush(color);
        var sz = g.MeasureString(label, Theme.UiSm);
        g.DrawString(label, Theme.UiSm, brush, plot.Right - sz.Width - 4, y - sz.Height - 1);
    }

    private void DrawCentered(Graphics g, string text, Rectangle area)
    {
        using var brush = new SolidBrush(Theme.Sub);
        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(text, Font, brush, area, sf);
    }
}
