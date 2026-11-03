using System.Text.Json;
using WeeklyQuotaMonitor;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor.BusinessTests;

internal static class Gpt61TerraTests
{
    /// <summary>按官方 Standard API 价格验证 272K 边界、套餐 Fast、自定义配置迁移及可重算历史，不调用收费接口。</summary>
    public static void Run()
    {
        var boundary = PublicApiPricing.Calculate("gpt-6.1-terra", "standard", new(272000, 200000, 0, 1000, 100));
        Check(boundary.Success && !boundary.IsLongContext && boundary.CostUsd == 0.087m, "Terra 272K 边界或短上下文价格错误。");
        var usage = new TokenUsage(272001, 200000, 0, 1000, 100);
        var standard = PublicApiPricing.Calculate(" GPT-6.1-TERRA ", "default", usage);
        Check(standard.Success && standard.IsLongContext && standard.CostUsd == 0.087001m &&
            standard.OfficialLongContextCostUsd == 0.171502m, "Terra 双口径长上下文金额错误。");
        foreach (var tier in new[] { "fast", "priority" })
        {
            var fast = PublicApiPricing.Calculate("gpt-6.1-terra", tier, usage);
            Check(fast.Success && fast.CreditMultiplier == 2.5m && fast.CostUsd == standard.CostUsd * 2.5m &&
                fast.OfficialLongContextCostUsd == standard.OfficialLongContextCostUsd * 2.5m,
                "Terra 套餐 Fast 不能误用 API Fast 的 2 倍价格。");
        }
        Check(!PublicApiPricing.Calculate("gpt-6.1-terra", "unknown", usage).Success, "未知速度被猜测为可计费层级。");
        Check(PublicApiPricing.Calculate("gpt-6.1-terra", "standard", new(100000, 80000, 1000, 1000, 0)).CostUsd == 0.02925m,
            "Terra Standard API 缓存写价格错误。");

        var customSol = new ModelPriceProfile("gpt-6.1-sol", new(2, 0.1m, null, 10), null, 272000);
        var previous = PublicApiPricing.GetBuiltInProfiles().Where(p => p.Model is not ("gpt-6.1-sol" or "gpt-6.1-terra"))
            .Append(customSol).ToList();
        var settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings
            { SettingsSchemaVersion = 10, ModelPrices = previous }))!;
        Check(AppSettingsMigration.Apply(settings) && settings.SettingsSchemaVersion == 11 &&
            settings.ModelPrices.Single(p => p.Model == "gpt-6.1-sol") == customSol &&
            settings.ModelPrices.Count(p => p.Model == "gpt-6.1-terra") == 1,
            "新模型未补入或覆盖了用户其他模型的缓存写、长上下文配置。");
        Check(!AppSettingsMigration.Apply(settings), "重复迁移产生配置变化。");
        var customTerra = new ModelPriceProfile("gpt-6.1-terra", new(3, 0.2m, 0, 8), new(6, 0.4m, 0, 12), 272000);
        var withCustom = new AppSettings { SettingsSchemaVersion = 10, ModelPrices = previous.Append(customTerra).ToList() };
        AppSettingsMigration.Apply(withCustom);
        Check(withCustom.ModelPrices.Single(p => p.Model == "gpt-6.1-terra") == customTerra, "同名自定义 Terra 价格被覆盖。");
        Check(PublicApiPricing.CreateCatalog(PublicApiPricing.GetBuiltInProfiles()).PricingVersion == PublicApiPricing.PricingVersion,
            "复位内置价格不能恢复当前版本。");

        ResponsePricingUsage[] details = [new("gpt-6.1-sol", "standard", new(100000, 80000, 0, 1000, 100)),
            new("gpt-6.1-terra", "priority", new(300000, 200000, 0, 2000, 200))];
        var sample = new QuotaSample(DateTimeOffset.Now, "codex", 10, 1, 999, 99900,
            details.Aggregate(TokenUsage.Zero, (sum, item) => sum.Add(item.Usage)), 2, "gpt-6.1-sol, gpt-6.1-terra")
            { PricingVersion = "previous", PricingUsages = details };
        var state = JsonSerializer.Deserialize<MonitorState>(JsonSerializer.Serialize(new MonitorState { Samples = [sample] }))!;
        var repriced = SampleRepricer.Apply(state, PublicApiPricing.BuiltInCatalog);
        var updated = state.Samples.Single(PublicApiPricing.IsCurrentSample);
        Check(repriced.Errors.Count == 0 && updated.IntervalApiEquivalentUsd == 0.358m &&
            updated.OfficialLongContextIntervalApiEquivalentUsd == 0.6455m && state.Samples.Count == 2 &&
            updated.PricingUsages.SequenceEqual(details), "Terra 混合速度历史重算金额错误或丢失原始明细。");
        SampleRepricer.Apply(state, PublicApiPricing.BuiltInCatalog);
        Check(state.Samples.Count == 2, "重复重算产生重复历史。");
    }

    /// <summary>验收失败时保留具体业务原因。</summary>
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
