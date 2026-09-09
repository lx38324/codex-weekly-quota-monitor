using WeeklyQuotaMonitor.Core;
using System.Drawing.Drawing2D;

namespace WeeklyQuotaMonitor;

/// <summary>
/// 保存基础和官方长上下文两种口径的样本折线与回归曲线显隐状态。
/// </summary>
public sealed record ChartSeriesVisibility(
    bool ShowBaseSamples,
    bool ShowBaseRegression,
    bool ShowOfficialSamples,
    bool ShowOfficialRegression)
{
    public static ChartSeriesVisibility All { get; } = new(true, true, true, true);
}

/// <summary>
/// 使用 WinForms Graphics 绘制原始反推样本折线和所选回归曲线。
/// </summary>
public sealed class QuotaChartControl : Control
{
    private IReadOnlyList<QuotaSample> _samples = [];
    private IReadOnlyList<CurvePoint> _baseCurve = [];
    private IReadOnlyList<CurvePoint> _officialLongContextCurve = [];
    private IReadOnlyList<RegressionContribution> _currentContributions = [];
    private ChartSeriesVisibility _seriesVisibility = ChartSeriesVisibility.All;
    private readonly ToolTip _toolTip = new();
    private IReadOnlyList<ChartHitTarget> _hitTargets = [];
    private ChartHitTarget? _hoverTarget;

    public ChartSeriesVisibility SeriesVisibility => _seriesVisibility;

    public IReadOnlyList<RegressionContribution> CurrentContributions => _currentContributions;
    public ChartTimeNavigation Navigation { get; }

    /// <summary>
    /// 启用双缓冲并设置适合金额时间序列的默认外观。
    /// </summary>
    public QuotaChartControl()
    {
        DoubleBuffered = true;
        BackColor = AppTheme.Current.Surface;
        Font = SystemFonts.MessageBoxFont!;
        ResizeRedraw = true;
        Navigation = new ChartTimeNavigation(this);
        MouseMove += ChartMouseMove;
        MouseLeave += ChartMouseLeave;
    }

    /// <summary>
    /// 按当前主题和语言刷新绘图区，并清除可能已失效的悬停文本。
    /// </summary>
    public void ApplyAppearance()
    {
        BackColor = AppTheme.Current.Surface;
        ForeColor = AppTheme.Current.Text;
        _toolTip.SetToolTip(this, null);
        _hoverTarget = null;
        Invalidate();
    }

    /// <summary>
    /// 替换图表数据并触发重绘。
    /// </summary>
    /// <param name="samples">原始反推样本。</param>
    /// <param name="baseCurve">无长上下文加价口径的回归曲线。</param>
    /// <param name="officialLongContextCurve">官方 >272K 加价口径的回归曲线。</param>
    /// <param name="currentContributions">基础当前估值实际使用的样本时间与相对权重。</param>
    public void SetData(
        IReadOnlyList<QuotaSample> samples,
        IReadOnlyList<CurvePoint> baseCurve,
        IReadOnlyList<CurvePoint> officialLongContextCurve,
        IReadOnlyList<RegressionContribution> currentContributions)
    {
        _samples = samples;
        _baseCurve = baseCurve;
        _officialLongContextCurve = officialLongContextCurve;
        _currentContributions = currentContributions;
        _hitTargets = []; _hoverTarget = null;
        Invalidate();
    }

    /// <summary>
    /// 更新四类图表系列的显隐状态并立即重绘。
    /// </summary>
    /// <param name="visibility">由图表标签页复选框生成的系列配置。</param>
    public void SetSeriesVisibility(ChartSeriesVisibility visibility)
    {
        _seriesVisibility = visibility;
        Invalidate();
    }

    /// <summary>
    /// 绘制坐标轴、网格、时间标签、原始折线、采样点和回归曲线。
    /// </summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var background = new LinearGradientBrush(
            ClientRectangle,
            AppTheme.Current.Surface,
            AppTheme.Current.SurfaceAlternate,
            LinearGradientMode.Vertical);
        e.Graphics.FillRectangle(background, ClientRectangle);

        var scale = DeviceDpi / 96F;
        var plot = new RectangleF(
            76 * scale,
            30 * scale,
            Math.Max(10 * scale, Width - 104 * scale),
            Math.Max(10 * scale, Height - 94 * scale));
        Navigation.SetPlot(plot);
        using var axisPen = new Pen(AppTheme.Current.Border, 1F * scale);
        e.Graphics.DrawLine(axisPen, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
        e.Graphics.DrawLine(axisPen, plot.Left, plot.Top, plot.Left, plot.Bottom);

        if (_samples.Count == 0)
        {
            if (Navigation.Window is { } emptyWindow)
                DrawGridAndLabels(e.Graphics, plot, emptyWindow.Start, emptyWindow.End, 0, 1, scale);
            using var emptyBrush = new SolidBrush(AppTheme.Current.MutedText);
            e.Graphics.DrawString(
                UiText.Get("ChartEmpty"),
                Font,
                emptyBrush,
                plot.Left + 20 * scale,
                plot.Top + 30 * scale);
            DrawLegend(e.Graphics, plot, scale);
            return;
        }

        var baseRaw = _samples
            .OrderBy(sample => sample.Timestamp)
            .Select(sample => new CurvePoint(sample.Timestamp, sample.EstimatedWeeklyQuotaUsd))
            .ToArray();
        var officialLongRaw = _samples
            .OrderBy(sample => sample.Timestamp)
            .Select(sample => new CurvePoint(sample.Timestamp, sample.OfficialLongContextEstimatedWeeklyQuotaUsd))
            .ToArray();
        var visiblePoints = new List<CurvePoint>();
        if (_seriesVisibility.ShowBaseSamples) visiblePoints.AddRange(baseRaw);
        if (_seriesVisibility.ShowBaseRegression) visiblePoints.AddRange(_baseCurve);
        if (_seriesVisibility.ShowOfficialSamples) visiblePoints.AddRange(officialLongRaw);
        if (_seriesVisibility.ShowOfficialRegression) visiblePoints.AddRange(_officialLongContextCurve);
        if (visiblePoints.Count == 0)
        {
            using var emptyBrush = new SolidBrush(AppTheme.Current.MutedText);
            e.Graphics.DrawString(
                UiText.Get("ChartNoVisibleSeries"),
                Font,
                emptyBrush,
                plot.Left + 20 * scale,
                plot.Top + 30 * scale);
            DrawLegend(e.Graphics, plot, scale);
            return;
        }

        var allValues = visiblePoints.Select(point => point.Value).ToArray();
        var minY = allValues.Min();
        var maxY = allValues.Max();
        var padding = maxY == minY ? Math.Max(1, maxY * 0.1m) : (maxY - minY) * 0.1m;
        minY = Math.Max(0, minY - padding);
        maxY += padding;

        var minX = Navigation.Window?.Start ?? visiblePoints.Min(point => point.Timestamp);
        var maxX = Navigation.Window?.End ?? visiblePoints.Max(point => point.Timestamp);
        if (minX == maxX)
        {
            minX = minX.AddMinutes(-30);
            maxX = maxX.AddMinutes(30);
        }

        DrawGridAndLabels(e.Graphics, plot, minX, maxX, minY, maxY, scale);
        if (_seriesVisibility.ShowBaseSamples)
        {
            DrawRawSeries(
                e.Graphics,
                plot,
                baseRaw,
                minX,
                maxX,
                minY,
                maxY,
                scale,
                Color.FromArgb(120, 14, 165, 233),
                Color.FromArgb(115, 14, 165, 233));
        }

        if (_seriesVisibility.ShowOfficialSamples)
        {
            DrawRawSeries(
                e.Graphics,
                plot,
                officialLongRaw,
                minX,
                maxX,
                minY,
                maxY,
                scale,
                Color.FromArgb(150, 135, 84, 196),
                Color.FromArgb(125, 121, 67, 171));
        }

        if (_seriesVisibility.ShowBaseRegression)
        {
            DrawRegressionSeries(
                e.Graphics,
                plot,
                _baseCurve,
                minX,
                maxX,
                minY,
                maxY,
                scale,
                Color.FromArgb(15, 135, 210),
                fillArea: true,
                dashed: false);
        }

        if (_seriesVisibility.ShowOfficialRegression)
        {
            DrawRegressionSeries(
                e.Graphics,
                plot,
                _officialLongContextCurve,
                minX,
                maxX,
                minY,
                maxY,
                scale,
                Color.FromArgb(139, 92, 246),
                fillArea: false,
                dashed: true);
        }

        DrawContributionMarkers(
            e.Graphics,
            plot,
            baseRaw,
            officialLongRaw,
            minX,
            maxX,
            minY,
            maxY,
            scale);
        BuildHitTargets(plot, baseRaw, officialLongRaw, minX, maxX, minY, maxY);
        DrawLegend(e.Graphics, plot, scale);
        DrawHover(e.Graphics, plot, scale);
    }

    /// <summary>释放时间导航事件和样本悬停提示。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) { Navigation.Dispose(); _toolTip.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>
    /// 绘制五等分金额网格和时间网格，并显示横纵轴标签。
    /// </summary>
    private void DrawGridAndLabels(
        Graphics graphics,
        RectangleF plot,
        DateTimeOffset minX,
        DateTimeOffset maxX,
        decimal minY,
        decimal maxY,
        float scale)
    {
        using var gridPen = new Pen(AppTheme.Current.Grid, scale);
        using var labelBrush = new SolidBrush(AppTheme.Current.MutedText);
        for (var index = 0; index <= 5; index++)
        {
            var ratio = index / 5F;
            var y = plot.Bottom - plot.Height * ratio;
            graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            var value = minY + (maxY - minY) * (decimal)ratio;
            var label = $"${value.ToString("N0", UiText.Culture)}";
            var labelSize = graphics.MeasureString(label, Font);
            graphics.DrawString(label, Font, labelBrush, plot.Left - labelSize.Width - 8 * scale, y - labelSize.Height / 2);

            var x = plot.Left + plot.Width * ratio;
            graphics.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
            var timestamp = minX + TimeSpan.FromTicks((long)((maxX - minX).Ticks * ratio));
            var timeLabel = timestamp.LocalDateTime.ToString((maxX - minX).TotalMinutes < 5 ? "HH:mm:ss" : "MM-dd HH:mm", UiText.Culture);
            var timeSize = graphics.MeasureString(timeLabel, Font);
            var timeX = Math.Clamp(
                x - timeSize.Width / 2,
                plot.Left,
                Math.Max(plot.Left, Width - timeSize.Width - 8 * scale));
            graphics.DrawString(timeLabel, Font, labelBrush, timeX, plot.Bottom + 8 * scale);
        }

        graphics.DrawString(UiText.Get("ChartAxisTime"), Font, labelBrush, plot.Right - 70 * scale, plot.Bottom + 34 * scale);
        graphics.DrawString(UiText.Get("ChartAxisAmount"), Font, labelBrush, plot.Left, 6 * scale);
    }

    /// <summary>
    /// 使用指定颜色绘制一个口径的原始样本连线和圆形采样点。
    /// </summary>
    private static void DrawRawSeries(
        Graphics graphics,
        RectangleF plot,
        IReadOnlyList<CurvePoint> points,
        DateTimeOffset minX,
        DateTimeOffset maxX,
        decimal minY,
        decimal maxY,
        float scale,
        Color lineColor,
        Color pointColor)
    {
        var screenPoints = points.Select(point => Map(point, plot, minX, maxX, minY, maxY)).ToArray();
        using var linePen = new Pen(lineColor, 1.2F * scale);
        using var pointBrush = new SolidBrush(pointColor);
        if (screenPoints.Length > 1)
        {
            graphics.DrawLines(linePen, screenPoints);
        }

        foreach (var point in screenPoints)
        {
            graphics.FillEllipse(
                pointBrush,
                point.X - 3.5F * scale,
                point.Y - 3.5F * scale,
                7 * scale,
                7 * scale);
        }
    }

    /// <summary>
    /// 使用指定颜色绘制一个口径的回归或聚合曲线，并可为主曲线增加轻量面积光晕。
    /// </summary>
    private static void DrawRegressionSeries(
        Graphics graphics,
        RectangleF plot,
        IReadOnlyList<CurvePoint> points,
        DateTimeOffset minX,
        DateTimeOffset maxX,
        decimal minY,
        decimal maxY,
        float scale,
        Color color,
        bool fillArea,
        bool dashed)
    {
        if (points.Count == 0)
        {
            return;
        }

        var screenPoints = points.Select(point => Map(point, plot, minX, maxX, minY, maxY)).ToArray();
        if (fillArea && screenPoints.Length > 1)
        {
            var areaPoints = screenPoints
                .Concat([new PointF(screenPoints[^1].X, plot.Bottom), new PointF(screenPoints[0].X, plot.Bottom)])
                .ToArray();
            using var areaPath = new GraphicsPath();
            areaPath.AddPolygon(areaPoints);
            using var areaBrush = new LinearGradientBrush(
                plot,
                Color.FromArgb(42, color),
                Color.FromArgb(3, color),
                LinearGradientMode.Vertical);
            graphics.FillPath(areaBrush, areaPath);
        }

        using var regressionPen = new Pen(color, 2.6F * scale)
        {
            DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid
        };
        if (screenPoints.Length == 1)
        {
            graphics.DrawEllipse(
                regressionPen,
                screenPoints[0].X - 4 * scale,
                screenPoints[0].Y - 4 * scale,
                8 * scale,
                8 * scale);
        }
        else
        {
            graphics.DrawLines(regressionPen, screenPoints);
        }
    }

    /// <summary>
    /// 在当前可见原始系列上用琥珀色圆环标明当前估值贡献样本，并以圆环强弱表达高斯相对权重。
    /// </summary>
    /// <param name="graphics">当前 WinForms 绘图上下文。</param>
    /// <param name="plot">可绘制的数据矩形。</param>
    /// <param name="baseRaw">基础金额原始样本点。</param>
    /// <param name="officialLongRaw">官方长上下文金额原始样本点。</param>
    /// <param name="minX">横轴起始时间。</param>
    /// <param name="maxX">横轴结束时间。</param>
    /// <param name="minY">纵轴最小金额。</param>
    /// <param name="maxY">纵轴最大金额。</param>
    /// <param name="scale">当前窗口 DPI 缩放系数。</param>
    private void DrawContributionMarkers(
        Graphics graphics,
        RectangleF plot,
        IReadOnlyList<CurvePoint> baseRaw,
        IReadOnlyList<CurvePoint> officialLongRaw,
        DateTimeOffset minX,
        DateTimeOffset maxX,
        decimal minY,
        decimal maxY,
        float scale)
    {
        if (_currentContributions.Count == 0 ||
            (!_seriesVisibility.ShowBaseSamples && !_seriesVisibility.ShowOfficialSamples))
        {
            return;
        }

        var weights = _currentContributions
            .GroupBy(contribution => contribution.Timestamp)
            .ToDictionary(
                group => group.Key,
                group => Math.Clamp(group.Max(contribution => contribution.RelativeWeight), 0, 1));
        using var ringPen = new Pen(Color.FromArgb(220, 245, 158, 11), 2F * scale);
        using var centerBrush = new SolidBrush(Color.FromArgb(150, 245, 158, 11));
        var visibleSeries = new List<CurvePoint>();
        if (_seriesVisibility.ShowBaseSamples)
        {
            visibleSeries.AddRange(baseRaw);
        }

        if (_seriesVisibility.ShowOfficialSamples)
        {
            visibleSeries.AddRange(officialLongRaw);
        }

        foreach (var point in visibleSeries)
        {
            if (!weights.TryGetValue(point.Timestamp, out var relativeWeight))
            {
                continue;
            }

            var screenPoint = Map(point, plot, minX, maxX, minY, maxY);
            var radius = (4.5F + 3F * MathF.Sqrt((float)relativeWeight)) * scale;
            var alpha = (int)Math.Round(65 + 190 * relativeWeight);
            ringPen.Color = Color.FromArgb(alpha, 245, 158, 11);
            ringPen.Width = (1.2F + 1.2F * (float)relativeWeight) * scale;
            centerBrush.Color = Color.FromArgb(Math.Max(35, alpha / 2), 245, 158, 11);
            graphics.FillEllipse(
                centerBrush,
                screenPoint.X - 2F * scale,
                screenPoint.Y - 2F * scale,
                4F * scale,
                4F * scale);
            graphics.DrawEllipse(
                ringPen,
                screenPoint.X - radius,
                screenPoint.Y - radius,
                radius * 2,
                radius * 2);
        }
    }

    /// <summary>
    /// 绘制基础口径与官方长上下文口径的样本和回归图例。
    /// </summary>
    private void DrawLegend(Graphics graphics, RectangleF plot, float scale)
    {
        var entries = new List<(string Label, Color Color, bool IsRegression, bool IsContribution)>();
        if (_seriesVisibility.ShowBaseSamples)
            entries.Add((UiText.Get("SeriesBaseSamples"), Color.FromArgb(14, 165, 233), false, false));
        if (_seriesVisibility.ShowBaseRegression)
            entries.Add((UiText.Get("SeriesBaseRegression"), Color.FromArgb(15, 135, 210), true, false));
        if (_seriesVisibility.ShowOfficialSamples)
            entries.Add((UiText.Get("SeriesOfficialSamples"), Color.FromArgb(121, 67, 171), false, false));
        if (_seriesVisibility.ShowOfficialRegression)
            entries.Add((UiText.Get("SeriesOfficialRegression"), Color.FromArgb(139, 92, 246), true, false));
        if (_currentContributions.Count > 0 &&
            (_seriesVisibility.ShowBaseSamples || _seriesVisibility.ShowOfficialSamples))
            entries.Add((UiText.Get("SeriesCurrentContributions"), Color.FromArgb(245, 158, 11), false, true));

        using var textBrush = new SolidBrush(AppTheme.Current.Text);
        var startX = Math.Max(plot.Left + 8 * scale, plot.Right - 440 * scale);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var x = startX + index % 2 * 220 * scale;
            var y = plot.Top + 9 * scale + index / 2 * 23 * scale;
            if (entry.IsContribution)
            {
                using var ringPen = new Pen(entry.Color, 2F * scale);
                graphics.DrawEllipse(ringPen, x, y - 5 * scale, 10 * scale, 10 * scale);
                graphics.DrawString(entry.Label, Font, textBrush, x + 16 * scale, y - 9 * scale);
            }
            else if (entry.IsRegression)
            {
                using var pen = new Pen(entry.Color, 3F * scale)
                {
                    DashStyle = System.Drawing.Drawing2D.DashStyle.Dash
                };
                graphics.DrawLine(pen, x, y, x + 28 * scale, y);
                graphics.DrawString(entry.Label, Font, textBrush, x + 34 * scale, y - 9 * scale);
            }
            else
            {
                using var brush = new SolidBrush(entry.Color);
                graphics.FillEllipse(brush, x, y - 4 * scale, 8 * scale, 8 * scale);
                graphics.DrawString(entry.Label, Font, textBrush, x + 14 * scale, y - 9 * scale);
            }
        }
    }

    /// <summary>
    /// 为当前可见样本建立屏幕命中目标，供鼠标悬停显示精确时间、系列和金额。
    /// </summary>
    private void BuildHitTargets(
        RectangleF plot,
        IReadOnlyList<CurvePoint> baseRaw,
        IReadOnlyList<CurvePoint> officialLongRaw,
        DateTimeOffset minX,
        DateTimeOffset maxX,
        decimal minY,
        decimal maxY)
    {
        var targets = new List<ChartHitTarget>();
        var weights = _currentContributions
            .GroupBy(contribution => contribution.Timestamp)
            .ToDictionary(
                group => group.Key,
                group => Math.Clamp(group.Max(contribution => contribution.RelativeWeight), 0, 1));
        if (_seriesVisibility.ShowBaseSamples)
        {
            targets.AddRange(baseRaw.Select(point => new ChartHitTarget(
                Map(point, plot, minX, maxX, minY, maxY),
                point,
                UiText.Get("SeriesBaseSamples"),
                weights.TryGetValue(point.Timestamp, out var weight) ? weight : null)));
        }

        if (_seriesVisibility.ShowOfficialSamples)
        {
            targets.AddRange(officialLongRaw.Select(point => new ChartHitTarget(
                Map(point, plot, minX, maxX, minY, maxY),
                point,
                UiText.Get("SeriesOfficialSamples"),
                weights.TryGetValue(point.Timestamp, out var weight) ? weight : null)));
        }

        _hitTargets = targets;
    }

    /// <summary>
    /// 在最近样本点处绘制十字线和高对比度选中圆环。
    /// </summary>
    private void DrawHover(Graphics graphics, RectangleF plot, float scale)
    {
        if (_hoverTarget is null)
        {
            return;
        }

        using var guidePen = new Pen(AppTheme.Current.MutedText, scale)
        {
            DashStyle = System.Drawing.Drawing2D.DashStyle.Dot
        };
        using var ringPen = new Pen(AppTheme.Current.Accent, 2F * scale);
        graphics.DrawLine(guidePen, _hoverTarget.ScreenPoint.X, plot.Top, _hoverTarget.ScreenPoint.X, plot.Bottom);
        graphics.DrawLine(guidePen, plot.Left, _hoverTarget.ScreenPoint.Y, plot.Right, _hoverTarget.ScreenPoint.Y);
        graphics.DrawEllipse(
            ringPen,
            _hoverTarget.ScreenPoint.X - 6F * scale,
            _hoverTarget.ScreenPoint.Y - 6F * scale,
            12F * scale,
            12F * scale);
    }

    /// <summary>
    /// 选择鼠标附近最近的样本点并显示区域化提示；离点过远时清除悬停状态。
    /// </summary>
    private void ChartMouseMove(object? sender, MouseEventArgs e)
    {
        if (Navigation.IsDragging) { _toolTip.Hide(this); return; }
        var radius = 12F * DeviceDpi / 96F;
        var nearest = _hitTargets
            .Select(target => new
            {
                Target = target,
                Distance = MathF.Sqrt(
                    MathF.Pow(target.ScreenPoint.X - e.X, 2) +
                    MathF.Pow(target.ScreenPoint.Y - e.Y, 2))
            })
            .Where(item => item.Distance <= radius)
            .MinBy(item => item.Distance)
            ?.Target;
        if (Equals(nearest, _hoverTarget))
        {
            return;
        }

        _hoverTarget = nearest;
        _toolTip.SetToolTip(
            this,
            nearest is null
                ? null
                : nearest.ContributionWeight is double weight
                    ? UiText.Format(
                        "ChartHoverContribution",
                        nearest.Point.Timestamp.LocalDateTime,
                        nearest.Series,
                        nearest.Point.Value,
                        weight)
                    : UiText.Format(
                        "ChartHover",
                        nearest.Point.Timestamp.LocalDateTime,
                        nearest.Series,
                        nearest.Point.Value));
        Invalidate();
    }

    /// <summary>
    /// 鼠标离开绘图区时移除十字线和悬停提示。
    /// </summary>
    private void ChartMouseLeave(object? sender, EventArgs e)
    {
        _hoverTarget = null;
        _toolTip.SetToolTip(this, null);
        Invalidate();
    }

    /// <summary>
    /// 将时间金额坐标映射到绘图区屏幕坐标。
    /// </summary>
    /// <returns>WinForms 绘图坐标。</returns>
    private static PointF Map(
        CurvePoint point,
        RectangleF plot,
        DateTimeOffset minX,
        DateTimeOffset maxX,
        decimal minY,
        decimal maxY)
    {
        var xRatio = (float)((point.Timestamp - minX).TotalMilliseconds / (maxX - minX).TotalMilliseconds);
        var yRatio = maxY == minY ? 0.5F : (float)((point.Value - minY) / (maxY - minY));
        return new(plot.Left + plot.Width * xRatio, plot.Bottom - plot.Height * yRatio);
    }

    /// <summary>
    /// 保存一个可见样本在屏幕上的命中位置、业务系列名称和可选贡献权重。
    /// </summary>
    private sealed record ChartHitTarget(
        PointF ScreenPoint,
        CurvePoint Point,
        string Series,
        double? ContributionWeight);
}
