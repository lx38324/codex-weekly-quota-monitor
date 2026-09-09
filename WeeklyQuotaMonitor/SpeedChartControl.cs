using System.Drawing.Drawing2D;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor;

/// <summary>按模型颜色和速度层级线型绘制分组 TPS；横坐标与下方明细均使用本地时间。</summary>
public sealed class SpeedChartControl : Control
{
    private IReadOnlyList<SpeedBucket> _buckets = [];
    private string[] _models = [];
    private bool _visibleTokens;
    private readonly ToolTip _tooltip = new();
    private readonly List<(PointF Point, SpeedBucket Bucket)> _hitPoints = [];
    private static readonly Color[] Palette = [Color.FromArgb(14, 165, 233), Color.FromArgb(139, 92, 246),
        Color.FromArgb(16, 185, 129), Color.FromArgb(245, 158, 11), Color.FromArgb(244, 63, 94), Color.FromArgb(6, 182, 212)];

    public int DisplayedSeriesCount => _buckets.Select(bucket => (bucket.Model, bucket.ServiceTier)).Distinct().Count();

    /// <summary>启用双缓冲绘制并提供采样点悬停明细，避免折线值与模型分组难以核对。</summary>
    public SpeedChartControl()
    {
        Name = "SpeedChart";
        Dock = DockStyle.Fill;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        MouseMove += (_, e) => ShowPointDetails(e.Location);
        MouseLeave += (_, _) => _tooltip.Hide(this);
    }

    /// <summary>应用当前模型筛选与指标选择；模型颜色依据完整模型列表保持稳定。</summary>
    public void SetData(IReadOnlyList<SpeedBucket> buckets, string[] models, bool visibleTokens)
    {
        _buckets = buckets; _models = models; _visibleTokens = visibleTokens;
        Invalidate();
    }

    /// <summary>绘制坐标、模型图例和含样本圆点的折线；空数据与单点均有明确显示。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        _hitPoints.Clear();
        var theme = AppTheme.Current;
        e.Graphics.Clear(theme.Surface);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        var lineHeight = TextRenderer.MeasureText("Ag", Font).Height + (int)(6 * scale);
        var legendModels = _models.Where(model => _buckets.Any(bucket => bucket.Model == model)).ToArray();
        var columns = Math.Max(1, Width / (int)(230 * scale));
        var legendRows = (int)Math.Ceiling((double)legendModels.Length / columns);
        var top = (legendRows + 1) * lineHeight + 6;
        var plot = new RectangleF(64 * scale, top, Width - 84 * scale, Height - top - 40 * scale);
        if (plot.Width < 30 || plot.Height < 20) return;
        for (var i = 0; i < legendModels.Length; i++)
        {
            var x = 12 + i % columns * (Width / columns);
            var y = i / columns * lineHeight;
            using var pen = new Pen(ModelColor(legendModels[i]), 3 * scale);
            e.Graphics.DrawLine(pen, x, y + lineHeight / 2, x + 18 * scale, y + lineHeight / 2);
            TextRenderer.DrawText(e.Graphics, legendModels[i], Font, new Point(x + (int)(24 * scale), y), theme.Text);
        }
        TextRenderer.DrawText(e.Graphics, UiText.Get("SpeedLineStyles"), Font,
            new Point(12, legendRows * lineHeight), theme.MutedText);
        if (_buckets.Count == 0)
        {
            TextRenderer.DrawText(e.Graphics, UiText.Get("SpeedNoSamples"), Font,
                Rectangle.Round(plot), theme.MutedText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        var minTime = _buckets.Min(bucket => bucket.End);
        var maxTime = _buckets.Max(bucket => bucket.End);
        if (maxTime == minTime) { minTime = minTime.AddMinutes(-1); maxTime = maxTime.AddMinutes(1); }
        var maximum = Math.Max(1d, _buckets.Max(Value) * 1.1);
        using var gridPen = new Pen(theme.Border);
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Bottom - plot.Height * i / 4;
            e.Graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            TextRenderer.DrawText(e.Graphics, (maximum * i / 4).ToString("0.#"), Font,
                new Rectangle(0, (int)y - lineHeight / 2, (int)plot.Left - 6, lineHeight), theme.MutedText, TextFormatFlags.Right);
        }
        for (var i = 0; i <= 4; i++)
        {
            var x = plot.Left + plot.Width * i / 4;
            var time = minTime.AddSeconds((maxTime - minTime).TotalSeconds * i / 4);
            var labelWidth = Math.Min(Width, (int)(110 * scale));
            var labelLeft = Math.Clamp((int)x - labelWidth / 2, 0, Width - labelWidth);
            TextRenderer.DrawText(e.Graphics, time.LocalDateTime.ToString("MM-dd HH:mm"), Font,
                new Rectangle(labelLeft, (int)plot.Bottom + 8, labelWidth, lineHeight),
                theme.MutedText, TextFormatFlags.HorizontalCenter);
        }
        foreach (var series in _buckets.GroupBy(bucket => (bucket.Model, bucket.ServiceTier)))
        {
            using var pen = new Pen(ModelColor(series.Key.Model), 2 * scale)
            { DashStyle = series.Key.ServiceTier switch { "fast" => DashStyle.Dash, "standard" => DashStyle.Solid, _ => DashStyle.Dot } };
            PointF? previous = null;
            foreach (var bucket in series.OrderBy(bucket => bucket.End))
            {
                var point = new PointF(plot.Left + (float)((bucket.End - minTime).TotalSeconds /
                    (maxTime - minTime).TotalSeconds) * plot.Width, plot.Bottom - (float)(Value(bucket) / maximum) * plot.Height);
                if (previous is PointF last) e.Graphics.DrawLine(pen, last, point);
                using var brush = new SolidBrush(pen.Color);
                e.Graphics.FillEllipse(brush, point.X - 3 * scale, point.Y - 3 * scale, 6 * scale, 6 * scale);
                _hitPoints.Add((point, bucket)); previous = point;
            }
        }
    }

    /// <summary>选择总输出或扣除推理后的可见输出 TPS，绘图与悬停使用同一数值。</summary>
    private double Value(SpeedBucket bucket) => _visibleTokens ? bucket.VisibleTps : bucket.TotalTps;

    /// <summary>按全局模型排序分配颜色，筛选单个模型时不改变其颜色。</summary>
    private Color ModelColor(string model) => Palette[Math.Max(0, Array.IndexOf(_models, model)) % Palette.Length];

    /// <summary>展示距离鼠标最近的样本数、时间、模型和层级，不将不同系列的重叠点混成均值。</summary>
    private void ShowPointDetails(Point point)
    {
        var nearest = _hitPoints.OrderBy(item => Math.Pow(item.Point.X - point.X, 2) + Math.Pow(item.Point.Y - point.Y, 2)).FirstOrDefault();
        if (nearest.Bucket is null || Math.Abs(nearest.Point.X - point.X) > 12 || Math.Abs(nearest.Point.Y - point.Y) > 12)
        { _tooltip.Hide(this); return; }
        var bucket = nearest.Bucket;
        _tooltip.SetToolTip(this, $"{bucket.Model} · {bucket.ServiceTier}\n{bucket.End.LocalDateTime:MM-dd HH:mm:ss}\n{Value(bucket):N2} TPS · n={bucket.Count}");
    }

    /// <summary>释放图表持有的悬停提示资源。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) _tooltip.Dispose();
        base.Dispose(disposing);
    }
}
