namespace WeeklyQuotaMonitor.Core;

/// <summary>
/// 从活动与归档 rollout 中发现多个旧额度窗口，并在窗口互不重叠的证据边界内重建历史样本。
/// </summary>
public static class HistoricalArchiveReplayCalculator
{
    public const int CurrentVersion = 1;
    private static readonly TimeSpan ResetClusterTolerance = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 判断旧窗口回填是否因算法、价格、保留期或自动发现的数据根变化而需要重跑。
    /// </summary>
    /// <param name="state">当前持久化监控状态。</param>
    /// <param name="historyDays">图表配置的历史保留天数。</param>
    /// <param name="sourceRoots">本轮自动发现的活动与归档会话根目录。</param>
    /// <returns>需要重新扫描旧窗口时返回 true。</returns>
    public static bool IsReplayRequired(
        MonitorState state,
        int historyDays,
        IReadOnlyCollection<string> sourceRoots)
    {
        if (state.HistoricalArchiveReplayVersion > CurrentVersion)
        {
            throw new InvalidDataException(
                $"state.json 的旧窗口重放版本 {state.HistoricalArchiveReplayVersion} 高于当前支持版本 {CurrentVersion}。");
        }

        return state.HistoricalArchiveReplayVersion != CurrentVersion ||
               !string.Equals(
                   state.HistoricalArchiveReplayPricingVersion,
                   PublicApiPricing.PricingVersion,
                   StringComparison.Ordinal) ||
               state.HistoricalArchiveReplayHistoryDays != historyDays ||
               !string.Equals(
                   state.HistoricalArchiveReplaySourceRoots,
                   BuildSourceRootsSignature(sourceRoots),
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 在一次宽时间扫描结果上聚类旧重置承诺，并逐个调用单窗口计算器生成可审计样本。
    /// </summary>
    /// <param name="limitId">当前权威额度桶标识。</param>
    /// <param name="windowDurationMinutes">目标周窗口分钟数。</param>
    /// <param name="facts">活动与归档目录合并后的宽时间历史事实。</param>
    /// <param name="currentWindowStart">当前权威窗口起点；此时刻之后由实时重放负责。</param>
    /// <returns>所有成功形成样本的旧窗口结果和一次扫描诊断。</returns>
    public static HistoricalArchiveReplayResult Build(
        string limitId,
        int windowDurationMinutes,
        HistoricalRolloutFacts facts,
        DateTimeOffset currentWindowStart)
    {
        if (string.IsNullOrWhiteSpace(limitId))
        {
            throw new ArgumentException("旧窗口重放需要明确的额度桶标识。", nameof(limitId));
        }

        if (windowDurationMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowDurationMinutes));
        }

        if (currentWindowStart <= facts.WindowStart || currentWindowStart > facts.WindowEnd)
        {
            throw new ArgumentOutOfRangeException(nameof(currentWindowStart), "当前窗口起点必须位于宽时间历史扫描范围内。");
        }

        var candidates = facts.RateLimitCheckpoints
            .Where(checkpoint => checkpoint.WindowDurationMinutes == windowDurationMinutes)
            .Where(checkpoint => checkpoint.Timestamp >= facts.WindowStart)
            .Where(checkpoint => checkpoint.Timestamp < currentWindowStart)
            .Where(IsCheckpointInsidePromisedWindow)
            .OrderBy(checkpoint => checkpoint.ResetsAt)
            .ThenBy(checkpoint => checkpoint.Timestamp)
            .ToArray();
        var discovered = DiscoverCandidateWindows(candidates);
        var windows = new List<HistoricalReplayResult>();
        for (var index = 0; index < discovered.Count; index++)
        {
            var candidateWindow = discovered[index];
            var exclusiveEnd = index + 1 < discovered.Count
                ? discovered[index + 1].FirstObservedAt
                : currentWindowStart;
            var activeCandidates = candidateWindow.Checkpoints
                .Where(checkpoint => checkpoint.Timestamp < exclusiveEnd)
                .ToArray();
            var timeline = BuildStrictTimeline(activeCandidates);
            if (timeline.Count < 2)
            {
                continue;
            }

            var finalCheckpoint = timeline[^1];
            var windowStart = finalCheckpoint.ResetsAt.AddMinutes(-windowDurationMinutes);
            var responses = facts.Responses
                .Where(response => response.Timestamp > timeline[0].Timestamp)
                .Where(response => response.Timestamp <= finalCheckpoint.Timestamp)
                .ToArray();
            var windowFacts = new HistoricalRolloutFacts(
                windowStart,
                finalCheckpoint.Timestamp,
                responses,
                activeCandidates,
                facts.FilesScanned,
                0);
            var snapshot = new RateLimitSnapshot(
                finalCheckpoint.Timestamp,
                limitId,
                null,
                finalCheckpoint.UsedPercent,
                windowDurationMinutes,
                finalCheckpoint.ResetsAt);
            var replay = HistoricalReplayCalculator.Build(snapshot, windowFacts, []);
            if (replay.Samples.Count > 0)
            {
                windows.Add(replay);
            }
        }

        return new(
            facts.WindowStart,
            currentWindowStart,
            limitId,
            windows,
            facts.FilesScanned,
            facts.MalformedLineCount);
    }

    /// <summary>
    /// 按成功重建窗口替换其覆盖区间的当前价格样本，并持久化旧窗口扫描版本和诊断。
    /// </summary>
    /// <param name="state">需要原地更新的监控状态。</param>
    /// <param name="result">多窗口重建结果。</param>
    /// <param name="historyDays">本轮使用的历史保留天数。</param>
    /// <param name="sourceRoots">本轮实际扫描的活动与归档根目录。</param>
    public static void ApplyToState(
        MonitorState state,
        HistoricalArchiveReplayResult result,
        int historyDays,
        IReadOnlyCollection<string> sourceRoots)
    {
        foreach (var window in result.Windows)
        {
            var firstSampleAt = window.Samples.Min(sample => sample.Timestamp);
            state.Samples.RemoveAll(sample =>
                PublicApiPricing.IsCurrentSample(sample) &&
                string.Equals(sample.LimitId, result.LimitId, StringComparison.Ordinal) &&
                sample.Timestamp >= firstSampleAt &&
                sample.Timestamp <= window.WindowEnd);
            state.Samples.AddRange(window.Samples);
        }

        state.Samples.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        state.HistoricalArchiveReplayVersion = CurrentVersion;
        state.HistoricalArchiveReplayPricingVersion = PublicApiPricing.PricingVersion;
        state.HistoricalArchiveReplayCompletedAt = DateTimeOffset.Now;
        state.HistoricalArchiveReplayHistoryDays = historyDays;
        state.HistoricalArchiveReplaySourceRoots = BuildSourceRootsSignature(sourceRoots);
        state.HistoricalArchiveReplayWindowCount = result.Windows.Count;
        state.HistoricalArchiveReplaySampleCount = result.Windows.Sum(window => window.Samples.Count);
        state.HistoricalArchiveReplayFilesScanned = result.FilesScanned;
        state.HistoricalArchiveReplayUnpricedResponses = result.Windows.Sum(window => window.UnpricedResponseCount);
        state.HistoricalArchiveReplayUnattributedIntervals = result.Windows.Sum(window => window.UnattributedIntervalCount);
        state.HistoricalArchiveReplayMalformedLines = result.MalformedLineCount;
    }

    /// <summary>
    /// 把自动发现的数据根归一化为稳定签名，用于在目录布局变化后触发一次重放。
    /// </summary>
    /// <param name="sourceRoots">活动与归档根目录。</param>
    /// <returns>按路径排序、不区分来源顺序的签名字符串。</returns>
    public static string BuildSourceRootsSignature(IReadOnlyCollection<string> sourceRoots) =>
        string.Join(
            "|",
            sourceRoots
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 检查额度快照观察时间是否落在其声明窗口内，排除重置后仍被旧会话重复写出的陈旧快照。
    /// </summary>
    /// <param name="checkpoint">待检查的 rollout 额度候选。</param>
    /// <returns>候选时间位于声明窗口及一分钟协议漂移容差内时返回 true。</returns>
    private static bool IsCheckpointInsidePromisedWindow(HistoricalRateLimitCheckpoint checkpoint)
    {
        var windowStart = checkpoint.ResetsAt.AddMinutes(-checkpoint.WindowDurationMinutes);
        return checkpoint.Timestamp >= windowStart - ResetClusterTolerance &&
               checkpoint.Timestamp <= checkpoint.ResetsAt + ResetClusterTolerance;
    }

    /// <summary>
    /// 按一分钟 resetsAt 容差聚类候选，并仅保留至少出现一次百分比上升的窗口。
    /// </summary>
    /// <param name="candidates">按重置时间排序的候选检查点。</param>
    /// <returns>按首次观察时间排序的可重放候选窗口。</returns>
    private static IReadOnlyList<CandidateWindow> DiscoverCandidateWindows(
        IReadOnlyList<HistoricalRateLimitCheckpoint> candidates)
    {
        var clusters = new List<List<HistoricalRateLimitCheckpoint>>();
        foreach (var candidate in candidates)
        {
            if (clusters.Count == 0 ||
                Absolute(candidate.ResetsAt - clusters[^1][0].ResetsAt) > ResetClusterTolerance)
            {
                clusters.Add([]);
            }

            clusters[^1].Add(candidate);
        }

        var windows = new List<CandidateWindow>();
        foreach (var cluster in clusters)
        {
            var timeline = BuildStrictTimeline(cluster);
            if (timeline.Count >= 2)
            {
                windows.Add(new(cluster.ToArray(), timeline[0].Timestamp));
            }
        }

        return windows
            .OrderBy(window => window.FirstObservedAt)
            .ThenBy(window => window.Checkpoints[0].ResetsAt)
            .ToArray();
    }

    /// <summary>
    /// 对同一重置簇按时间归一化，保留百分比严格递增时的首次可信观察点。
    /// </summary>
    /// <param name="candidates">同一重置簇内的候选检查点。</param>
    /// <returns>时间和百分比均单调前进的检查点序列。</returns>
    private static IReadOnlyList<HistoricalRateLimitCheckpoint> BuildStrictTimeline(
        IReadOnlyCollection<HistoricalRateLimitCheckpoint> candidates)
    {
        var timeline = new List<HistoricalRateLimitCheckpoint>();
        decimal? lastPercent = null;
        foreach (var candidate in candidates
                     .OrderBy(checkpoint => checkpoint.Timestamp)
                     .ThenByDescending(checkpoint => checkpoint.UsedPercent))
        {
            if (lastPercent is not null && candidate.UsedPercent <= lastPercent.Value)
            {
                continue;
            }

            timeline.Add(candidate);
            lastPercent = candidate.UsedPercent;
        }

        return timeline;
    }

    /// <summary>
    /// 返回时间跨度绝对值，供重置承诺容差比较使用。
    /// </summary>
    /// <param name="value">需要取绝对值的时间跨度。</param>
    /// <returns>非负时间跨度。</returns>
    private static TimeSpan Absolute(TimeSpan value) => value < TimeSpan.Zero ? -value : value;

    /// <summary>
    /// 保存一个重置簇的原始检查点及其首次可信观察时间。
    /// </summary>
    private sealed record CandidateWindow(
        IReadOnlyList<HistoricalRateLimitCheckpoint> Checkpoints,
        DateTimeOffset FirstObservedAt);
}
