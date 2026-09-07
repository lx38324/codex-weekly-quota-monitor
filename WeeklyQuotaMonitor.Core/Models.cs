namespace WeeklyQuotaMonitor.Core;

/// <summary>
/// 表示一次或一组模型响应的 token 用量；输入 token 包含缓存读取和缓存写入部分。
/// </summary>
public sealed record TokenUsage(
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens)
{
    public static TokenUsage Zero { get; } = new(0, 0, 0, 0, 0);

    /// <summary>
    /// 合并两个相同统计口径的 token 用量并返回新值。
    /// </summary>
    /// <param name="other">需要累加的 token 用量。</param>
    /// <returns>各字段相加后的 token 用量。</returns>
    public TokenUsage Add(TokenUsage other) => new(
        InputTokens + other.InputTokens,
        CachedInputTokens + other.CachedInputTokens,
        CacheWriteInputTokens + other.CacheWriteInputTokens,
        OutputTokens + other.OutputTokens,
        ReasoningOutputTokens + other.ReasoningOutputTokens);

    /// <summary>
    /// 返回既未命中缓存、也未被记录为缓存写入的输入 token 数。
    /// </summary>
    /// <returns>未缓存输入 token 数。</returns>
    public long GetUncachedInputTokens() =>
        InputTokens - CachedInputTokens - CacheWriteInputTokens;

    /// <summary>
    /// 判断当前统计是否完全没有 token。
    /// </summary>
    /// <returns>所有字段均为零时返回 true。</returns>
    public bool IsZero() =>
        InputTokens == 0 && CachedInputTokens == 0 && CacheWriteInputTokens == 0 && OutputTokens == 0;
}

/// <summary>
/// 表示一次额度窗口查询得到的服务端快照。
/// </summary>
public sealed record RateLimitSnapshot(
    DateTimeOffset SampledAt,
    string LimitId,
    string? LimitName,
    decimal UsedPercent,
    int WindowDurationMinutes,
    DateTimeOffset ResetsAt);

/// <summary>
/// 表示一次百分比变化区间对应的周额度金额反推样本。
/// </summary>
public sealed record QuotaSample(
    DateTimeOffset Timestamp,
    string LimitId,
    decimal UsedPercent,
    decimal DeltaPercent,
    decimal IntervalApiEquivalentUsd,
    decimal EstimatedWeeklyQuotaUsd,
    TokenUsage Usage,
    int ModelResponseCount,
    string Models)
{
    public string AmountDefinition { get; init; } = string.Empty;
    public decimal OfficialLongContextIntervalApiEquivalentUsd { get; init; }
    public decimal OfficialLongContextEstimatedWeeklyQuotaUsd { get; init; }
    public string OfficialLongContextAmountDefinition { get; init; } = string.Empty;
    public string PricingVersion { get; init; } = string.Empty;
    public string ServiceTiers { get; init; } = string.Empty;
    public string CreditMultipliers { get; init; } = string.Empty;
    public string SampleSource { get; init; } = "live";
    public IReadOnlyList<ResponsePricingUsage> PricingUsages { get; init; } = [];
}

/// <summary>
/// 保存逐响应计价依据；保留每次输入长度，使修改长上下文阈值后仍能精确重算。
/// 不保存对话正文或本地路径。
/// </summary>
public sealed record ResponsePricingUsage(string Model, string ServiceTier, TokenUsage Usage);

/// <summary>
/// 表示图表上的时间与金额坐标。
/// </summary>
public sealed record CurvePoint(DateTimeOffset Timestamp, decimal Value);

/// <summary>
/// 定义样本曲线的聚合或回归方式。
/// </summary>
public enum RegressionMode
{
    Linear,
    TimeWindowSegmented,
    GaussianAggregation
}

/// <summary>
/// 保存回归计算参数，输入由设置界面维护。
/// </summary>
public sealed class RegressionOptions
{
    public RegressionMode Mode { get; set; } = RegressionMode.GaussianAggregation;
    public int LinearLookbackPoints { get; set; } = 120;
    public double SegmentWindowHours { get; set; } = 24;
    public double GaussianBandwidthHours { get; set; } = 12;
    public decimal MaximumSampleUsd { get; set; } = 10000;
}

/// <summary>
/// 保存单个 rollout 文件的增量读取位置和模型响应关联状态。
/// </summary>
public sealed class FileCursorState
{
    public long Offset { get; set; }
    public long CreationTimeUtcTicks { get; set; }
    public string CurrentModel { get; set; } = string.Empty;
    public string CurrentServiceTier { get; set; } = "unknown";
    public bool CurrentServiceTierFromConfig { get; set; }
    public bool PendingModelResponse { get; set; }
    public string PendingResponseModel { get; set; } = string.Empty;
    public string PendingResponseServiceTier { get; set; } = string.Empty;
    public bool PendingResponseServiceTierFromConfig { get; set; }
}

/// <summary>
/// 保存程序跨重启需要延续的监控状态、文件游标和有效采样点。
/// </summary>
public sealed class MonitorState
{
    public int ServiceTierTrackingVersion { get; set; }
    public int ResponseAssociationTrackingVersion { get; set; }
    public int HistoricalReplayVersion { get; set; }
    public string HistoricalReplayPricingVersion { get; set; } = string.Empty;
    public DateTimeOffset? HistoricalReplayCompletedAt { get; set; }
    public int HistoricalReplayCandidateCheckpoints { get; set; }
    public int HistoricalReplayAcceptedCheckpoints { get; set; }
    public int HistoricalReplayRejectedCheckpoints { get; set; }
    public int HistoricalReplayPricedResponses { get; set; }
    public int HistoricalReplayUnpricedResponses { get; set; }
    public int HistoricalReplayUnattributedIntervals { get; set; }
    public int HistoricalReplayAwaitingLogIntervals { get; set; }
    public List<decimal> HistoricalReplayUnattributedUsedPercents { get; set; } = [];
    public Dictionary<string, long> HistoricalReplayUnresolvedFileLengths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int HistoricalReplayMalformedLines { get; set; }
    public int HistoricalArchiveReplayVersion { get; set; }
    public string HistoricalArchiveReplayPricingVersion { get; set; } = string.Empty;
    public DateTimeOffset? HistoricalArchiveReplayCompletedAt { get; set; }
    public int HistoricalArchiveReplayHistoryDays { get; set; }
    public string HistoricalArchiveReplaySourceRoots { get; set; } = string.Empty;
    public int HistoricalArchiveReplayWindowCount { get; set; }
    public int HistoricalArchiveReplaySampleCount { get; set; }
    public int HistoricalArchiveReplayFilesScanned { get; set; }
    public int HistoricalArchiveReplayUnpricedResponses { get; set; }
    public int HistoricalArchiveReplayUnattributedIntervals { get; set; }
    public int HistoricalArchiveReplayMalformedLines { get; set; }
    public List<AuthoritativeRateLimitCheckpoint> AuthoritativeRateLimitCheckpoints { get; set; } = [];
    public bool RolloutFilesPrimed { get; set; }
    public Dictionary<string, FileCursorState> FileCursors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public decimal? ReferenceUsedPercent { get; set; }
    public decimal? LastObservedUsedPercent { get; set; }
    public DateTimeOffset? LastResetAt { get; set; }
    public string LastLimitId { get; set; } = string.Empty;
    public decimal PendingApiEquivalentUsd { get; set; }
    public decimal PendingOfficialLongContextApiEquivalentUsd { get; set; }
    public TokenUsage PendingUsage { get; set; } = TokenUsage.Zero;
    public List<ResponsePricingUsage> PendingPricingUsages { get; set; } = [];
    public int PendingModelResponseCount { get; set; }
    public HashSet<string> PendingModels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> PendingServiceTiers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> PendingCreditMultipliers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string PendingPricingVersion { get; set; } = string.Empty;
    public int PendingUnpricedModelResponses { get; set; }
    public int PendingMalformedRolloutLines { get; set; }
    public int PendingRotatedRolloutFiles { get; set; }
    public int UnattributedPercentChanges { get; set; }
    public int UnpricedModelResponses { get; set; }
    public int MalformedRolloutLines { get; set; }
    public int RotatedRolloutFiles { get; set; }
    public int PrunedRolloutCursors { get; set; }
    public List<QuotaSample> Samples { get; set; } = [];
}

/// <summary>
/// 表示一次增量日志读取形成的可定价和不可定价结果。
/// </summary>
public sealed record RolloutScanResult(
    TokenUsage Usage,
    decimal ApiEquivalentUsd,
    int ModelResponseCount,
    IReadOnlyCollection<string> Models,
    int UnpricedModelResponses,
    IReadOnlyCollection<string> UnpricedModels)
{
    public IReadOnlyList<ResponsePricingUsage> PricingUsages { get; init; } = [];
    public decimal OfficialLongContextApiEquivalentUsd { get; init; }
    public IReadOnlyCollection<string> ServiceTiers { get; init; } = [];
    public IReadOnlyCollection<string> CreditMultipliers { get; init; } = [];
    public string PricingVersion { get; init; } = string.Empty;
    public DateTimeOffset? IncludedSince { get; init; }
    public int MalformedLineCount { get; init; }
    public int RotatedFileCount { get; init; }
    public int PrunedCursorCount { get; init; }

    public static RolloutScanResult Empty { get; } = new(
        TokenUsage.Zero,
        0,
        0,
        [],
        0,
        []);
}

/// <summary>
/// 保存历史重放中一个已关联模型响应的价格无关 token 事实和当前价格计算结果。
/// </summary>
public sealed record HistoricalResponseFact(
    DateTimeOffset Timestamp,
    string SourceFile,
    string Model,
    string ServiceTier,
    TokenUsage Usage,
    bool PricingSucceeded,
    decimal ApiEquivalentUsd,
    decimal OfficialLongContextApiEquivalentUsd,
    string NormalizedServiceTier,
    decimal CreditMultiplier)
{
    public bool ServiceTierRecoveredFromConfig { get; init; }
}

/// <summary>
/// 保存 rollout token_count 携带的一个周额度候选快照。
/// </summary>
public sealed record HistoricalRateLimitCheckpoint(
    DateTimeOffset Timestamp,
    decimal UsedPercent,
    int WindowDurationMinutes,
    DateTimeOffset ResetsAt);

/// <summary>
/// 保存监控程序直接从 App Server 首次观察到的额度检查点，防止后写入的响应污染已结束区间。
/// </summary>
public sealed record AuthoritativeRateLimitCheckpoint(
    DateTimeOffset Timestamp,
    string LimitId,
    decimal UsedPercent,
    int WindowDurationMinutes,
    DateTimeOffset ResetsAt);

/// <summary>
/// 汇总当前窗口历史扫描提取的响应事实、额度候选和数据完整性诊断。
/// </summary>
public sealed record HistoricalRolloutFacts(
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    IReadOnlyList<HistoricalResponseFact> Responses,
    IReadOnlyList<HistoricalRateLimitCheckpoint> RateLimitCheckpoints,
    int FilesScanned,
    int MalformedLineCount)
{
    public string PricingVersion { get; init; } = PublicApiPricing.PricingVersion;
}

/// <summary>
/// 表示一次历史重放生成的当前口径样本和时间线归一化诊断。
/// </summary>
public sealed record HistoricalReplayResult(
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    string LimitId,
    IReadOnlyList<QuotaSample> Samples,
    int CandidateCheckpointCount,
    int AcceptedCheckpointCount,
    int RejectedCheckpointCount,
    int PricedResponseCount,
    int UnpricedResponseCount,
    int UnattributedIntervalCount,
    int AwaitingLogIntervalCount,
    IReadOnlyList<decimal> UnattributedUsedPercents,
    IReadOnlyList<string> UnresolvedSourceFiles,
    int MalformedLineCount)
{
    public string PricingVersion { get; init; } = PublicApiPricing.PricingVersion;
}

/// <summary>
/// 汇总图表保留期内多个旧额度窗口的重建结果和一次性扫描诊断。
/// </summary>
public sealed record HistoricalArchiveReplayResult(
    DateTimeOffset HistoryStart,
    DateTimeOffset HistoryEnd,
    string LimitId,
    IReadOnlyList<HistoricalReplayResult> Windows,
    int FilesScanned,
    int MalformedLineCount)
{
    public string PricingVersion { get; init; } = PublicApiPricing.PricingVersion;
}

/// <summary>
/// 表示托盘、详情窗和图表窗共同消费的只读展示快照。
/// </summary>
public sealed record MonitorViewSnapshot(
    DateTimeOffset UpdatedAt,
    string Status,
    RateLimitSnapshot? RateLimit,
    decimal? EstimatedWeeklyQuotaUsd,
    int SampleCount,
    int UnattributedPercentChanges,
    int UnpricedModelResponses,
    IReadOnlyList<QuotaSample> Samples,
    IReadOnlyList<CurvePoint> RegressionCurve)
{
    public bool ShowingPreviousPrices { get; init; }
    public int? RepricingProgressPercent { get; init; }
    public string PricingVersion { get; init; } = PublicApiPricing.PricingVersion;
    public decimal? OfficialLongContextEstimatedWeeklyQuotaUsd { get; init; }
    public IReadOnlyList<CurvePoint> OfficialLongContextRegressionCurve { get; init; } = [];
    public int ArchivedSampleCount { get; init; }
    public int MalformedRolloutLines { get; init; }
    public int RotatedRolloutFiles { get; init; }
    public int PrunedRolloutCursors { get; init; }
    public int MalformedAppServerMessages { get; init; }
    public bool AppServerConnected { get; init; }
    public DateTimeOffset? HistoricalReplayCompletedAt { get; init; }
    public int HistoricalReplayAcceptedCheckpoints { get; init; }
    public int HistoricalReplayRejectedCheckpoints { get; init; }
    public int HistoricalReplayUnpricedResponses { get; init; }
    public int HistoricalReplayUnattributedIntervals { get; init; }
    public int HistoricalReplayAwaitingLogIntervals { get; init; }
    public IReadOnlyList<decimal> HistoricalReplayUnattributedUsedPercents { get; init; } = [];
    public int HistoricalReplayMalformedLines { get; init; }
    public DateTimeOffset? HistoricalArchiveReplayCompletedAt { get; init; }
    public int HistoricalArchiveReplayWindowCount { get; init; }
    public int HistoricalArchiveReplaySampleCount { get; init; }
    public int HistoricalArchiveReplayFilesScanned { get; init; }
    public int HistoricalArchiveReplayUnpricedResponses { get; init; }
    public int HistoricalArchiveReplayUnattributedIntervals { get; init; }
    public int HistoricalArchiveReplayMalformedLines { get; init; }
}
