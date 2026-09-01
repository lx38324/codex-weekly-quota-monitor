namespace WeeklyQuotaMonitor.Core;

/// <summary>
/// 表示一个有效样本对当前时刻估值的相对贡献；1 表示当前算法中的最大贡献强度。
/// </summary>
/// <param name="Timestamp">参与估值的样本时间。</param>
/// <param name="RelativeWeight">归一化到 0 到 1 的相对贡献强度。</param>
public sealed record RegressionContribution(DateTimeOffset Timestamp, double RelativeWeight);

/// <summary>
/// 保存同一次回归计算产生的完整曲线和当前估值贡献样本，供金额、计数和图表统一使用。
/// </summary>
/// <param name="Curve">按有效样本时间输出的回归或聚合曲线。</param>
/// <param name="CurrentContributions">当前估值实际使用的样本及相对贡献强度。</param>
public sealed record RegressionAnalysis(
    IReadOnlyList<CurvePoint> Curve,
    IReadOnlyList<RegressionContribution> CurrentContributions)
{
    /// <summary>
    /// 返回曲线最后一个时刻的当前估值；没有有效曲线点时返回 null。
    /// </summary>
    public decimal? CurrentEstimate => Curve.Count == 0 ? null : Curve[^1].Value;
}

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
        Analyze(samples, options).Curve;

    /// <summary>
    /// 按基础 Standard API 等价金额生成曲线，并返回当前估值真正使用的样本及权重。
    /// </summary>
    /// <param name="samples">原始周额度反推样本。</param>
    /// <param name="options">回归模式和窗口参数。</param>
    /// <returns>基础金额曲线、当前估值和贡献样本组成的统一分析结果。</returns>
    public static RegressionAnalysis Analyze(
        IReadOnlyList<QuotaSample> samples,
        RegressionOptions options) =>
        Analyze(samples, options, sample => sample.EstimatedWeeklyQuotaUsd);

    /// <summary>
    /// 使用官方 >272K 长上下文加价金额生成独立回归曲线。
    /// </summary>
    /// <param name="samples">包含两套金额的原始周额度反推样本。</param>
    /// <param name="options">回归模式和窗口参数。</param>
    /// <returns>按时间升序排列的官方长上下文加价曲线坐标。</returns>
    public static IReadOnlyList<CurvePoint> BuildOfficialLongContextCurve(
        IReadOnlyList<QuotaSample> samples,
        RegressionOptions options) =>
        AnalyzeOfficialLongContext(samples, options).Curve;

    /// <summary>
    /// 按官方长上下文加价金额生成曲线，并返回当前估值真正使用的样本及权重。
    /// </summary>
    /// <param name="samples">包含两套金额的原始周额度反推样本。</param>
    /// <param name="options">回归模式和窗口参数。</param>
    /// <returns>官方长上下文金额曲线、当前估值和贡献样本组成的统一分析结果。</returns>
    public static RegressionAnalysis AnalyzeOfficialLongContext(
        IReadOnlyList<QuotaSample> samples,
        RegressionOptions options) =>
        Analyze(samples, options, sample => sample.OfficialLongContextEstimatedWeeklyQuotaUsd);

    /// <summary>
    /// 按指定金额选择器过滤当前版本样本并执行配置的回归或聚合算法。
    /// </summary>
    /// <param name="samples">原始周额度反推样本。</param>
    /// <param name="options">回归模式和窗口参数。</param>
    /// <param name="valueSelector">从单个样本读取目标口径周额度金额的函数。</param>
    /// <returns>目标金额口径的曲线和当前估值贡献样本。</returns>
    private static RegressionAnalysis Analyze(
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
            return new([], []);
        }

        var curve = options.Mode switch
        {
            RegressionMode.Linear => BuildLinear(points, options.LinearLookbackPoints),
            RegressionMode.TimeWindowSegmented => BuildSegmented(points, options.SegmentWindowHours),
            RegressionMode.GaussianAggregation => BuildGaussian(points, options.GaussianBandwidthHours),
            _ => throw new InvalidOperationException($"未知回归模式：{options.Mode}")
        };
        return new(curve, BuildCurrentContributions(points, options));
    }

    /// <summary>
    /// 按当前算法提取最后一个估值时刻使用的样本，并为高斯模式计算归一化相对权重。
    /// </summary>
    /// <param name="points">已通过价格版本、金额和上限过滤的时间金额点。</param>
    /// <param name="options">决定贡献范围和权重的回归参数。</param>
    /// <returns>按样本时间升序排列的当前估值贡献列表。</returns>
    private static IReadOnlyList<RegressionContribution> BuildCurrentContributions(
        CurvePoint[] points,
        RegressionOptions options)
    {
        return options.Mode switch
        {
            RegressionMode.Linear => points[^Math.Clamp(options.LinearLookbackPoints, 1, points.Length)..]
                .Select(point => new RegressionContribution(point.Timestamp, 1))
                .ToArray(),
            RegressionMode.TimeWindowSegmented => points
                .Where(point => point.Timestamp >= points[^1].Timestamp.AddHours(-options.SegmentWindowHours))
                .Select(point => new RegressionContribution(point.Timestamp, 1))
                .ToArray(),
            RegressionMode.GaussianAggregation => points
                .Select(point =>
                {
                    var distance = HoursBetween(points[^1].Timestamp, point.Timestamp) / options.GaussianBandwidthHours;
                    return new RegressionContribution(point.Timestamp, Math.Exp(-0.5 * distance * distance));
                })
                .ToArray(),
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
