namespace WeeklyQuotaMonitor.Core;

/// <summary>
/// 保存某个模型在 Standard API 服务层级和上下文档位下的每百万 token 公开价格。
/// </summary>
public sealed record PriceBand(
    decimal InputPerMillion,
    decimal CachedInputPerMillion,
    decimal? CacheWritePerMillion,
    decimal OutputPerMillion);

/// <summary>
/// 保存模型的短上下文、可选长上下文和长上下文触发阈值。
/// </summary>
public sealed record ModelPriceProfile(
    string Model,
    PriceBand ShortContext,
    PriceBand? LongContext,
    long LongContextThresholdTokens);

/// <summary>
/// 表示公开 API 定价计算的成功结果或明确的不可定价原因。
/// </summary>
public sealed record PricingResult(
    bool Success,
    decimal CostUsd,
    decimal OfficialLongContextCostUsd,
    decimal StandardCostUsd,
    decimal CreditMultiplier,
    bool IsLongContext,
    string NormalizedServiceTier,
    string Error)
{
    /// <summary>
    /// 创建不包含金额的明确不可定价结果。
    /// </summary>
    /// <param name="error">可供诊断和展示的失败原因。</param>
    /// <returns>失败的定价结果。</returns>
    public static PricingResult Failed(string error) => new(false, 0, 0, 0, 0, false, string.Empty, error);

    /// <summary>
    /// 用 Standard API 成本、额度倍率和规范化层级创建成功结果。
    /// </summary>
    /// <param name="baseStandardCostUsd">不含长上下文加价且未乘额度倍率的 Standard API 成本。</param>
    /// <param name="officialLongContextStandardCostUsd">按官方长上下文规则且未乘额度倍率的成本。</param>
    /// <param name="creditMultiplier">ChatGPT 额度消耗倍率。</param>
    /// <param name="normalizedServiceTier">standard 或 fast。</param>
    /// <param name="isLongContext">输入是否超过模型的 272K 长上下文阈值。</param>
    /// <returns>包含 Standard API 等价金额的成功结果。</returns>
    public static PricingResult Priced(
        decimal baseStandardCostUsd,
        decimal officialLongContextStandardCostUsd,
        decimal creditMultiplier,
        string normalizedServiceTier,
        bool isLongContext)
    {
        var costUsd = baseStandardCostUsd * creditMultiplier;
        return new(
            true,
            costUsd,
            officialLongContextStandardCostUsd * creditMultiplier,
            baseStandardCostUsd,
            creditMultiplier,
            isLongContext,
            normalizedServiceTier,
            string.Empty);
    }
}

/// <summary>
/// 并行计算无长上下文加价和官方 >272K 加价两套 Standard API 等价成本。
/// </summary>
public static class PublicApiPricing
{
    public const string PriceSource = "https://developers.openai.com/api/docs/pricing";
    public const string CreditMultiplierSource = "https://learn.chatgpt.com/docs/agent-configuration/speed#fast-mode";
    public const string PriceObservedDate = "2026-08-24";
    public const string PricingVersion = "openai-standard-2026-08-24-response-boundary-confirmed-reset-dual-long-context-v4";
    public const string AmountDefinition = "standard-api-equivalent-no-long-context-surcharge-v1";
    public const string OfficialLongContextAmountDefinition = "standard-api-equivalent-official-long-context-surcharge-v1";
    public const string AutoReviewPricingAssumption = "codex-auto-review 按 gpt-5.6-luna Standard API 价格计算";

    private static readonly IReadOnlyDictionary<string, ModelPriceProfile> Standard =
        BuildStandardProfiles();

    /// <summary>
    /// 使用短上下文基础价格和官方长上下文价格分别计算响应成本，再应用 ChatGPT Fast 额度倍率。
    /// </summary>
    /// <param name="model">rollout 的实际模型标识。</param>
    /// <param name="serviceTier">明确记录的 standard/default/auto 或 fast/priority；未知值失败关闭。</param>
    /// <param name="usage">该模型响应的 token 分类。</param>
    /// <returns>成功时包含美元金额，失败时包含明确原因。</returns>
    public static PricingResult Calculate(string model, string serviceTier, TokenUsage usage)
    {
        var uncachedInput = usage.GetUncachedInputTokens();
        if (uncachedInput < 0)
        {
            return PricingResult.Failed($"模型 {model} 的输入 token 分类相加超过 input_tokens。 ");
        }

        var normalizedModel = NormalizeModel(model);
        if (!Standard.TryGetValue(normalizedModel, out var profile))
        {
            return PricingResult.Failed($"Standard API 价格表未配置模型 {model}。");
        }

        var multiplier = ResolveCreditMultiplier(normalizedModel, serviceTier);
        if (!multiplier.Success)
        {
            return PricingResult.Failed(multiplier.Error);
        }

        var longContext = usage.InputTokens > profile.LongContextThresholdTokens;
        if (longContext && profile.LongContext is null)
        {
            return PricingResult.Failed($"模型 {model} 没有公开的长上下文价格。 ");
        }

        var officialBand = longContext ? profile.LongContext! : profile.ShortContext;
        if (usage.CacheWriteInputTokens > 0 &&
            (profile.ShortContext.CacheWritePerMillion is null || officialBand.CacheWritePerMillion is null))
        {
            return PricingResult.Failed($"模型 {model} 没有公开的缓存写入价格。 ");
        }

        var baseStandardCost = CalculateBandCost(profile.ShortContext, usage, uncachedInput);
        var officialLongContextStandardCost = CalculateBandCost(officialBand, usage, uncachedInput);

        return PricingResult.Priced(
            baseStandardCost,
            officialLongContextStandardCost,
            multiplier.Multiplier,
            multiplier.NormalizedServiceTier,
            longContext);
    }

    /// <summary>
    /// 使用指定价格档位计算未乘 ChatGPT 额度倍率的单次 Standard API 成本。
    /// </summary>
    /// <param name="band">短上下文基础价格或官方长上下文价格。</param>
    /// <param name="usage">该模型响应的 token 分类。</param>
    /// <param name="uncachedInput">已扣除缓存读写后的输入 token。</param>
    /// <returns>指定档位下的 Standard API 美元成本。</returns>
    private static decimal CalculateBandCost(PriceBand band, TokenUsage usage, long uncachedInput) =>
        uncachedInput * band.InputPerMillion / 1_000_000m +
        usage.CachedInputTokens * band.CachedInputPerMillion / 1_000_000m +
        usage.CacheWriteInputTokens * (band.CacheWritePerMillion ?? 0) / 1_000_000m +
        usage.OutputTokens * band.OutputPerMillion / 1_000_000m;

    /// <summary>
    /// 按实际模型和服务层级解析 ChatGPT 额度倍率；官方未定义的组合明确返回失败。
    /// </summary>
    /// <param name="normalizedModel">已规范化的实际模型。</param>
    /// <param name="serviceTier">rollout 中的服务层级。</param>
    /// <returns>成功时包含归一化服务层级和额度倍率。</returns>
    private static CreditMultiplierResult ResolveCreditMultiplier(string normalizedModel, string serviceTier)
    {
        var normalizedTier = serviceTier.Trim().ToLowerInvariant();
        if (normalizedTier is "standard" or "default" or "auto")
        {
            return new(true, 1m, "standard", string.Empty);
        }

        if (normalizedTier is not ("fast" or "priority"))
        {
            return new(false, 0, string.Empty, $"Standard API 等价口径不支持服务层级 {serviceTier}。");
        }

        if (normalizedModel.StartsWith("gpt-5.6-", StringComparison.Ordinal) || normalizedModel == "gpt-5.5")
        {
            return new(true, 2.5m, "fast", string.Empty);
        }

        if (normalizedModel == "gpt-5.4")
        {
            return new(true, 2m, "fast", string.Empty);
        }

        return new(
            false,
            0,
            string.Empty,
            $"官方 ChatGPT Fast 文档未定义模型 {normalizedModel} 的额度倍率。");
    }

    /// <summary>
    /// 统一模型标识的大小写和空白，并应用明确配置的 Auto-review 代理计价模型。
    /// </summary>
    /// <param name="model">原始模型标识。</param>
    /// <returns>用于价格和额度倍率查找的模型标识。</returns>
    private static string NormalizeModel(string model)
    {
        var normalized = model.Trim().ToLowerInvariant();
        if (normalized == "codex-auto-review")
        {
            return "gpt-5.6-luna";
        }

        return normalized;
    }

    /// <summary>
    /// 构造官方标准服务层级价格表，价格单位为美元/百万 token。
    /// </summary>
    /// <returns>按模型标识索引的标准价格表。</returns>
    private static IReadOnlyDictionary<string, ModelPriceProfile> BuildStandardProfiles()
    {
        return new Dictionary<string, ModelPriceProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.6-sol"] = Profile("gpt-5.6-sol", 4, 0.4m, 5, 20, 8, 0.8m, 10, 30),
            ["gpt-5.6-terra"] = Profile("gpt-5.6-terra", 2, 0.2m, 2.5m, 12, 4, 0.4m, 5, 18),
            ["gpt-5.6-luna"] = Profile("gpt-5.6-luna", 0.2m, 0.02m, 0.25m, 1.2m, 0.4m, 0.04m, 0.5m, 1.8m),
            ["gpt-5.5"] = Profile("gpt-5.5", 5, 0.5m, null, 30, 10, 1, null, 45),
            ["gpt-5.4"] = Profile("gpt-5.4", 2.5m, 0.25m, null, 15, 5, 0.5m, null, 22.5m),
            ["gpt-5.4-mini"] = new("gpt-5.4-mini", new(0.75m, 0.075m, null, 4.5m), null, 272_000),
            ["gpt-5.2"] = new("gpt-5.2", new(1.75m, 0.175m, null, 14), null, long.MaxValue),
            ["gpt-5.3-codex"] = new("gpt-5.3-codex", new(1.75m, 0.175m, null, 14), null, long.MaxValue)
        };
    }

    /// <summary>
    /// 以统一的 272K 阈值创建同时具有短、长上下文价格的模型配置。
    /// </summary>
    /// <returns>完整模型价格配置。</returns>
    private static ModelPriceProfile Profile(
        string model,
        decimal shortInput,
        decimal shortCached,
        decimal? shortWrite,
        decimal shortOutput,
        decimal longInput,
        decimal longCached,
        decimal? longWrite,
        decimal longOutput)
    {
        return new(
            model,
            new(shortInput, shortCached, shortWrite, shortOutput),
            new(longInput, longCached, longWrite, longOutput),
            272_000);
    }

    /// <summary>
    /// 判断样本是否使用当前 Standard API 等价口径和价格版本。
    /// </summary>
    /// <param name="sample">需要参与展示或回归的历史样本。</param>
    /// <returns>口径和价格版本均匹配时返回 true。</returns>
    public static bool IsCurrentSample(QuotaSample sample) =>
        string.Equals(sample.AmountDefinition, AmountDefinition, StringComparison.Ordinal) &&
        string.Equals(
            sample.OfficialLongContextAmountDefinition,
            OfficialLongContextAmountDefinition,
            StringComparison.Ordinal) &&
        string.Equals(sample.PricingVersion, PricingVersion, StringComparison.Ordinal);

    /// <summary>
    /// 保存一次额度倍率解析结果。
    /// </summary>
    private sealed record CreditMultiplierResult(
        bool Success,
        decimal Multiplier,
        string NormalizedServiceTier,
        string Error);
}
