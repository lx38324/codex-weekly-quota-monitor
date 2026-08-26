namespace WeeklyQuotaMonitor.Core;

/// <summary>
/// 根据有效反推样本生成线性、时间窗口分段或高斯聚合曲线。
/// </summary>
public static class RegressionCalculator
{
    /// <summary>
    /// 过滤旧金额口径、旧价格版本和超过上限的异常样本，再生成与采样时间对齐的曲线。
    /// </summary>
    /// <param name="samples">原始周额度反推样本。</param>
    /// <param name="options">回归模式和窗口参数。</param>
    /// <returns>按时间升序排列的曲线坐标。</returns>
    public static IReadOnlyList<CurvePoint> BuildCurve(
        IReadOnlyList<QuotaSample> samples,
        RegressionOptions options) =>
        BuildCurve(samples, options, sample => sample.EstimatedWeeklyQuotaUsd);

    /// <summary>
    /// 使用官方 >272K 长上下文加价金额生成独立回归曲线。
    /// </summary>
    /// <param name="samples">包含两套金额的原始周额度反推样本。</param>
    /// <param name="options">回归模式和窗口参数。</param>
    /// <returns>按时间升序排列的官方长上下文加价曲线坐标。</returns>
    public static IReadOnlyList<CurvePoint> BuildOfficialLongContextCurve(
        IReadOnlyList<QuotaSample> samples,
        RegressionOptions options) =>
        BuildCurve(samples, options, sample => sample.OfficialLongContextEstimatedWeeklyQuotaUsd);

    /// <summary>
    /// 按指定金额选择器过滤当前版本样本并执行配置的回归或聚合算法。
    /// </summary>
    /// <param name="samples">原始周额度反推样本。</param>
    /// <param name="options">回归模式和窗口参数。</param>
    /// <param name="valueSelector">从单个样本读取目标口径周额度金额的函数。</param>
    /// <returns>按时间升序排列的目标口径曲线坐标。</returns>
    private static IReadOnlyList<CurvePoint> BuildCurve(
        IReadOnlyList<QuotaSample> samples,
        RegressionOptions options,
        Func<QuotaSample, decimal> valueSelector)
    {
        var points = samples
            .Where(PublicApiPricing.IsCurrentSample)
            .OrderBy(sample => sample.Timestamp)
            .Select(sample => new CurvePoint(sample.Timestamp, valueSelector(sample)))
            .Where(point => point.Value > 0)
            .Where(point => point.Value <= options.MaximumSampleUsd)
            .ToArray();

        if (points.Length == 0)
        {
            return [];
        }

        return options.Mode switch
        {
            RegressionMode.Linear => BuildLinear(points, options.LinearLookbackPoints),
            RegressionMode.TimeWindowSegmented => BuildSegmented(points, options.SegmentWindowHours),
            RegressionMode.GaussianAggregation => BuildGaussian(points, options.GaussianBandwidthHours),
            _ => throw new InvalidOperationException($"未知回归模式：{options.Mode}")
        };
    }

    /// <summary>
    /// 返回曲线最后一个时刻的当前估计值；没有有效样本时返回 null。
    /// </summary>
    /// <param name="curve">已计算的回归曲线。</param>
    /// <returns>当前周限额美元估计。</returns>
    public static decimal? CurrentEstimate(IReadOnlyList<CurvePoint> curve) =>
        curve.Count == 0 ? null : curve[^1].Value;

    /// <summary>
    /// 对最近指定数量的样本执行全局最小二乘线性回归。
    /// </summary>
    /// <returns>在每个输入时间点上的线性拟合值。</returns>
    private static IReadOnlyList<CurvePoint> BuildLinear(CurvePoint[] points, int lookbackPoints)
    {
        var count = Math.Clamp(lookbackPoints, 1, points.Length);
        var training = points[^count..];
        var origin = training[0].Timestamp;
        var fit = FitLinear(training, origin);

        return points
            .Select(point => new CurvePoint(
                point.Timestamp,
                ToNonNegativeDecimal(fit.Intercept + fit.Slope * HoursBetween(origin, point.Timestamp))))
            .ToArray();
    }

    /// <summary>
    /// 在每个采样时刻使用其前方时间窗口内的样本执行局部线性回归。
    /// </summary>
    /// <returns>每个采样时刻对应的分段拟合值。</returns>
    private static IReadOnlyList<CurvePoint> BuildSegmented(CurvePoint[] points, double windowHours)
    {
        if (windowHours <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowHours), "时间窗口必须大于零。");
        }

        var result = new List<CurvePoint>(points.Length);
        foreach (var point in points)
        {
            var start = point.Timestamp.AddHours(-windowHours);
            var training = points.Where(candidate => candidate.Timestamp >= start && candidate.Timestamp <= point.Timestamp).ToArray();
            var fit = FitLinear(training, training[0].Timestamp);
            var fitted = fit.Intercept + fit.Slope * HoursBetween(training[0].Timestamp, point.Timestamp);
            result.Add(new(point.Timestamp, ToNonNegativeDecimal(fitted)));
        }

        return result;
    }

    /// <summary>
    /// 在每个采样时刻使用时间距离高斯核加权全部样本，得到平滑聚合曲线。
    /// </summary>
    /// <returns>每个采样时刻对应的高斯加权均值。</returns>
    private static IReadOnlyList<CurvePoint> BuildGaussian(CurvePoint[] points, double bandwidthHours)
    {
        if (bandwidthHours <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bandwidthHours), "高斯带宽必须大于零。");
        }

        var result = new List<CurvePoint>(points.Length);
        foreach (var point in points)
        {
            double weightedTotal = 0;
            double weightTotal = 0;
            foreach (var candidate in points)
            {
                var distance = HoursBetween(point.Timestamp, candidate.Timestamp) / bandwidthHours;
                var weight = Math.Exp(-0.5 * distance * distance);
                weightedTotal += weight * (double)candidate.Value;
                weightTotal += weight;
            }

            result.Add(new(point.Timestamp, ToNonNegativeDecimal(weightedTotal / weightTotal)));
        }

        return result;
    }

    /// <summary>
    /// 对给定时间金额点执行普通最小二乘拟合，单点时返回水平线。
    /// </summary>
    /// <returns>以美元为截距、美元/小时为斜率的拟合参数。</returns>
    private static (double Intercept, double Slope) FitLinear(CurvePoint[] points, DateTimeOffset origin)
    {
        if (points.Length == 1)
        {
            return ((double)points[0].Value, 0);
        }

        var xs = points.Select(point => HoursBetween(origin, point.Timestamp)).ToArray();
        var ys = points.Select(point => (double)point.Value).ToArray();
        var meanX = xs.Average();
        var meanY = ys.Average();
        var denominator = xs.Sum(x => (x - meanX) * (x - meanX));
        if (denominator == 0)
        {
            return (meanY, 0);
        }

        var numerator = xs.Zip(ys, (x, y) => (x - meanX) * (y - meanY)).Sum();
        var slope = numerator / denominator;
        return (meanY - slope * meanX, slope);
    }

    /// <summary>
    /// 计算两个时间点之间带符号的小时差。
    /// </summary>
    /// <returns>结束时间减开始时间的总小时数。</returns>
    private static double HoursBetween(DateTimeOffset start, DateTimeOffset end) =>
        (end - start).TotalHours;

    /// <summary>
    /// 将浮点拟合值转换为非负 decimal，避免图表出现无意义负金额。
    /// </summary>
    /// <returns>不小于零的 decimal 金额。</returns>
    private static decimal ToNonNegativeDecimal(double value) =>
        value <= 0 ? 0 : (decimal)value;
}
