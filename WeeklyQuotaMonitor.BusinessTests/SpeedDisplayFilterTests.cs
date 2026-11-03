using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor.BusinessTests;

internal static class SpeedDisplayFilterTests
{
    /// <summary>验证免费审批在各展示口径均被排除，旧计费审批与其他模型仍保留，原始记录不修改。</summary>
    public static void Run()
    {
        var cutoff = PublicApiPricing.AutoReviewFreeFrom;
        var now = DateTimeOffset.Now.AddMinutes(-1);
        var normal = new SpeedSample("sol", now.AddSeconds(-10), now, "gpt-6.1-sol", "standard", 200, 50)
            { StreamingStartedAt = now.AddSeconds(-5), StreamingStatus = "available" };
        var free = normal with { Source = "review", Model = "codex-auto-review", OutputTokens = 90000 };
        var paid = free with { StartedAt = cutoff.AddSeconds(-11), EndedAt = cutoff.AddSeconds(-1), OutputTokens = 100 };
        SpeedSample[] original = [normal, free, paid];
        var visible = SpeedDisplayFilter.VisibleSamples(original);
        if (visible.Length != 2 || !visible.Contains(normal) || !visible.Contains(paid) || original.Length != 3)
            throw new InvalidOperationException("免费审批未隐藏或旧计费审批及原始历史被修改。");
        if (!SpeedDisplayFilter.IsFreeReview(" CODEX-AUTO-REVIEW ", cutoff) ||
            SpeedDisplayFilter.IsFreeReview("codex-auto-review", cutoff.AddTicks(-1)) ||
            SpeedDisplayFilter.IsFreeReview("gpt-5.6-luna", now))
            throw new InvalidOperationException("免费审批展示边界与现有计费规则不一致。");

        var view = new MonitorViewSnapshot(now, "过滤合成验证", null, null, 0, 0, 0, [], [])
            { SpeedSamples = [normal, free], SpeedSampleCount = 2, SpeedHistoryHours = 24 };
        using var form = new ChartForm(new AppSettings(), _ => { });
        form.UpdateView(view, new()); form.ShowSpeedTab(); Application.DoEvents();
        var panel = Find<SpeedPanel>(form).Single();
        var model = Find<ComboBox>(panel).Single(c => c.Name == "SpeedModelFilter");
        if (model.Items.Cast<object>().Any(item => item.ToString() == "codex-auto-review"))
            throw new InvalidOperationException("模型筛选器仍显示免费审批。");
        var grid = Find<DataGridView>(panel).Single();
        var metric = Find<ComboBox>(panel).Single(c => c.Name == "SpeedMetric");
        for (var selected = 0; selected < 3; selected++)
        {
            metric.SelectedIndex = selected; Application.DoEvents();
            var expected = selected == 2 ? 40d : 20d;
            var title = UiText.Get(selected == 2 ? "SpeedStreamingTps" : "SpeedTotalTps");
            if (grid.Rows.Count != 1 || Find<SpeedChartControl>(panel).Single().DisplayedSeriesCount != 1 ||
                !Find<MetricCard>(panel).Any(card => card.AccessibleName!.StartsWith(title + ": " + expected.ToString("N2"))))
                throw new InvalidOperationException("免费审批仍影响曲线、表格或卡片加权均值。");
        }
        form.UpdateView(view with { SpeedSamples = original, SpeedSampleCount = 3 }, new());
        Find<ComboBox>(panel).Single(c => c.Name == "SpeedRange").SelectedIndex = 0;
        if (!model.Items.Contains("codex-auto-review") || grid.Rows.Count != 2)
            throw new InvalidOperationException("旧计费审批无法在保留历史范围查看。");

        var freeBucket = new TelemetryBucket(now.AddMinutes(-1), now, "test", "codex-auto-review", "unknown", 100, 9000, 60);
        var normalBucket = freeBucket with { Model = "gpt-6.1-sol", Count = 2, Sum = 200 };
        var query = new TelemetryQuery([freeBucket, normalBucket], ["codex-auto-review", "gpt-6.1-sol"], now.AddMinutes(-1), now, false)
        { Diagnostics = [new("codex-auto-review", 10, 10, 0, 0, 0, 0, 0, 10, 0, "")] };
        var filtered = SpeedDisplayFilter.VisibleTelemetry(query, now.AddHours(-1));
        if (filtered.Buckets.Count != 1 || filtered.Models.Contains("codex-auto-review") || filtered.Diagnostics.Count != 0 ||
            query.Buckets.Count != 2 || query.Diagnostics.Count != 1)
            throw new InvalidOperationException("OTel 免费审批展示过滤不完整或原始查询被修改。");
        var boundaryBucket = freeBucket with { Start = cutoff.AddSeconds(-1), End = cutoff.AddSeconds(1) };
        var boundary = SpeedDisplayFilter.VisibleTelemetry(query with { Buckets = [boundaryBucket] }, cutoff.AddHours(-1));
        if (boundary.Buckets.Count != 1 || !boundary.Models.Contains("codex-auto-review"))
            throw new InvalidOperationException("跨免费分界的聚合桶被误当全部免费丢弃。");
        var empty = SpeedDisplayFilter.VisibleTelemetry(query with { Buckets = [freeBucket], Models = ["codex-auto-review"] }, now.AddHours(-1));
        if (empty.Buckets.Count != 0 || empty.Models.Length != 0 || empty.First.HasValue || empty.Last.HasValue)
            throw new InvalidOperationException("全是免费审批时仍保留空曲线的模型或时间边界。");
    }

    /// <summary>通过公开控件树读取筛选结果和可见指标，不访问正式数据文件。</summary>
    private static IEnumerable<T> Find<T>(Control root) where T : Control => root.Controls.Cast<Control>().SelectMany(c =>
        (c is T match ? new[] { match } : Enumerable.Empty<T>()).Concat(Find<T>(c)));
}
