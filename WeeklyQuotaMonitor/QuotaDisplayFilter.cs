using System.Globalization;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor;

/// <summary>将额度样本投影为隐藏免费审批后的展示副本，不修改样本金额、估值或持久化重算依据。</summary>
public static class QuotaDisplayFilter
{
    /// <summary>只有逐响应明细完整且全部明确免费时隐藏整行；旧计费、缺时间及不完整记录均不猜测为免费。</summary>
    public static bool IsHidden(QuotaSample sample) => sample.PricingUsages.Count > 0 &&
        sample.PricingUsages.All(IsFree) && SampleRepricer.HasCompleteUsage(sample);

    /// <summary>同步投影模型、层级、倍率及 token，避免只删模型名却仍在相邻列显示免费审批的贡献。</summary>
    public static QuotaSample ForDisplay(QuotaSample sample)
    {
        if (!sample.PricingUsages.Any(IsFree) || !SampleRepricer.HasCompleteUsage(sample)) return sample;
        var visible = sample.PricingUsages.Where(item => !IsFree(item)).ToArray();
        var tiers = visible.Select(item => NormalizeTier(item.ServiceTier)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var displayedTiers = sample.ServiceTiers.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(tier => tiers.Contains(NormalizeTier(tier))).ToArray();
        // 无时间的旧审批仍保留；不能将其未知倍率跟随另一次明确免费响应一起抹去。
        var uncertainReview = visible.Any(item => PublicApiPricing.IsAutoReview(item.Model) && item.Timestamp is null &&
            sample.Timestamp >= PublicApiPricing.AutoReviewFreeFrom);
        return sample with
        {
            Usage = visible.Aggregate(TokenUsage.Zero, (sum, item) => sum.Add(item.Usage)),
            ModelResponseCount = visible.Length,
            Models = string.Join(", ", visible.Select(item => item.Model).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)),
            ServiceTiers = string.Join(", ", displayedTiers.Length > 0 ? displayedTiers : tiers.OrderBy(tier => tier).ToArray()),
            CreditMultipliers = string.Join(", ", sample.CreditMultipliers.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(text => uncertainReview || !IsZeroMultiplier(text))),
            PricingUsages = visible
        };
    }

    /// <summary>沿用价格规则的模型名及免费生效时刻；缺少逐响应时间时保留记录，不用样本结束时间追认免费。</summary>
    private static bool IsFree(ResponsePricingUsage item) => item.Timestamp is { } timestamp &&
        SpeedDisplayFilter.IsFreeReview(item.Model, timestamp);

    /// <summary>识别原有倍率列的零值，保留未识别文本而不伪造倍率。</summary>
    private static bool IsZeroMultiplier(string text) => text.EndsWith('x') &&
        decimal.TryParse(text[..^1], NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value == 0;

    /// <summary>比较标准化层级，同时保留原列中已有的 config 来源标记，不将配置恢复伪装为原生记录。</summary>
    private static string NormalizeTier(string tier) => tier.Split('(')[0].Trim().ToLowerInvariant() switch
    {
        "default" or "auto" or "standard" => "standard",
        "priority" or "fast" => "fast",
        var value => value
    };
}
