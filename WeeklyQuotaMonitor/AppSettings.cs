using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor;

/// <summary>
/// 保存托盘监控的协议、轮询、采样、回归、展示和开机启动参数。
/// </summary>
public sealed class AppSettings
{
    public int SettingsSchemaVersion { get; set; } = AppSettingsMigration.CurrentVersion;
    public string CodexExecutable { get; set; } = CodexExecutableResolver.DesktopPackageLocator;
    public string CodexArguments { get; set; } = "app-server --listen stdio://";
    public string SessionRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex",
        "sessions");
    public int PollIntervalSeconds { get; set; } = 60;
    public string PreferredLimitId { get; set; } = string.Empty;
    public int MinimumWindowMinutes { get; set; } = 1440;
    public decimal MinimumPercentDelta { get; set; } = 0.1m;
    public int InitialContextLookbackHours { get; set; } = 24;
    public bool StartWithWindows { get; set; } = true;
    public int ChartHistoryDays { get; set; } = 90;
    public int DayStartMinutes { get; set; }
    public bool EnableOfficialLongContextEstimate { get; set; }
    public UiLanguage Language { get; set; } = UiLanguage.Auto;
    public UiTheme Theme { get; set; } = UiTheme.System;
    public RegressionOptions Regression { get; set; } = new();
    public SpeedOptions Speed { get; set; } = new();
    public TelemetryOptions Telemetry { get; set; } = new();
    public List<ModelPriceProfile> ModelPrices { get; set; } = PublicApiPricing.GetBuiltInProfiles().ToList();

    /// <summary>
    /// 验证设置值是否满足协议、轮询和回归算法的硬约束。
    /// </summary>
    /// <returns>所有业务校验错误；空集合表示设置有效。</returns>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(CodexExecutable)) errors.Add(UiText.Get("ValidationCodexExecutable"));
        if (string.IsNullOrWhiteSpace(CodexArguments)) errors.Add(UiText.Get("ValidationAppServerArgs"));
        if (string.IsNullOrWhiteSpace(SessionRoot)) errors.Add(UiText.Get("ValidationSessionRoot"));
        if (PollIntervalSeconds < 10) errors.Add(UiText.Get("ValidationPoll"));
        if (MinimumWindowMinutes < 1) errors.Add(UiText.Get("ValidationWindow"));
        if (MinimumPercentDelta <= 0 || MinimumPercentDelta > 100) errors.Add(UiText.Get("ValidationDelta"));
        if (InitialContextLookbackHours < 1) errors.Add(UiText.Get("ValidationLookback"));
        if (ChartHistoryDays < 1) errors.Add(UiText.Get("ValidationHistory"));
        if (DayStartMinutes is < 0 or >= 1440) errors.Add(UiText.Get("ValidationDayStart"));
        if (Regression.LinearLookbackPoints < 1) errors.Add(UiText.Get("ValidationLinear"));
        if (Regression.SegmentWindowHours <= 0) errors.Add(UiText.Get("ValidationSegment"));
        if (Regression.GaussianBandwidthHours <= 0) errors.Add(UiText.Get("ValidationGaussian"));
        if (Regression.MaximumSampleUsd <= 0) errors.Add(UiText.Get("ValidationMaximum"));
        if (Speed is null || !Enum.IsDefined(Speed.Mode) || Speed.BucketMinutes is < 1 or > 1440 ||
            Speed.ResponsesPerBucket is < 1 or > 10000 || Speed.HistoryHours is < 1 or > 720 ||
            !double.IsFinite(Speed.MinimumDurationSeconds) || Speed.MinimumDurationSeconds is < 1 or > 600)
            errors.Add(UiText.Get("SpeedValidation"));
        ValidateModelPrices(errors);
        if (Telemetry is null || !Telemetry.IsValid()) errors.Add(UiText.Get("OtelValidation"));
        return errors;
    }

    /// <summary>
    /// 验证模型价格表的唯一性、必填价格和长上下文阈值，避免保存后才在日志扫描中失败。
    /// </summary>
    /// <param name="errors">接收区域化业务错误的设置校验集合。</param>
    private void ValidateModelPrices(List<string> errors)
    {
        if (ModelPrices is null || ModelPrices.Count == 0)
        {
            errors.Add(UiText.Get("ValidationPricesMissing"));
            return;
        }

        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in ModelPrices)
        {
            var model = profile.Model?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(model))
            {
                errors.Add(UiText.Get("ValidationPriceModelMissing"));
                continue;
            }

            if (!models.Add(model))
            {
                errors.Add(UiText.Format("ValidationPriceDuplicate", model));
            }

            if (profile.ShortContext is null)
            {
                errors.Add(UiText.Format("ValidationPriceBand", model, UiText.Get("PricingShortContext")));
                continue;
            }

            ValidatePriceBand(errors, model, UiText.Get("PricingShortContext"), profile.ShortContext);
            if (profile.LongContextThresholdTokens <= 0)
            {
                errors.Add(UiText.Format("ValidationPriceLongThreshold", model));
            }

            if (profile.LongContext is null)
            {
                continue;
            }

            ValidatePriceBand(errors, model, UiText.Get("PricingLongContext"), profile.LongContext);
        }
    }

    /// <summary>
    /// 验证一个上下文档位的输入、缓存读取、缓存写入和输出价格数值边界。
    /// </summary>
    /// <param name="errors">接收区域化业务错误的设置校验集合。</param>
    /// <param name="model">用于定位错误的模型标识。</param>
    /// <param name="contextName">短上下文或长上下文的区域化名称。</param>
    /// <param name="band">需要验证的价格档位。</param>
    private static void ValidatePriceBand(
        List<string> errors,
        string model,
        string contextName,
        PriceBand band)
    {
        if (band.InputPerMillion <= 0 ||
            band.CachedInputPerMillion < 0 ||
            band.OutputPerMillion <= 0 ||
            band.CacheWritePerMillion is < 0)
        {
            errors.Add(UiText.Format("ValidationPriceBand", model, contextName));
        }
    }
}

/// <summary>
/// 对设置文件执行显式版本迁移，避免旧默认值在升级后继续阻断有效回归样本。
/// </summary>
public static class AppSettingsMigration
{
    public const int CurrentVersion = 11;
    public const decimal LegacyMaximumSampleUsd = 1000m;
    public const decimal DefaultMaximumSampleUsd = 10000m;

    /// <summary>
    /// 将旧版默认值迁移到当前设置结构，并为尚无价格字段的版本显式写入程序内置模型价格。
    /// </summary>
    /// <param name="settings">从 settings.json 反序列化的设置。</param>
    /// <param name="modelPricesPresent">原始 JSON 是否明确包含模型价格字段。</param>
    /// <returns>设置内容发生变化时返回 true。</returns>
    public static bool Apply(AppSettings settings, bool modelPricesPresent = true)
    {
        if (settings.SettingsSchemaVersion > CurrentVersion)
        {
            throw new InvalidDataException(
                $"settings.json 版本 {settings.SettingsSchemaVersion} 高于程序支持的版本 {CurrentVersion}。");
        }

        var changed = false;
        // 新模型只补齐一次；保留用户已手动添加的同名价格及其他模型的自定义配置。
        // 空价格表仍由正常验证报错，不以迁移掩盖损坏或无效配置。
        if (settings.SettingsSchemaVersion < 8 && settings.ModelPrices is { Count: > 0 })
        {
            foreach (var profile in PublicApiPricing.GetBuiltInProfiles().Where(p => p.Model is "gpt-6-sol" or "gpt-6-luna"))
                if (!settings.ModelPrices.Any(p => string.Equals(p.Model?.Trim(), profile.Model, StringComparison.OrdinalIgnoreCase)))
                {
                    settings.ModelPrices.Add(profile);
                    changed = true;
                }
        }
        // 旧设置只补入 6.1 Sol，不覆盖用户已配置的同名模型价格。
        if (settings.SettingsSchemaVersion < 9 && settings.ModelPrices is { Count: > 0 } &&
            !settings.ModelPrices.Any(p => string.Equals(p.Model?.Trim(), "gpt-6.1-sol", StringComparison.OrdinalIgnoreCase)))
        {
            settings.ModelPrices.Add(PublicApiPricing.GetBuiltInProfiles().Single(p => p.Model == "gpt-6.1-sol"));
            changed = true;
        }
        // 只补入缺失的 6.1 Terra；同名自定义价格和其他模型的缓存写、长上下文设置保持原样。
        if (settings.SettingsSchemaVersion < 11 && settings.ModelPrices is { Count: > 0 } &&
            !settings.ModelPrices.Any(p => string.Equals(p.Model?.Trim(), "gpt-6.1-terra", StringComparison.OrdinalIgnoreCase)))
        {
            settings.ModelPrices.Add(PublicApiPricing.GetBuiltInProfiles().Single(p => p.Model == "gpt-6.1-terra"));
            changed = true;
        }
        if (settings.SettingsSchemaVersion < 5 && !modelPricesPresent)
        {
            settings.ModelPrices = PublicApiPricing.GetBuiltInProfiles().ToList();
            changed = true;
        }

        if (settings.SettingsSchemaVersion < 5 &&
            settings.Regression.MaximumSampleUsd == LegacyMaximumSampleUsd)
        {
            settings.Regression.MaximumSampleUsd = DefaultMaximumSampleUsd;
            changed = true;
        }

        if (settings.SettingsSchemaVersion != CurrentVersion)
        {
            settings.SettingsSchemaVersion = CurrentVersion;
            changed = true;
        }

        return changed;
    }
}

/// <summary>
/// 集中定义当前用户本地应用数据、设置、状态和运行日志路径。
/// </summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexWeeklyQuotaMonitor");

    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "settings.json");
    public static string StateFile { get; } = Path.Combine(DataDirectory, "state.json");
    public static string RuntimeLogFile { get; } = Path.Combine(DataDirectory, "runtime.log");
    public static string SpeedFile { get; } = Path.Combine(DataDirectory, "speed.json");
}
