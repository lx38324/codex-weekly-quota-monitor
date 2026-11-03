using System.Text.Json;
using WeeklyQuotaMonitor;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor.BusinessTests;

internal static class Gpt6SolLunaTests
{
    /// <summary>按官方金额验证新模型、272K 边界、Fast、旧配置补齐及持久化明细重算，不触碰生产数据。</summary>
    public static void Run()
    {
        foreach (var (model, scale) in new[] { ("gpt-6-sol", 1m), ("gpt-6-luna", 0.05m) })
        {
            var exact = PublicApiPricing.Calculate(model, "standard", new(272000, 200000, 0, 1000, 100));
            Check(exact.Success && !exact.IsLongContext && exact.CostUsd == 0.194m * scale, "272K 基础价格错误");
            var usage = new TokenUsage(272001, 200000, 0, 1000, 100);
            var longResult = PublicApiPricing.Calculate(model, "default", usage);
            Check(longResult.Success && longResult.IsLongContext && longResult.CostUsd == 0.194002m * scale &&
                longResult.OfficialLongContextCostUsd == 0.383004m * scale, "长上下文两套口径错误");
            foreach (var tier in new[] { "fast", "priority" })
            {
                var fast = PublicApiPricing.Calculate(model, tier, usage);
                Check(fast.Success && fast.CreditMultiplier == 2.5m && fast.CostUsd == longResult.CostUsd * 2.5m &&
                    fast.OfficialLongContextCostUsd == longResult.OfficialLongContextCostUsd * 2.5m, "Fast 倍率错误");
            }
            Check(!PublicApiPricing.Calculate(model, "unknown", usage).Success, "未知速度不应猜测");
        }
        var custom = new ModelPriceProfile("gpt-6-sol", new(7, 1, 0, 30), new(14, 2, 0, 45), 272000);
        var settings = new AppSettings { SettingsSchemaVersion = 7,
            ModelPrices = PublicApiPricing.GetBuiltInProfiles().Where(p => !p.Model.StartsWith("gpt-6-", StringComparison.Ordinal)).Append(custom).ToList() };
        settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        AppSettingsMigration.Apply(settings);
        Check(settings.ModelPrices.Single(p => p.Model == "gpt-6-sol") == custom, "迁移覆盖用户新模型价格");
        Check(settings.ModelPrices.Any(p => p.Model == "gpt-6-luna"), "旧设置漏补新模型");
        Check(!AppSettingsMigration.Apply(settings), "迁移不幂等");
        var profiles = PublicApiPricing.GetBuiltInProfiles();
        Check(PublicApiPricing.CreateCatalog(profiles.ToArray()).PricingVersion == PublicApiPricing.PricingVersion, "复位未恢复内置价格版本");
        var usages = new[] { new ResponsePricingUsage("gpt-6-sol", "standard", new(100000, 80000, 0, 1000, 100)),
            new ResponsePricingUsage("gpt-6-luna", "fast", new(300000, 200000, 0, 2000, 200)) };
        var sample = new QuotaSample(DateTimeOffset.UtcNow, "codex", 10, 1, 999, 99900,
            usages.Aggregate(TokenUsage.Zero, (sum, item) => sum.Add(item.Usage)), 2, "gpt-6-sol,gpt-6-luna")
        { PricingVersion = "previous", PricingUsages = usages };
        var state = JsonSerializer.Deserialize<MonitorState>(JsonSerializer.Serialize(new MonitorState { Samples = [sample] }))!;
        var result = SampleRepricer.Apply(state, PublicApiPricing.BuiltInCatalog);
        Check(result.PricedIntervals == 1 && result.Errors.Count == 0 && state.Samples.Count == 2, "历史重算丢旧记录或失败");
        var updated = state.Samples.Single(PublicApiPricing.IsCurrentSample);
        Check(updated.IntervalApiEquivalentUsd == 0.0985m && updated.OfficialLongContextIntervalApiEquivalentUsd == 0.12975m,
            "持久化混合模型/速度重算金额错误");
        SampleRepricer.Apply(state, PublicApiPricing.BuiltInCatalog);
        Check(state.Samples.Count == 2, "重复重算制造重复样本");
        Check(PublicApiPricing.Calculate("codex-auto-review", "standard", new(100000, 0, 0, 1000, 0),
            PublicApiPricing.AutoReviewFreeFrom.AddDays(-1)).CostUsd == 0.0212m,
            "擅自修改了 auto-review 的旧模型假设");
    }

    /// <summary>业务结果不符合预期时保留明确原因。</summary>
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
