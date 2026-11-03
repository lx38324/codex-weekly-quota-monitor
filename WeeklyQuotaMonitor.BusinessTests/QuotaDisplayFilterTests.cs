using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor.BusinessTests;

internal static class QuotaDisplayFilterTests
{
    /// <summary>复现额度表格的 review/0x 残留，同步验证 token、层级、金额不变及历史依据未被修改。</summary>
    public static void Run(string? screenshot = null)
    {
        var now = DateTimeOffset.Now.AddMinutes(-1);
        var free = new ResponsePricingUsage("codex-auto-review", "fast", new(10000, 9000, 0, 100, 30)) { Timestamp = now };
        var paid = new ResponsePricingUsage("gpt-6-astra", "standard", new(1000, 800, 0, 50, 20)) { Timestamp = now };
        var sample = CreateSample(now, [free, paid]);
        var filtered = QuotaDisplayFilter.ForDisplay(sample);
        Check(!QuotaDisplayFilter.IsHidden(sample) && filtered.Models == "gpt-6-astra" && filtered.CreditMultipliers == "1x" &&
            filtered.ServiceTiers == "standard(config)" && filtered.Usage == paid.Usage && filtered.ModelResponseCount == 1,
            "额度行只隐藏了名称，未同步过滤 token、层级和 0 倍率。");
        Check(filtered.IntervalApiEquivalentUsd == sample.IntervalApiEquivalentUsd &&
            filtered.EstimatedWeeklyQuotaUsd == sample.EstimatedWeeklyQuotaUsd && sample.PricingUsages.Count == 2 &&
            sample.Usage == free.Usage.Add(paid.Usage) && sample.Models.Contains("codex-auto-review"),
            "展示过滤改变了金额、周额度或原始计价依据。");
        var paidReview = free with { Timestamp = PublicApiPricing.AutoReviewFreeFrom.AddTicks(-1) };
        var beforeFree = CreateSample(now, [paidReview, paid]);
        Check(ReferenceEquals(QuotaDisplayFilter.ForDisplay(beforeFree), beforeFree) && !QuotaDisplayFilter.IsHidden(beforeFree),
            "免费前的审批历史被隐藏。");
        var unknown = CreateSample(now, [free with { Timestamp = null }, paid]);
        Check(ReferenceEquals(QuotaDisplayFilter.ForDisplay(unknown), unknown), "缺少时间的旧审批被猜测为免费。");
        var partial = sample with { ModelResponseCount = 3 };
        Check(ReferenceEquals(QuotaDisplayFilter.ForDisplay(partial), partial), "部分明细被错误地当作完整依据。");
        var onlyFree = CreateSample(now.AddSeconds(-1), [free]) with
            { Models = "codex-auto-review", IntervalApiEquivalentUsd = 0, EstimatedWeeklyQuotaUsd = 0 };
        Check(QuotaDisplayFilter.IsHidden(onlyFree), "全部免费审批的额度行未隐藏。");

        var view = new MonitorViewSnapshot(now, "额度展示合成验证", null, 1000, 1, 0, 0, [sample, onlyFree], [])
            { PricingVersion = PublicApiPricing.PricingVersion };
        using var form = new ChartForm(new AppSettings(), _ => { });
        form.UpdateView(view, new()); form.ShowDashboardTab(); form.ClientSize = new Size(1260, 860); Application.DoEvents();
        var grid = Find<DataGridView>(form).Single(g => g.Columns.Cast<DataGridViewColumn>().Any(c => c.DataPropertyName == "UsedPercent"));
        Check(grid.Rows.Count == 1, "额度页仍显示纯免费审批行。");
        foreach (var (property, value) in new (string, object)[] { ("Models", "gpt-6-astra"), ("CreditMultipliers", "1x"),
                     ("ServiceTiers", "standard(config)"), ("InputTokens", 1000L), ("CachedInputTokens", 800L), ("OutputTokens", 50L) })
        {
            var column = grid.Columns.Cast<DataGridViewColumn>().Single(c => c.DataPropertyName == property);
            Check(Equals(grid.Rows[0].Cells[column.Index].Value, value), $"额度表格 {property} 未使用过滤后的展示值。");
        }
        form.UpdateView(view, new()); Application.DoEvents();
        Check(grid.Rows.Count == 1 && sample.PricingUsages.Count == 2, "重复刷新导致免费行复现或修改原始明细。");
        if (screenshot is not null)
        {
            form.UpdateView(view with { Samples = [sample] }, new());
            grid.FirstDisplayedScrollingColumnIndex = grid.Columns.Cast<DataGridViewColumn>().Single(c => c.DataPropertyName == "InputTokens").Index;
            Application.DoEvents();
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(screenshot);
        }
    }

    /// <summary>生成含完整逐响应依据的混合区间，模拟用户截图中的模型和倍率组合。</summary>
    private static QuotaSample CreateSample(DateTimeOffset time, ResponsePricingUsage[] details) => new(time, "codex", 10, 1, 10, 1000,
        details.Aggregate(TokenUsage.Zero, (sum, item) => sum.Add(item.Usage)), details.Length, "codex-auto-review, gpt-6-astra")
    {
        PricingUsages = details, PricingVersion = PublicApiPricing.PricingVersion, AmountDefinition = PublicApiPricing.AmountDefinition,
        OfficialLongContextAmountDefinition = PublicApiPricing.OfficialLongContextAmountDefinition,
        ServiceTiers = "fast, standard(config)", CreditMultipliers = "0x, 1x"
    };

    /// <summary>遍历业务控件读取真实表格结果，不依赖私有字段。</summary>
    private static IEnumerable<T> Find<T>(Control root) where T : Control => root.Controls.Cast<Control>().SelectMany(c =>
        (c is T match ? new[] { match } : Enumerable.Empty<T>()).Concat(Find<T>(c)));

    /// <summary>验收失败时报告具体业务差异。</summary>
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
