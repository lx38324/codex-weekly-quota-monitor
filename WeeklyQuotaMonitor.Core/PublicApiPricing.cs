using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WeeklyQuotaMonitor.Core;

/// <summary>
/// 保存某个模型在 Standard API 服务层级和上下文档位下的每百万 token 价格。
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
/// 保存一次完整、不可变的模型价格配置，并用稳定版本标识隔离不同价格口径生成的样本。
/// </summary>
public sealed class PricingCatalog
{
    private readonly IReadOnlyDictionary<string, ModelPriceProfile> _profiles;

    internal PricingCatalog(
        IReadOnlyDictionary<string, ModelPriceProfile> profiles,
        string pricingVersion)
    {
        _profiles = profiles;
        PricingVersion = pricingVersion;
    }

    public string PricingVersion { get; }

    /// <summary>
    /// 按本价格配置计算一次响应的两套 Standard API 等价金额。
    /// </summary>
    /// <param name="model">rollout 中记录的实际模型标识。</param>
    /// <param name="serviceTier">响应的 Standard 或 Fast 服务层级证据。</param>
    /// <param name="usage">逐响应 token 分类。</param>
    /// <returns>成功时包含基础与长上下文金额，失败时保留明确原因。</returns>
    public PricingResult Calculate(string model, string serviceTier, TokenUsage usage) =>
        PublicApiPricing.Calculate(_profiles, model, serviceTier, usage);

    /// <summary>
    /// 判断样本是否属于本价格配置及当前金额定义。
    /// </summary>
    /// <param name="sample">需要参与展示或回归的历史样本。</param>
    /// <returns>样本价格版本和金额定义均匹配时返回 true。</returns>
    public bool IsCurrentSample(QuotaSample sample) =>
        PublicApiPricing.IsSampleForVersion(sample, PricingVersion);

    /// <summary>
    /// 导出按模型标识排序的不可变价格配置，供设置界面编辑副本和持久化。
    /// </summary>
    /// <returns>不暴露内部字典的模型价格列表。</returns>
    public IReadOnlyList<ModelPriceProfile> ExportProfiles() =>
        _profiles.Values.OrderBy(profile => profile.Model, StringComparer.OrdinalIgnoreCase).ToArray();
}

/// <summary>
/// 表示内置或用户指定模型价格计算的成功结果及明确的不可定价原因。
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
/// 维护公开内置价格、创建用户价格配置，并计算基础与长上下文两套 Standard API 等价成本。
/// </summary>
public static class PublicApiPricing
{
    public const string PriceSource = "https://developers.openai.com/api/docs/pricing";
    public const string CreditMultiplierSource = "https://learn.chatgpt.com/docs/agent-configuration/speed#fast-mode";
    public const string PriceObservedDate = "2026-09-07";
    public const string PricingVersion = "openai-standard-2026-09-04-response-boundary-confirmed-reset-dual-long-context-astra-v5";
    public const string AmountDefinition = "standard-api-equivalent-no-long-context-surcharge-v1";
    public const string OfficialLongContextAmountDefinition = "standard-api-equivalent-official-long-context-surcharge-v1";
    public const string AutoReviewPricingAssumption = "codex-auto-review 按 gpt-5.6-luna Standard API 价格计算";

    private static readonly IReadOnlyDictionary<string, ModelPriceProfile> Standard =
        BuildStandardProfiles();

    public static PricingCatalog BuiltInCatalog { get; } = new(Standard, PricingVersion);

    /// <summary>
    /// 返回程序内置价格的排序副本，供首次设置、显式迁移和用户复位使用。
    /// </summary>
    /// <returns>包含全部内置模型且不暴露内部字典的价格列表。</returns>
    public static IReadOnlyList<ModelPriceProfile> GetBuiltInProfiles() =>
        BuiltInCatalog.ExportProfiles();

    /// <summary>
    /// 从用户设置创建不可变价格配置；与内置价格完全一致时沿用内置版本，否则生成内容相关版本。
    /// </summary>
    /// <param name="profiles">每个模型的短上下文价格、可选长上下文价格和阈值。</param>
    /// <returns>可安全贯穿一次实时扫描或历史重放的价格配置。</returns>
    public static PricingCatalog CreateCatalog(IReadOnlyCollection<ModelPriceProfile> profiles)
    {
        if (profiles.Count == 0)
        {
            throw new InvalidDataException("模型价格表至少需要包含一个模型。");
        }

        var normalized = new Dictionary<string, ModelPriceProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            var model = profile.Model?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(model))
            {
                throw new InvalidDataException("模型价格表包含空模型标识。");
            }

            if (profile.ShortContext is null)
            {
                throw new InvalidDataException($"模型 {model} 缺少短上下文价格。");
            }

            ValidateBand(profile.ShortContext, model, "短上下文");
            if (profile.LongContextThresholdTokens <= 0)
            {
                throw new InvalidDataException($"模型 {model} 的长上下文阈值必须大于零。");
            }

            if (profile.LongContext is not null)
            {
                ValidateBand(profile.LongContext, model, "长上下文");
            }

            var normalizedProfile = profile with
            {
                Model = model
            };
            if (!normalized.TryAdd(model, normalizedProfile))
            {
                throw new InvalidDataException($"模型价格表包含重复模型：{model}。");
            }
        }

        if (ProfilesEqual(normalized, Standard))
        {
            return BuiltInCatalog;
        }

        var signature = BuildCanonicalSignature(normalized.Values);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))
            .ToLowerInvariant()[..16];
        return new(normalized, $"{PricingVersion}-custom-{fingerprint}");
    }

    /// <summary>
    /// 使用短上下文基础价格和官方长上下文价格分别计算响应成本，再应用 ChatGPT Fast 额度倍率。
    /// </summary>
    /// <param name="model">rollout 的实际模型标识。</param>
    /// <param name="serviceTier">明确记录的 standard/default/auto 或 fast/priority；未知值失败关闭。</param>
    /// <param name="usage">该模型响应的 token 分类。</param>
    /// <returns>成功时包含美元金额，失败时包含明确原因。</returns>
    public static PricingResult Calculate(string model, string serviceTier, TokenUsage usage)
        => BuiltInCatalog.Calculate(model, serviceTier, usage);

    /// <summary>
    /// 使用给定模型价格字典计算响应成本，由不可变 PricingCatalog 保证一次计算中的价格一致性。
    /// </summary>
    /// <param name="profiles">按规范化模型标识索引的价格配置。</param>
    /// <param name="model">rollout 的实际模型标识。</param>
    /// <param name="serviceTier">明确记录的 standard/default/auto 或 fast/priority。</param>
    /// <param name="usage">该模型响应的 token 分类。</param>
    /// <returns>成功时包含美元金额，失败时包含明确原因。</returns>
    internal static PricingResult Calculate(
        IReadOnlyDictionary<string, ModelPriceProfile> profiles,
        string model,
        string serviceTier,
        TokenUsage usage)
    {
        var uncachedInput = usage.GetUncachedInputTokens();
        if (uncachedInput < 0)
        {
            return PricingResult.Failed($"模型 {model} 的输入 token 分类相加超过 input_tokens。 ");
        }

        var normalizedModel = NormalizeModel(model);
        if (!profiles.TryGetValue(normalizedModel, out var profile))
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
            return PricingResult.Failed($"当前价格配置没有模型 {model} 的长上下文价格。 ");
        }

        var officialBand = longContext ? profile.LongContext! : profile.ShortContext;
        if (usage.CacheWriteInputTokens > 0 &&
            (profile.ShortContext.CacheWritePerMillion is null || officialBand.CacheWritePerMillion is null))
        {
            return PricingResult.Failed($"当前价格配置没有模型 {model} 的缓存写入价格。 ");
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

        if (normalizedModel == "gpt-6-astra" ||
            normalizedModel.StartsWith("gpt-5.6-", StringComparison.Ordinal) ||
            normalizedModel == "gpt-5.5")
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
            ["gpt-6-astra"] = Profile("gpt-6-astra", 10, 1, 12.5m, 50, 20, 2, 25, 75),
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
        IsSampleForVersion(sample, PricingVersion);

    /// <summary>
    /// 判断样本是否使用指定价格版本和当前金额结构，供自定义价格配置隔离历史样本。
    /// </summary>
    /// <param name="sample">需要参与展示、回归或历史替换的样本。</param>
    /// <param name="pricingVersion">本轮价格配置生成的稳定版本。</param>
    /// <returns>金额定义和价格版本均匹配时返回 true。</returns>
    public static bool IsSampleForVersion(QuotaSample sample, string pricingVersion) =>
        string.Equals(sample.AmountDefinition, AmountDefinition, StringComparison.Ordinal) &&
        string.Equals(
            sample.OfficialLongContextAmountDefinition,
            OfficialLongContextAmountDefinition,
            StringComparison.Ordinal) &&
        string.Equals(sample.PricingVersion, pricingVersion, StringComparison.Ordinal);

    /// <summary>
    /// 验证单个上下文价格档位的数值边界，防止负价或无法形成金额的配置进入计算。
    /// </summary>
    /// <param name="band">待验证的短上下文或长上下文价格。</param>
    /// <param name="model">用于错误定位的模型标识。</param>
    /// <param name="contextName">用于错误定位的上下文档位名称。</param>
    private static void ValidateBand(PriceBand band, string model, string contextName)
    {
        if (band.InputPerMillion <= 0 ||
            band.CachedInputPerMillion < 0 ||
            band.OutputPerMillion <= 0 ||
            band.CacheWritePerMillion < 0)
        {
            throw new InvalidDataException(
                $"模型 {model} 的{contextName}价格无效：输入和输出必须大于零，缓存读取与缓存写入不得为负数。");
        }
    }

    /// <summary>
    /// 比较两套规范化价格配置的模型集合与全部计价字段。
    /// </summary>
    /// <param name="left">用户配置规范化后的价格字典。</param>
    /// <param name="right">程序内置价格字典。</param>
    /// <returns>模型及其所有价格字段完全一致时返回 true。</returns>
    private static bool ProfilesEqual(
        IReadOnlyDictionary<string, ModelPriceProfile> left,
        IReadOnlyDictionary<string, ModelPriceProfile> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        return left.All(pair => right.TryGetValue(pair.Key, out var other) && pair.Value == other);
    }

    /// <summary>
    /// 生成与顺序和区域设置无关的价格文本，用于构造自定义价格的稳定样本版本。
    /// </summary>
    /// <param name="profiles">已经通过业务校验的模型价格集合。</param>
    /// <returns>包含模型、阈值及全部价格字段的规范文本。</returns>
    private static string BuildCanonicalSignature(IEnumerable<ModelPriceProfile> profiles)
    {
        var builder = new StringBuilder();
        foreach (var profile in profiles.OrderBy(item => item.Model, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(profile.Model.ToLowerInvariant()).Append('|');
            AppendBand(builder, profile.ShortContext);
            builder.Append('|').Append(
                profile.LongContextThresholdTokens.ToString(CultureInfo.InvariantCulture)).Append('|');
            if (profile.LongContext is null)
            {
                builder.Append("none");
            }
            else
            {
                AppendBand(builder, profile.LongContext);
            }

            builder.Append(';');
        }

        return builder.ToString();
    }

    /// <summary>
    /// 把一个价格档位按固定字段顺序和不变区域格式追加到版本签名。
    /// </summary>
    /// <param name="builder">接收规范文本的构建器。</param>
    /// <param name="band">需要写入的价格档位。</param>
    private static void AppendBand(StringBuilder builder, PriceBand band)
    {
        builder.Append(band.InputPerMillion.ToString("G29", CultureInfo.InvariantCulture)).Append(',');
        builder.Append(band.CachedInputPerMillion.ToString("G29", CultureInfo.InvariantCulture)).Append(',');
        builder.Append(band.CacheWritePerMillion?.ToString("G29", CultureInfo.InvariantCulture) ?? "null").Append(',');
        builder.Append(band.OutputPerMillion.ToString("G29", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 保存一次额度倍率解析结果。
    /// </summary>
    private sealed record CreditMultiplierResult(
        bool Success,
        decimal Multiplier,
        string NormalizedServiceTier,
        string Error);
}
