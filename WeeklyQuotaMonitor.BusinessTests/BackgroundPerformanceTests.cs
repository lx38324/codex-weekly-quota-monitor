using System.Diagnostics;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor.BusinessTests;

internal static class BackgroundPerformanceTests
{
    /// <summary>验证缓存不改变估算结果，改价、替换样本和原地修改参数均即时生效；报告固定工作量耗时。</summary>
    public static void Run()
    {
        var now = DateTimeOffset.UtcNow;
        var version = PublicApiPricing.PricingVersion;
        var samples = Enumerable.Range(0, 1000).Select(i => new QuotaSample(now.AddMinutes(i), "codex", i, 1,
            10, 1000 + i % 100, TokenUsage.Zero, 1, "test")
        {
            PricingVersion = version, AmountDefinition = PublicApiPricing.AmountDefinition,
            OfficialLongContextEstimatedWeeklyQuotaUsd = 1500 + i % 100,
            OfficialLongContextAmountDefinition = PublicApiPricing.OfficialLongContextAmountDefinition
        }).ToArray();
        var options = new RegressionOptions();
        var cache = new RegressionCurveCache();
        var first = cache.Get(samples, options, version);
        if (first.Base.Count != samples.Length) throw new InvalidOperationException("性能夹具样本没有进入估算。");
        if (!ReferenceEquals(first.Base, cache.Get(samples.ToArray(), options, version).Base))
            throw new InvalidOperationException("相同样本的状态刷新仍在重算。");
        foreach (var mode in Enum.GetValues<RegressionMode>())
        {
            options.Mode = mode;
            options.GaussianBandwidthHours = 4; options.SegmentWindowHours = 3; options.LinearLookbackPoints = 30;
            samples[0] = samples[0] with { EstimatedWeeklyQuotaUsd = 2000, OfficialLongContextEstimatedWeeklyQuotaUsd = 3000 };
            var result = cache.Get(samples, options, version);
            if (!result.Base.SequenceEqual(RegressionCalculator.BuildCurve(samples, options, version)) ||
                !result.Official.SequenceEqual(RegressionCalculator.BuildOfficialLongContextCurve(samples, options, version)))
                throw new InvalidOperationException("缓存返回了过期回归结果。");
            var analysis = cache.GetAnalysis(samples, options, version);
            if (!analysis.Base.CurrentContributions.SequenceEqual(RegressionCalculator.Analyze(samples, options, version).CurrentContributions) ||
                !analysis.Official.CurrentContributions.SequenceEqual(RegressionCalculator.AnalyzeOfficialLongContext(samples, options, version).CurrentContributions))
                throw new InvalidOperationException("缓存贡献点与同一次回归结果不一致。");
            if (cache.GetAnalysis(samples, options, version, false).Official.Curve.Count != 0 ||
                cache.GetAnalysis(samples, options, version, true).Official.Curve.Count == 0)
                throw new InvalidOperationException("切换实验口径未正确失效缓存。");
        }
        if (cache.Get(samples, options, "different-price-version").Base.Count != 0)
            throw new InvalidOperationException("价格切换后仍返回旧版曲线。");
        options.Mode = RegressionMode.GaussianAggregation;
        cache.Get(samples, options, version);
        const int repeats = 20;
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < repeats; i++)
        {
            RegressionCalculator.BuildCurve(samples, options, version);
            RegressionCalculator.BuildOfficialLongContextCurve(samples, options, version);
        }
        var before = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        for (var i = 0; i < repeats; i++) cache.Get(samples, options, version);
        Console.WriteLine($"后台曲线刷新基准：1000样本×{repeats}次，原始={before:F2}ms，复用={timer.Elapsed.TotalMilliseconds:F2}ms；不代表整机CPU比例。");
        VerifyVisibleRefresh(samples, options);
    }

    /// <summary>用真实窗口验证重复打开不重绑额度表、隐藏速度页不做刷新，切回速度页使用最新数据，并报告耗时。</summary>
    private static void VerifyVisibleRefresh(QuotaSample[] samples, RegressionOptions options)
    {
        var now = DateTimeOffset.Now;
        samples = samples.Select((s, i) => s with { Timestamp = now.AddMinutes(i - samples.Length) }).ToArray();
        var view = new MonitorViewSnapshot(now, "性能验证", null, null, samples.Length, 0, 0, samples, []);
        using var form = new ChartForm(new AppSettings { Regression = options }, _ => { });
        form.UpdateView(view, options);
        var timer = Stopwatch.StartNew(); form.ShowDashboardTab(); Application.DoEvents();
        var first = timer.Elapsed.TotalMilliseconds;
        var quotaGrid = Find<DataGridView>(form).Single(g => g.Columns.Cast<DataGridViewColumn>().Any(c => c.DataPropertyName == "UsedPercent"));
        var binding = quotaGrid.DataSource;
        timer.Restart();
        for (var i = 0; i < 10; i++) { form.Hide(); form.ShowDashboardTab(); Application.DoEvents(); }
        Console.WriteLine($"打开性能（本机合成1000点）：首次={first:F2}ms；重复打开10次={timer.Elapsed.TotalMilliseconds:F2}ms。");
        if (!ReferenceEquals(binding, quotaGrid.DataSource)) throw new InvalidOperationException("同样本重复打开仍重绑表格。");
        form.ShowSpeedTab(); Application.DoEvents();
        var panel = Find<SpeedPanel>(form).Single();
        if (Find<ComboBox>(panel).Single(c => c.Name == "SpeedRange").Text != UiText.Get("TimeRange4Hours"))
            throw new InvalidOperationException("速度页未使用统一4小时默认。");
        var speedGrid = Find<DataGridView>(panel).Single();
        var oldSpeed = speedGrid.DataSource;
        form.ShowDashboardTab();
        view = view with { SpeedSamples = [new("perf", now.AddSeconds(-10), now, "gpt-6-astra", "standard", 100, 20)] };
        form.UpdateView(view, options);
        if (!ReferenceEquals(oldSpeed, speedGrid.DataSource)) throw new InvalidOperationException("隐藏速度页仍在重绑数据。");
        form.ShowSpeedTab(); Application.DoEvents();
        if (speedGrid.Rows.Count != 1) throw new InvalidOperationException("切回速度页未显示最新请求。");
    }

    /// <summary>遍历业务控件获取表格，测试不依赖私有字段或页面排列。</summary>
    private static IEnumerable<T> Find<T>(Control root) where T : Control => root.Controls.Cast<Control>().SelectMany(c =>
        (c is T match ? new[] { match } : Enumerable.Empty<T>()).Concat(Find<T>(c)));
}
