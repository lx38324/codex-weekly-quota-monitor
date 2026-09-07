using System.Globalization;

namespace WeeklyQuotaMonitor.Core;

/// <summary>
/// 利用样本持久化的逐响应事实离线重算；缺失明细或无法定价时保留旧记录并报告原因。
/// </summary>
public static class SampleRepricer
{
    /// <summary>
    /// 检查明细是否完整覆盖样本全部响应和 token，避免把部分明细误当作完整成本。
    /// </summary>
    public static bool HasCompleteUsage(QuotaSample sample) =>
        sample.ModelResponseCount > 0 &&
        sample.PricingUsages.Count == sample.ModelResponseCount &&
        sample.PricingUsages.Aggregate(TokenUsage.Zero, (sum, item) => sum.Add(item.Usage)) == sample.Usage;

    /// <summary>
    /// 每个原始采样区间只生成一份目标价格结果；保留旧价格版本，重复调用不会增加重复样本。
    /// 只迁移指定展示版本的区间，避免把更早算法已弃用的记录再次复活；未指定时处理全部区间。
    /// 返回成功覆盖区间数、缺失明细数和明确计价失败，供 UI 决定是否仍需扫描日志。
    /// </summary>
    public static RepricingResult Apply(MonitorState state, PricingCatalog catalog, string? sourcePricingVersion = null)
    {
        var groups = state.Samples.GroupBy(sample =>
            (sample.LimitId, sample.Timestamp, sample.UsedPercent, sample.DeltaPercent))
            .Where(group => sourcePricingVersion is null || group.Any(sample => sample.PricingVersion == sourcePricingVersion))
            .ToArray();
        var priced = 0;
        var missing = 0;
        var errors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var source = group.FirstOrDefault(HasCompleteUsage);
            if (source is null)
            {
                missing++;
                continue;
            }

            if (source.DeltaPercent <= 0)
            {
                errors.Add("样本额度变化必须大于零。");
                continue;
            }

            var results = source.PricingUsages.Select(item =>
                catalog.Calculate(item.Model, item.ServiceTier, item.Usage)).ToArray();
            if (results.Any(result => !result.Success))
            {
                foreach (var failure in results.Where(result => !result.Success))
                    errors.Add(failure.Error);
                continue;
            }

            var cost = results.Sum(result => result.CostUsd);
            var longCost = results.Sum(result => result.OfficialLongContextCostUsd);
            var updated = source with
            {
                IntervalApiEquivalentUsd = cost,
                EstimatedWeeklyQuotaUsd = cost * 100m / source.DeltaPercent,
                OfficialLongContextIntervalApiEquivalentUsd = longCost,
                OfficialLongContextEstimatedWeeklyQuotaUsd = longCost * 100m / source.DeltaPercent,
                AmountDefinition = PublicApiPricing.AmountDefinition,
                OfficialLongContextAmountDefinition = PublicApiPricing.OfficialLongContextAmountDefinition,
                PricingVersion = catalog.PricingVersion,
                CreditMultipliers = string.Join(", ", results.Select(result =>
                    result.CreditMultiplier.ToString("0.###", CultureInfo.InvariantCulture) + "x").Distinct())
            };
            state.Samples.RemoveAll(sample => sample.PricingVersion == catalog.PricingVersion &&
                (sample.LimitId, sample.Timestamp, sample.UsedPercent, sample.DeltaPercent) == group.Key);
            state.Samples.Add(updated);
            priced++;
        }

        state.Samples.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        return new(priced, missing, errors.ToArray());
    }
}

/// <summary>记录本轮离线重算的覆盖范围；缺失与失败不得包装为完成。</summary>
public sealed record RepricingResult(int PricedIntervals, int MissingUsageIntervals, IReadOnlyList<string> Errors);
