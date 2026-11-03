using System.Text.Json;
using WeeklyQuotaMonitor;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor.BusinessTests;

internal static class Gpt61SolTests
{
    /// <summary>验证 6.1 Sol 官方价格、Fast 倍率、旧设置迁移与持久化混合模型历史重算。</summary>
    public static void Run()
    {
        var boundary = PublicApiPricing.Calculate("gpt-6.1-sol", "standard", new(272000, 200000, 0, 1000, 0));
        Check(boundary.Success && !boundary.IsLongContext && boundary.CostUsd == 0.174m,
            "6.1 Sol 的 272K 短上下文价格错误");
        var longUsage = new TokenUsage(272001, 200000, 0, 1000, 0);
        var standard = PublicApiPricing.Calculate("gpt-6.1-sol", "default", longUsage);
        Check(standard.Success && standard.IsLongContext && standard.CostUsd == 0.174002m &&
            standard.OfficialLongContextCostUsd == 0.343004m,
            "6.1 Sol 的基础/官方长上下文价格错误");
        foreach (var tier in new[] { "fast", "priority" })
        {
            var fast = PublicApiPricing.Calculate("gpt-6.1-sol", tier, longUsage);
            Check(fast.Success && fast.CreditMultiplier == 2.5m &&
                fast.CostUsd == standard.CostUsd * 2.5m &&
                fast.OfficialLongContextCostUsd == standard.OfficialLongContextCostUsd * 2.5m,
                "6.1 Sol Fast 倍率错误");
        }
        Check(!PublicApiPricing.Calculate("gpt-6.1-sol", "unknown", longUsage).Success,
            "未知速度不得猜测 Fast 倍率");
        Check(PublicApiPricing.Calculate("gpt-6.1-sol", "standard", new(100000, 80000, 1000, 1000, 0)).CostUsd == 0.0585m,
            "6.1 Sol 的 API 缓存写价格错误");

        var customOld = new ModelPriceProfile("gpt-6-sol", new(7, 1, 0, 30), new(14, 2, 0, 45), 272000);
        var existing = PublicApiPricing.GetBuiltInProfiles()
            .Where(p => p.Model is not ("gpt-6.1-sol" or "gpt-6-sol"))
            .Append(customOld).ToList();
        var settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings
        { SettingsSchemaVersion = 8, ModelPrices = existing }))!;
        Check(AppSettingsMigration.Apply(settings) && settings.SettingsSchemaVersion == AppSettingsMigration.CurrentVersion,
            "旧设置未升级到 6.1 Sol 价格版本");
        Check(settings.ModelPrices.Single(p => p.Model == "gpt-6-sol") == customOld &&
            settings.ModelPrices.Count(p => p.Model == "gpt-6.1-sol") == 1,
            "补入新模型时覆盖旧自定义价格或产生重复项");
        Check(!AppSettingsMigration.Apply(settings), "重复迁移改变已有配置");
        var customNew = new ModelPriceProfile("gpt-6.1-sol", new(8, 1, 0, 40), new(16, 2, 0, 60), 272000);
        var withCustomNew = new AppSettings { SettingsSchemaVersion = 8,
            ModelPrices = existing.Append(customNew).ToList() };
        AppSettingsMigration.Apply(withCustomNew);
        Check(withCustomNew.ModelPrices.Single(p => p.Model == "gpt-6.1-sol") == customNew,
            "迁移覆盖用户预先设置的 6.1 Sol 价格");
        Check(PublicApiPricing.CreateCatalog(PublicApiPricing.GetBuiltInProfiles()).PricingVersion ==
            PublicApiPricing.PricingVersion, "复位内置价格未恢复版本");

        var usages = new[]
        {
            new ResponsePricingUsage("gpt-6-sol", "standard", new(100000, 80000, 0, 1000, 0)),
            new ResponsePricingUsage("gpt-6.1-sol", "priority", new(300000, 200000, 0, 2000, 0))
        };
        var sample = new QuotaSample(DateTimeOffset.UtcNow, "codex", 10, 1, 999, 99900,
            usages.Aggregate(TokenUsage.Zero, (sum, item) => sum.Add(item.Usage)), 2, "gpt-6-sol,gpt-6.1-sol")
        { PricingVersion = "previous", PricingUsages = usages };
        var state = JsonSerializer.Deserialize<MonitorState>(JsonSerializer.Serialize(new MonitorState { Samples = [sample] }))!;
        var result = SampleRepricer.Apply(state, PublicApiPricing.BuiltInCatalog);
        Check(result.PricedIntervals == 1 && result.Errors.Count == 0 && state.Samples.Count == 2,
            "混合模型历史重算失败或丢失旧样本");
        var repriced = state.Samples.Single(PublicApiPricing.IsCurrentSample);
        Check(repriced.IntervalApiEquivalentUsd == 0.666m &&
            repriced.OfficialLongContextIntervalApiEquivalentUsd == 1.241m,
            "6.1 Sol Fast 历史金额错误");
        SampleRepricer.Apply(state, PublicApiPricing.BuiltInCatalog);
        Check(state.Samples.Count == 2, "重复重算制造重复样本");
    }

    /// <summary>业务结果不符合预期时保留明确失败信息。</summary>
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
