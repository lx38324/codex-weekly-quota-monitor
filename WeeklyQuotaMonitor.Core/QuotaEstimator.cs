namespace WeeklyQuotaMonitor.Core;

/// <summary>
/// 描述一次额度快照处理后是否生成样本，以及用户可理解的处理状态。
/// </summary>
public sealed record EstimationUpdate(bool SampleCreated, QuotaSample? Sample, string Status);

/// <summary>
/// 用相邻额度百分比变化和同区间双口径 Standard API 等价成本反推两套周额度金额。
/// </summary>
public static class QuotaEstimator
{
    private static readonly TimeSpan MaximumWindowStartClockSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EarlyResetMinimumShift = TimeSpan.FromHours(1);

    /// <summary>
    /// 合并新日志用量、处理窗口重置并在百分比变化达到阈值时生成反推样本。
    /// </summary>
    /// <param name="state">需要原地更新并持久化的监控状态。</param>
    /// <param name="snapshot">当前服务端额度快照。</param>
    /// <param name="scan">自上次额度变化以来新增的本机日志用量。</param>
    /// <param name="minimumPercentDelta">形成有效样本所需的最小百分比变化。</param>
    /// <returns>是否产生样本及本次处理状态。</returns>
    public static EstimationUpdate Process(
        MonitorState state,
        RateLimitSnapshot snapshot,
        RolloutScanResult scan,
        decimal minimumPercentDelta)
    {
        if (minimumPercentDelta <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumPercentDelta), "最小百分比变化必须大于零。");
        }

        ApplyScanDiagnostics(state, scan);
        if (state.ReferenceUsedPercent is null || state.LastObservedUsedPercent is null)
        {
            SetBaseline(state, snapshot);
            return new(false, null, "已建立额度基线，等待下一次百分比变化。");
        }

        var resetConfirmed = IsConfirmedWindowReset(state, snapshot);
        var limitChanged = !string.Equals(state.LastLimitId, snapshot.LimitId, StringComparison.Ordinal);
        var percentDecreased = snapshot.UsedPercent < state.LastObservedUsedPercent;
        if (limitChanged)
        {
            SetBaseline(state, snapshot);
            return new(false, null, "检测到额度桶切换；无法把本机日志可靠分配给新桶，已重新建立基线。");
        }

        if (resetConfirmed)
        {
            var windowStart = GetEffectiveWindowStart(snapshot);
            var scanWasWindowFiltered = scan.IncludedSince is DateTimeOffset includedSince && includedSince >= windowStart;
            PrepareResetWindow(state, snapshot);
            if (!scanWasWindowFiltered)
            {
                state.ReferenceUsedPercent = snapshot.UsedPercent;
                return new(false, null, "检测到额度窗口重置；本轮日志未按新窗口切分，已保守丢弃并建立当前基线。");
            }

            MergeScan(state, scan);
            return TryCreateSample(
                state,
                snapshot,
                minimumPercentDelta,
                "检测到额度窗口重置，已仅使用新窗口开始后的日志。 ");
        }

        if (percentDecreased)
        {
            SetCorrectedBaseline(state, snapshot);
            return new(false, null, "额度百分比下降但未确认跨过旧窗口重置时刻；按疑似服务端修正保守清空区间并重建基线。");
        }

        MergeScan(state, scan);
        UpdateObservedUsage(state, snapshot);
        return TryCreateSample(state, snapshot, minimumPercentDelta, string.Empty);
    }

    /// <summary>
    /// 判断新额度快照是否相对上次观察发生了需要读取日志的变化。
    /// </summary>
    /// <param name="state">当前持久化状态。</param>
    /// <param name="snapshot">新额度快照。</param>
    /// <returns>百分比、已确认窗口重置或额度桶变化时返回 true。</returns>
    public static bool RequiresRolloutScan(MonitorState state, RateLimitSnapshot snapshot)
    {
        return state.LastObservedUsedPercent is not null &&
               (state.LastObservedUsedPercent != snapshot.UsedPercent ||
                 IsConfirmedWindowReset(state, snapshot) ||
                 !string.Equals(state.LastLimitId, snapshot.LimitId, StringComparison.Ordinal));
    }

    /// <summary>
    /// 在确认同一额度桶进入新窗口时，返回日志扫描应纳入计价的当前窗口起点。
    /// </summary>
    /// <param name="state">当前持久化状态。</param>
    /// <param name="snapshot">新额度快照。</param>
    /// <returns>确认窗口重置时返回窗口起点；普通变化或桶切换时返回 null。</returns>
    public static DateTimeOffset? GetRolloutScanStart(MonitorState state, RateLimitSnapshot snapshot)
    {
        if (state.LastObservedUsedPercent is null ||
            !string.Equals(state.LastLimitId, snapshot.LimitId, StringComparison.Ordinal) ||
            !IsConfirmedWindowReset(state, snapshot))
        {
            return null;
        }

        return GetEffectiveWindowStart(snapshot);
    }

    /// <summary>
    /// 仅在服务端给出更晚重置时刻且采样已经跨过旧重置承诺时确认进入新窗口。
    /// </summary>
    /// <param name="state">保存旧窗口重置承诺的持久化状态。</param>
    /// <param name="snapshot">需要判断的当前额度快照。</param>
    /// <returns>可安全按新窗口切分日志时返回 true。</returns>
    private static bool IsConfirmedWindowReset(MonitorState state, RateLimitSnapshot snapshot)
    {
        if (state.LastResetAt is not DateTimeOffset previousResetAt ||
            snapshot.ResetsAt <= previousResetAt)
        {
            return false;
        }

        if (snapshot.SampledAt >= previousResetAt)
        {
            return true;
        }

        var nominalWindowStart = snapshot.ResetsAt.AddMinutes(-snapshot.WindowDurationMinutes);
        var startsNearCurrentObservation =
            Absolute(nominalWindowStart - snapshot.SampledAt) <= MaximumWindowStartClockSkew;
        var hasPersistedZeroObservationNearWindowStart = state.AuthoritativeRateLimitCheckpoints.Any(checkpoint =>
            string.Equals(checkpoint.LimitId, snapshot.LimitId, StringComparison.Ordinal) &&
            checkpoint.WindowDurationMinutes == snapshot.WindowDurationMinutes &&
            checkpoint.UsedPercent == 0 &&
            Absolute(checkpoint.ResetsAt - snapshot.ResetsAt) <= MaximumWindowStartClockSkew &&
            Absolute(checkpoint.Timestamp - nominalWindowStart) <= MaximumWindowStartClockSkew);
        return snapshot.UsedPercent == 0 &&
               snapshot.ResetsAt - previousResetAt >= EarlyResetMinimumShift &&
               (startsNearCurrentObservation || hasPersistedZeroObservationNearWindowStart);
    }

    /// <summary>
    /// 计算不晚于当前观察时刻的有效窗口起点；允许服务端重置边界存在少量时钟偏差。
    /// </summary>
    /// <param name="snapshot">包含重置时间和窗口长度的额度快照。</param>
    /// <returns>可安全作为历史读取下界的窗口起点。</returns>
    public static DateTimeOffset GetEffectiveWindowStart(RateLimitSnapshot snapshot)
    {
        var nominalWindowStart = snapshot.ResetsAt.AddMinutes(-snapshot.WindowDurationMinutes);
        if (nominalWindowStart - snapshot.SampledAt > MaximumWindowStartClockSkew)
        {
            throw new InvalidDataException(
                $"额度窗口起点比采样时间晚 {(nominalWindowStart - snapshot.SampledAt).TotalSeconds:F0} 秒，超过允许的 300 秒时钟偏差。");
        }

        return nominalWindowStart > snapshot.SampledAt ? snapshot.SampledAt : nominalWindowStart;
    }

    /// <summary>
    /// 返回时间跨度的绝对值，用于比较服务端窗口起点与本地观察时刻。
    /// </summary>
    /// <param name="value">需要取绝对值的时间跨度。</param>
    /// <returns>非负时间跨度。</returns>
    private static TimeSpan Absolute(TimeSpan value) => value < TimeSpan.Zero ? -value : value;

    /// <summary>
    /// 把扫描阶段的数据质量统计累计到长期状态。
    /// </summary>
    /// <param name="state">需要更新的监控状态。</param>
    /// <param name="scan">本轮扫描结果。</param>
    private static void ApplyScanDiagnostics(MonitorState state, RolloutScanResult scan)
    {
        state.MalformedRolloutLines += scan.MalformedLineCount;
        state.RotatedRolloutFiles += scan.RotatedFileCount;
        state.PrunedRolloutCursors += scan.PrunedCursorCount;
    }

    /// <summary>
    /// 将一次增量日志扫描累积到当前百分比区间，并验证价格版本没有跨升级混用。
    /// </summary>
    /// <param name="state">需要更新的监控状态。</param>
    /// <param name="scan">本轮扫描结果。</param>
    private static void MergeScan(MonitorState state, RolloutScanResult scan)
    {
        var scanContainsResponses = scan.ModelResponseCount > 0 || scan.UnpricedModelResponses > 0;
        if (scanContainsResponses && !string.Equals(scan.PricingVersion, PublicApiPricing.PricingVersion, StringComparison.Ordinal))
        {
            state.PendingUnpricedModelResponses += scan.ModelResponseCount + scan.UnpricedModelResponses;
            state.UnpricedModelResponses += scan.ModelResponseCount + scan.UnpricedModelResponses;
            state.PendingMalformedRolloutLines += scan.MalformedLineCount;
            state.PendingRotatedRolloutFiles += scan.RotatedFileCount;
            return;
        }

        if (scanContainsResponses &&
            state.PendingModelResponseCount + state.PendingUnpricedModelResponses > 0 &&
            !string.Equals(state.PendingPricingVersion, scan.PricingVersion, StringComparison.Ordinal))
        {
            state.UnattributedPercentChanges++;
            ClearPending(state);
        }

        if (scanContainsResponses)
        {
            state.PendingPricingVersion = scan.PricingVersion;
        }

        state.PendingUsage = state.PendingUsage.Add(scan.Usage);
        state.PendingApiEquivalentUsd += scan.ApiEquivalentUsd;
        state.PendingOfficialLongContextApiEquivalentUsd += scan.OfficialLongContextApiEquivalentUsd;
        state.PendingModelResponseCount += scan.ModelResponseCount;
        state.PendingUnpricedModelResponses += scan.UnpricedModelResponses;
        state.PendingMalformedRolloutLines += scan.MalformedLineCount;
        state.PendingRotatedRolloutFiles += scan.RotatedFileCount;
        state.UnpricedModelResponses += scan.UnpricedModelResponses;
        foreach (var model in scan.Models)
        {
            state.PendingModels.Add(model);
        }

        foreach (var tier in scan.ServiceTiers)
        {
            state.PendingServiceTiers.Add(tier);
        }

        foreach (var multiplier in scan.CreditMultipliers)
        {
            state.PendingCreditMultipliers.Add(multiplier);
        }

        foreach (var model in scan.UnpricedModels)
        {
            state.PendingModels.Add($"{model}(未定价)");
        }
    }

    /// <summary>
    /// 根据当前参考百分比、累计金额和数据质量决定等待、放弃或生成样本。
    /// </summary>
    /// <param name="state">当前监控状态。</param>
    /// <param name="snapshot">当前额度快照。</param>
    /// <param name="minimumPercentDelta">形成有效样本所需的最小百分比变化。</param>
    /// <param name="statusPrefix">需要放在结果状态前的窗口说明。</param>
    /// <returns>本轮估算结果。</returns>
    private static EstimationUpdate TryCreateSample(
        MonitorState state,
        RateLimitSnapshot snapshot,
        decimal minimumPercentDelta,
        string statusPrefix)
    {
        var deltaPercent = snapshot.UsedPercent - state.ReferenceUsedPercent!.Value;
        if (deltaPercent < minimumPercentDelta)
        {
            return new(false, null, $"{statusPrefix}额度变化 {deltaPercent:F3}% 尚未达到采样阈值。");
        }

        if (state.PendingMalformedRolloutLines > 0 || state.PendingRotatedRolloutFiles > 0)
        {
            state.UnattributedPercentChanges++;
            AdvanceReferenceAndClearPending(state, snapshot.UsedPercent);
            return new(false, null, $"{statusPrefix}区间包含畸形日志或文件轮转，已按数据完整性要求放弃样本。");
        }

        if (state.PendingUnpricedModelResponses > 0)
        {
            var unpricedModels = string.Join(", ", state.PendingModels.OrderBy(model => model, StringComparer.OrdinalIgnoreCase));
            state.UnattributedPercentChanges++;
            AdvanceReferenceAndClearPending(state, snapshot.UsedPercent);
            return new(false, null, $"{statusPrefix}区间包含无法按当前 Standard API 等价口径计算的响应（{unpricedModels}）；未形成样本。");
        }

        if (state.PendingModelResponseCount == 0 ||
            state.PendingApiEquivalentUsd <= 0 ||
            state.PendingOfficialLongContextApiEquivalentUsd <= 0)
        {
            state.UnattributedPercentChanges++;
            AdvanceReferenceAndClearPending(state, snapshot.UsedPercent);
            return new(false, null, $"{statusPrefix}额度发生变化，但没有可归因的本机模型响应；未形成样本。");
        }

        var estimatedWeeklyUsd = state.PendingApiEquivalentUsd * 100m / deltaPercent;
        var officialLongContextEstimatedWeeklyUsd =
            state.PendingOfficialLongContextApiEquivalentUsd * 100m / deltaPercent;
        var sample = new QuotaSample(
            snapshot.SampledAt,
            snapshot.LimitId,
            snapshot.UsedPercent,
            deltaPercent,
            state.PendingApiEquivalentUsd,
            estimatedWeeklyUsd,
            state.PendingUsage,
            state.PendingModelResponseCount,
            string.Join(", ", state.PendingModels.OrderBy(model => model, StringComparer.OrdinalIgnoreCase)))
        {
            AmountDefinition = PublicApiPricing.AmountDefinition,
            OfficialLongContextIntervalApiEquivalentUsd = state.PendingOfficialLongContextApiEquivalentUsd,
            OfficialLongContextEstimatedWeeklyQuotaUsd = officialLongContextEstimatedWeeklyUsd,
            OfficialLongContextAmountDefinition = PublicApiPricing.OfficialLongContextAmountDefinition,
            PricingVersion = PublicApiPricing.PricingVersion,
            ServiceTiers = string.Join(", ", state.PendingServiceTiers.OrderBy(tier => tier, StringComparer.OrdinalIgnoreCase)),
            CreditMultipliers = string.Join(", ", state.PendingCreditMultipliers.OrderBy(multiplier => multiplier, StringComparer.OrdinalIgnoreCase))
        };

        state.Samples.Add(sample);
        AdvanceReferenceAndClearPending(state, snapshot.UsedPercent);
        return new(
            true,
            sample,
            $"{statusPrefix}新增样本：无长上下文加价 ${estimatedWeeklyUsd:F2}；官方 >272K 加价 ${officialLongContextEstimatedWeeklyUsd:F2}。");
    }

    /// <summary>
    /// 用服务端快照建立新的百分比区间基线并清空旧窗口残留。
    /// </summary>
    /// <param name="state">需要更新的监控状态。</param>
    /// <param name="snapshot">新的额度快照。</param>
    private static void SetBaseline(MonitorState state, RateLimitSnapshot snapshot)
    {
        ClearPending(state);
        state.ReferenceUsedPercent = snapshot.UsedPercent;
        UpdateObservedWindow(state, snapshot);
    }

    /// <summary>
    /// 对未确认重置的百分比下降重建区间基线，同时保留旧窗口的重置承诺用于后续确认。
    /// </summary>
    /// <param name="state">需要更新的监控状态。</param>
    /// <param name="snapshot">服务端修正后的额度快照。</param>
    private static void SetCorrectedBaseline(MonitorState state, RateLimitSnapshot snapshot)
    {
        ClearPending(state);
        state.ReferenceUsedPercent = snapshot.UsedPercent;
        UpdateObservedUsage(state, snapshot);
    }

    /// <summary>
    /// 为已确认的新窗口建立零百分比参考点，以便使用窗口起点后的日志恢复首个样本。
    /// </summary>
    /// <param name="state">需要更新的监控状态。</param>
    /// <param name="snapshot">新窗口额度快照。</param>
    private static void PrepareResetWindow(MonitorState state, RateLimitSnapshot snapshot)
    {
        ClearPending(state);
        state.ReferenceUsedPercent = 0;
        UpdateObservedWindow(state, snapshot);
    }

    /// <summary>
    /// 更新最后观察到的百分比、重置时间和额度桶。
    /// </summary>
    /// <param name="state">需要更新的监控状态。</param>
    /// <param name="snapshot">最新额度快照。</param>
    private static void UpdateObservedWindow(MonitorState state, RateLimitSnapshot snapshot)
    {
        UpdateObservedUsage(state, snapshot);
        state.LastResetAt = snapshot.ResetsAt;
    }

    /// <summary>
    /// 更新最近百分比与额度桶，但不接受尚未确认的 resetsAt 漂移。
    /// </summary>
    /// <param name="state">需要更新的监控状态。</param>
    /// <param name="snapshot">最新额度快照。</param>
    private static void UpdateObservedUsage(MonitorState state, RateLimitSnapshot snapshot)
    {
        state.LastObservedUsedPercent = snapshot.UsedPercent;
        state.LastLimitId = snapshot.LimitId;
    }

    /// <summary>
    /// 在完成或放弃当前区间后推进参考百分比并清空累积量。
    /// </summary>
    /// <param name="state">需要更新的监控状态。</param>
    /// <param name="usedPercent">新的参考已用百分比。</param>
    private static void AdvanceReferenceAndClearPending(MonitorState state, decimal usedPercent)
    {
        state.ReferenceUsedPercent = usedPercent;
        ClearPending(state);
    }

    /// <summary>
    /// 清空当前百分比区间的 token、金额、响应数、口径元数据和完整性状态。
    /// </summary>
    /// <param name="state">需要清空 pending 字段的监控状态。</param>
    private static void ClearPending(MonitorState state)
    {
        state.PendingApiEquivalentUsd = 0;
        state.PendingOfficialLongContextApiEquivalentUsd = 0;
        state.PendingUsage = TokenUsage.Zero;
        state.PendingModelResponseCount = 0;
        state.PendingModels.Clear();
        state.PendingServiceTiers.Clear();
        state.PendingCreditMultipliers.Clear();
        state.PendingPricingVersion = string.Empty;
        state.PendingUnpricedModelResponses = 0;
        state.PendingMalformedRolloutLines = 0;
        state.PendingRotatedRolloutFiles = 0;
    }
}
