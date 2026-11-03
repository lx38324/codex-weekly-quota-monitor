using System.Drawing.Drawing2D;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor;

/// <summary>按模型颜色和速度层级线型绘制分组 TPS；横坐标与下方明细均使用本地时间。</summary>
public sealed class SpeedChartControl : Control
{
    private IReadOnlyList<ChartPoint> _buckets = [];
    private string[] _models = [];
    private string _unit = "TPS";
    private readonly ToolTip _tooltip = new();
    private readonly List<(PointF Point, ChartPoint Bucket)> _hitPoints = [];
    private static readonly Color[] Palette = [Color.FromArgb(14, 165, 233), Color.FromArgb(139, 92, 246),
        Color.FromArgb(16, 185, 129), Color.FromArgb(245, 158, 11), Color.FromArgb(244, 63, 94), Color.FromArgb(6, 182, 212)];

    public int DisplayedSeriesCount => _buckets.Select(bucket => (bucket.Model, bucket.ServiceTier)).Distinct().Count();
    public ChartTimeNavigation Navigation { get; }

    /// <summary>启用双缓冲绘制并提供采样点悬停明细，避免折线值与模型分组难以核对。</summary>
    public SpeedChartControl()
    {
        Name = "SpeedChart";
        Dock = DockStyle.Fill;
        Navigation = new ChartTimeNavigation(this);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        MouseMove += (_, e) => ShowPointDetails(e.Location);
        MouseLeave += (_, _) => _tooltip.Hide(this);
    }

    /// <summary>应用模型筛选与独立指标；流式口径只绘制真实有效样本桶，缺失不能回退原TPS。</summary>
    public void SetData(IReadOnlyList<SpeedBucket> buckets, string[] models, bool visibleTokens, bool streaming = false)
    {
        _buckets = buckets.Where(b => !streaming || b.StreamingTps.HasValue)
            .Select(b => new ChartPoint(b.End, b.Model, b.ServiceTier, streaming ? b.StreamingCount : b.Count,
                streaming ? b.StreamingTps!.Value : visibleTokens ? b.VisibleTps : b.TotalTps)).ToArray();
        _models = models; _unit = "TPS";
        Invalidate();
    }

    /// <summary>绘制同一遥测指标的时间段均值；直接使用毫秒值，绝不伪造 token 数换算 TPS。</summary>
    public void SetLatencyData(IReadOnlyList<TelemetryBucket> buckets, string[] models)
    {
        _buckets = buckets.Select(b => new ChartPoint(b.End, b.Model, b.Tier, b.Count, b.Mean)).ToArray();
        _models = models; _unit = "ms"; Invalidate();
    }

    /// <summary>图例逐项标出模型及速度模式，并按实际文字宽度换行；同模型不同模式不连线。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        _hitPoints.Clear();
        var theme = AppTheme.Current;
        e.Graphics.Clear(theme.Surface);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        var lineHeight = TextRenderer.MeasureText("Ag", Font).Height + (int)(6 * scale);
        var legend = _buckets.Select(b => (b.Model, b.ServiceTier)).Distinct()
            .OrderBy(b => b.Model).ThenBy(b => b.ServiceTier).ToArray();
        var labels = legend.Select(b => $"{b.Model} · {UiText.SpeedTierName(b.ServiceTier)}").ToArray();
        var desiredWidth = Math.Max((int)(230 * scale), labels.Select(label => TextRenderer.MeasureText(label, Font).Width + (int)(50 * scale)).DefaultIfEmpty(0).Max());
        var columns = Math.Max(1, Width / desiredWidth);
        var cellWidth = Width / columns;
        var textWidth = Math.Max(1, cellWidth - (int)(44 * scale));
        var legendHeight = Math.Max(lineHeight, labels.Select(label => TextRenderer.MeasureText(label, Font,
            new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak).Height + (int)(6 * scale)).DefaultIfEmpty(0).Max());
        var legendRows = (int)Math.Ceiling((double)legend.Length / columns);
        var styleText = UiText.Format("SpeedMetricLineStyles", _unit);
        var styleHeight = TextRenderer.MeasureText(styleText, Font, new Size(Math.Max(1, Width - (int)(24 * scale)), int.MaxValue),
            TextFormatFlags.WordBreak).Height;
        var top = legendRows * legendHeight + styleHeight + 8 * scale;
        var plot = new RectangleF(64 * scale, top, Width - 84 * scale, Height - top - 40 * scale);
        Navigation.SetPlot(plot);
        if (plot.Width < 30 || plot.Height < 20) return;
        for (var i = 0; i < legend.Length; i++)
        {
            var x = 12 * scale + i % columns * cellWidth;
            var y = i / columns * legendHeight;
            using var pen = new Pen(ModelColor(legend[i].Model), 2 * scale) { DashStyle = TierDashStyle(legend[i].ServiceTier) };
            e.Graphics.DrawLine(pen, x, y + lineHeight / 2, x + 18 * scale, y + lineHeight / 2);
            DrawMarker(e.Graphics, new PointF(x + 9 * scale, y + lineHeight / 2), pen.Color, legend[i].ServiceTier, scale);
            TextRenderer.DrawText(e.Graphics, labels[i], Font, new Rectangle((int)(x + 24 * scale), y, textWidth, legendHeight), theme.Text, TextFormatFlags.WordBreak);
        }
        TextRenderer.DrawText(e.Graphics, styleText, Font,
            new Rectangle((int)(12 * scale), legendRows * legendHeight, Math.Max(1, Width - (int)(24 * scale)), styleHeight), theme.MutedText, TextFormatFlags.WordBreak);
        if (_buckets.Count == 0 && Navigation.Window is null)
        {
            TextRenderer.DrawText(e.Graphics, UiText.Get("SpeedNoSamples"), Font,
                Rectangle.Round(plot), theme.MutedText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        var minTime = Navigation.Window?.Start ?? _buckets.Min(bucket => bucket.End);
        var maxTime = Navigation.Window?.End ?? _buckets.Max(bucket => bucket.End);
        if (maxTime == minTime) { minTime = minTime.AddMinutes(-1); maxTime = maxTime.AddMinutes(1); }
        var maximum = _buckets.Count == 0 ? 1 : Math.Max(1d, _buckets.Max(Value) * 1.1);
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
            TextRenderer.DrawText(e.Graphics, time.LocalDateTime.ToString((maxTime - minTime).TotalMinutes < 5 ? "HH:mm:ss" : "MM-dd HH:mm"), Font,
                new Rectangle(labelLeft, (int)plot.Bottom + 8, labelWidth, lineHeight),
                theme.MutedText, TextFormatFlags.HorizontalCenter);
        }
        if (_buckets.Count == 0)
            TextRenderer.DrawText(e.Graphics, UiText.Get("SpeedNoSamples"), Font, Rectangle.Round(plot),
                theme.MutedText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        foreach (var series in _buckets.GroupBy(bucket => (bucket.Model, bucket.ServiceTier)))
        {
            using var pen = new Pen(ModelColor(series.Key.Model), 2 * scale)
            { DashStyle = TierDashStyle(series.Key.ServiceTier) };
            PointF? previous = null;
            foreach (var bucket in series.OrderBy(bucket => bucket.End))
            {
                var point = new PointF(plot.Left + (float)((bucket.End - minTime).TotalSeconds /
                    (maxTime - minTime).TotalSeconds) * plot.Width, plot.Bottom - (float)(Value(bucket) / maximum) * plot.Height);
                if (previous is PointF last) e.Graphics.DrawLine(pen, last, point);
                DrawMarker(e.Graphics, point, pen.Color, series.Key.ServiceTier, scale);
                _hitPoints.Add((point, bucket)); previous = point;
            }
        }
    }

    /// <summary>统一图例与折线线型，未知证据采用点线而不是标准实线。</summary>
    private static DashStyle TierDashStyle(string tier) => tier switch
    { "fast" => DashStyle.Dash, "standard" => DashStyle.Solid, _ => DashStyle.Dot };

    /// <summary>单点也通过形状区分模式：标准圆点、Fast 菱形、未知方点。</summary>
    private static void DrawMarker(Graphics graphics, PointF point, Color color, string tier, float scale)
    {
        using var brush = new SolidBrush(color);
        var radius = 3.5f * scale;
        if (tier == "fast") graphics.FillPolygon(brush, new PointF[] { new(point.X, point.Y - radius), new(point.X + radius, point.Y),
            new(point.X, point.Y + radius), new(point.X - radius, point.Y) });
        else if (tier == "standard") graphics.FillEllipse(brush, point.X - radius, point.Y - radius, radius * 2, radius * 2);
        else graphics.FillRectangle(brush, point.X - radius, point.Y - radius, radius * 2, radius * 2);
    }

    /// <summary>读取当前指标的原生数值，绘图与悬停共享同一口径。</summary>
    private double Value(ChartPoint bucket) => bucket.Value;

    /// <summary>自动审查采用中性石板色，避免与青色主模型混淆；其余模型仍按完整列表保持稳定颜色。</summary>
    private Color ModelColor(string model) => string.Equals(model, "codex-auto-review", StringComparison.OrdinalIgnoreCase)
        ? Color.FromArgb(71, 85, 105)
        : Palette[Math.Max(0, Array.IndexOf(_models, model)) % Palette.Length];

    /// <summary>展示距离鼠标最近的样本数、时间、模型和层级，不将不同系列的重叠点混成均值。</summary>
    private void ShowPointDetails(Point point)
    {
        if (Navigation.IsDragging) { _tooltip.Hide(this); return; }
        var nearest = _hitPoints.OrderBy(item => Math.Pow(item.Point.X - point.X, 2) + Math.Pow(item.Point.Y - point.Y, 2)).FirstOrDefault();
        if (nearest.Bucket is null || Math.Abs(nearest.Point.X - point.X) > 12 || Math.Abs(nearest.Point.Y - point.Y) > 12)
        { _tooltip.Hide(this); return; }
        var bucket = nearest.Bucket;
        _tooltip.SetToolTip(this, $"{bucket.Model} · {UiText.SpeedTierName(bucket.ServiceTier)}\n{bucket.End.LocalDateTime:MM-dd HH:mm:ss}\n{Value(bucket):N2} {_unit} · n={bucket.Count}");
    }

    /// <summary>释放图表持有的悬停提示资源。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) { Navigation.Dispose(); _tooltip.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed record ChartPoint(DateTimeOffset End, string Model, string ServiceTier, long Count, double Value);
}
