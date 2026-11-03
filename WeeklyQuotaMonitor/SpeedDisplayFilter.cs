using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor;

/// <summary>只在速度页面隐藏已确认免费的自动审批；不修改 JSONL、速度缓存、遥测数据库或额度重算依据。</summary>
public static class SpeedDisplayFilter
{
    /// <summary>沿用现有计费分界，仅明确的自动审批且时间不早于免费时刻才隐藏，其他辅助模型不受影响。</summary>
    public static bool IsFreeReview(string model, DateTimeOffset timestamp) =>
        PublicApiPricing.IsAutoReview(model) && timestamp >= PublicApiPricing.AutoReviewFreeFrom;

    /// <summary>按每条响应的完成时间过滤，先过滤再生成模型选择、分桶及卡片，确保展示数量和均值口径一致。</summary>
    public static SpeedSample[] VisibleSamples(IReadOnlyList<SpeedSample> samples) =>
        samples.Where(sample => !IsFreeReview(sample.Model, sample.EndedAt)).ToArray();

    /// <summary>过滤遥测展示副本；跨免费分界的聚合桶可能混有旧计费请求，保守保留，不按比例虚构拆桶。</summary>
    public static TelemetryQuery VisibleTelemetry(TelemetryQuery data, DateTimeOffset windowStart)
    {
        var buckets = data.Buckets.Where(bucket => !IsFreeReview(bucket.Model, bucket.Start)).ToArray();
        var models = data.Models.Where(model => !IsFreeReview(model, windowStart) ||
            buckets.Any(bucket => string.Equals(bucket.Model, model, StringComparison.OrdinalIgnoreCase))).ToArray();
        return data with
        {
            Buckets = buckets, Models = models,
            Diagnostics = data.Diagnostics.Where(diagnostic => !IsFreeReview(diagnostic.Model, windowStart)).ToArray(),
            First = buckets.Length == 0 ? null : data.First,
            Last = buckets.Length == 0 ? null : data.Last
        };
    }
}
