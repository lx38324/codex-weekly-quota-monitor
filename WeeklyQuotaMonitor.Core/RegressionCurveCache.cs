namespace WeeklyQuotaMonitor.Core;

/// <summary>复用同一批不可变额度样本的两套曲线；仅状态文字或速度摘要变化时不重复回归。</summary>
public sealed class RegressionCurveCache
{
    private QuotaSample[] _samples = [];
    private (RegressionMode, int, double, double, decimal, string, bool)? _key;
    private (RegressionAnalysis Base, RegressionAnalysis Official) _analysis = (new([], []), new([], []));

    /// <summary>比较样本和全部回归参数值，价格、参数或样本变化立即重算；成功计算后才替换缓存。</summary>
    public (IReadOnlyList<CurvePoint> Base, IReadOnlyList<CurvePoint> Official) Get(
        IReadOnlyList<QuotaSample> samples, RegressionOptions options, string pricingVersion)
    {
        var result = GetAnalysis(samples, options, pricingVersion);
        return (result.Base.Curve, result.Official.Curve);
    }

    /// <summary>同时缓存估值与贡献点，确保界面金额、计数及高亮一致；关闭实验口径时不执行第二套回归。</summary>
    public (RegressionAnalysis Base, RegressionAnalysis Official) GetAnalysis(
        IReadOnlyList<QuotaSample> samples, RegressionOptions options, string pricingVersion, bool includeOfficial = true)
    {
        var key = (options.Mode, options.LinearLookbackPoints, options.SegmentWindowHours,
            options.GaussianBandwidthHours, options.MaximumSampleUsd, pricingVersion, includeOfficial);
        if (_key == key && _samples.SequenceEqual(samples)) return _analysis;
        var snapshot = samples.ToArray();
        var analysis = (RegressionCalculator.Analyze(snapshot, options, pricingVersion),
            includeOfficial ? RegressionCalculator.AnalyzeOfficialLongContext(snapshot, options, pricingVersion) : new RegressionAnalysis([], []));
        _samples = snapshot; _key = key; _analysis = analysis;
        return _analysis;
    }
}
