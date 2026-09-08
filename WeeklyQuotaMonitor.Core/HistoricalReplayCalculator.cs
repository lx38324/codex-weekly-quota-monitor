using System.Globalization;

namespace WeeklyQuotaMonitor.Core;

/// <summary>
/// 把 rollout 中重复且可能冲突的周额度快照归一化为单调时间线，并按区间重放当前价格口径样本。
/// </summary>
public static class HistoricalReplayCalculator
{
    public const int CurrentReplayVersion = 8;
    public const string HistoricalSampleSource = "historical-replay";
    private static readonly TimeSpan ResetClusterTolerance = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 判断持久化状态是否需要按当前重放算法和价格版本重新构建历史样本。
    /// </summary>
    /// <param name="state">当前监控状态。</param>
    /// <returns>重放算法或价格版本未完成时返回 true。</returns>
    public static bool IsReplayRequired(MonitorState state)
        => IsReplayRequired(state, PublicApiPricing.PricingVersion);

    /// <summary>
    /// 判断持久化状态是否需要按当前重放算法和指定价格配置重新构建历史样本。
    /// </summary>
    /// <param name="state">当前监控状态。</param>
    /// <param name="pricingVersion">当前生效价格配置的稳定版本。</param>
    /// <returns>重放算法或价格版本未完成时返回 true。</returns>
    public static bool IsReplayRequired(MonitorState state, string pricingVersion)
    {
        if (state.HistoricalReplayVersion > CurrentReplayVersion)
        {
            throw new InvalidDataException(
                $"state.json 的历史重放版本 {state.HistoricalReplayVersion} 高于当前支持版本 {CurrentReplayVersion}。");
        }

        return state.HistoricalReplayVersion != CurrentReplayVersion ||
               !string.Equals(
                   state.HistoricalReplayPricingVersion,
                   pricingVersion,
                   StringComparison.Ordinal);
    }

    /// <summary>
    /// 持久化 App Server 权威快照的首次观察时间，并清理不属于当前额度桶和窗口的旧检查点。
    /// </summary>
    /// <param name="state">需要原地更新的监控状态。</param>
    /// <param name="snapshot">当前 App Server 周额度快照。</param>
    /// <returns>检查点集合发生新增或清理时返回 true。</returns>
    public static bool RecordAuthoritativeCheckpoint(MonitorState state, RateLimitSnapshot snapshot)
    {
        var windowStart = QuotaEstimator.GetEffectiveWindowStart(snapshot);
        var removed = state.AuthoritativeRateLimitCheckpoints.RemoveAll(checkpoint =>
            !string.Equals(checkpoint.LimitId, snapshot.LimitId, StringComparison.Ordinal) ||
            checkpoint.WindowDurationMinutes != snapshot.WindowDurationMinutes ||
            Absolute(checkpoint.ResetsAt - snapshot.ResetsAt) > ResetClusterTolerance ||
            checkpoint.Timestamp < windowStart ||
            checkpoint.Timestamp > snapshot.SampledAt ||
            checkpoint.UsedPercent > snapshot.UsedPercent);
        if (state.AuthoritativeRateLimitCheckpoints.Any(checkpoint =>
                checkpoint.UsedPercent == snapshot.UsedPercent &&
                Absolute(checkpoint.ResetsAt - snapshot.ResetsAt) <= ResetClusterTolerance))
        {
            return removed > 0;
        }

        state.AuthoritativeRateLimitCheckpoints.Add(new(
            snapshot.SampledAt,
            snapshot.LimitId,
            snapshot.UsedPercent,
            snapshot.WindowDurationMinutes,
            snapshot.ResetsAt));
        state.AuthoritativeRateLimitCheckpoints.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        return true;
    }

    /// <summary>
    /// 使用当前 App Server 快照筛选同一周窗口的稳定 resetsAt 聚类，并重放各百分比变化区间。
    /// </summary>
    /// <param name="snapshot">当前权威周额度快照。</param>
    /// <param name="facts">从本机 rollout 提取的历史响应和额度候选。</param>
    /// <returns>可合入状态的当前价格版本历史样本与诊断。</returns>
    public static HistoricalReplayResult Build(
        RateLimitSnapshot snapshot,
        HistoricalRolloutFacts facts,
        IReadOnlyList<AuthoritativeRateLimitCheckpoint> authoritativeCheckpoints)
    {
        var expectedWindowStart = QuotaEstimator.GetEffectiveWindowStart(snapshot);
        if (facts.WindowStart != expectedWindowStart || facts.WindowEnd != snapshot.SampledAt)
        {
            throw new InvalidDataException("历史事实窗口与当前 App Server 周额度窗口不一致。");
        }

        var authoritativeCandidates = authoritativeCheckpoints
            .Where(candidate => string.Equals(candidate.LimitId, snapshot.LimitId, StringComparison.Ordinal))
            .Select(candidate => new HistoricalRateLimitCheckpoint(
                candidate.Timestamp,
                candidate.UsedPercent,
                candidate.WindowDurationMinutes,
                candidate.ResetsAt));
        var rawCandidates = facts.RateLimitCheckpoints
            .Concat(authoritativeCandidates)
            .Where(candidate => candidate.WindowDurationMinutes == snapshot.WindowDurationMinutes)
            .Where(candidate => candidate.Timestamp >= facts.WindowStart && candidate.Timestamp <= facts.WindowEnd)
            .OrderBy(candidate => candidate.Timestamp)
            .ThenByDescending(candidate => candidate.UsedPercent)
            .ToArray();
        var clusteredCandidates = rawCandidates
            .Where(candidate => Absolute(candidate.ResetsAt - snapshot.ResetsAt) <= ResetClusterTolerance)
            .Where(candidate => candidate.UsedPercent >= 0 && candidate.UsedPercent <= snapshot.UsedPercent)
            .ToArray();
        var canonical = BuildCanonicalTimeline(snapshot, clusteredCandidates);
        var samples = BuildSamples(
            snapshot,
            facts,
            canonical,
            out var unattributedIntervals,
            out var awaitingLogIntervals,
            out var unattributedUsedPercents,
            out var unresolvedSourceFiles);
        var acceptedRawCount = canonical.Count(checkpoint => checkpoint.Timestamp != snapshot.SampledAt ||
                                                             checkpoint.UsedPercent != snapshot.UsedPercent ||
                                                             clusteredCandidates.Any(candidate => candidate == checkpoint));

        return new(
            facts.WindowStart,
            facts.WindowEnd,
            snapshot.LimitId,
            samples,
            rawCandidates.Length,
            canonical.Count,
            Math.Max(0, rawCandidates.Length - acceptedRawCount),
            facts.Responses.Count(response => response.PricingSucceeded),
            facts.Responses.Count(response => !response.PricingSucceeded),
            unattributedIntervals,
            awaitingLogIntervals,
            unattributedUsedPercents,
            unresolvedSourceFiles,
            facts.MalformedLineCount)
        {
            PricingVersion = facts.PricingVersion
        };
    }

    /// <summary>
    /// 合入成功重建区间；响应覆盖退化时保留旧记录并记录拒绝数，不能把局部扫描当作完整重算。
    /// </summary>
    /// <param name="state">需要更新并持久化的监控状态。</param>
    /// <param name="result">已完成的历史重放结果。</param>
    public static void ApplyToState(MonitorState state, HistoricalReplayResult result)
    {
        state.HistoricalReplayCoverageRejectedIntervals = MergeCoveredSamples(state, result);
        state.Samples.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        state.HistoricalReplayVersion = CurrentReplayVersion;
        state.HistoricalReplayPricingVersion = result.PricingVersion;
        state.HistoricalReplayCompletedAt = DateTimeOffset.Now;
        state.HistoricalReplayCandidateCheckpoints = result.CandidateCheckpointCount;
        state.HistoricalReplayAcceptedCheckpoints = result.AcceptedCheckpointCount;
        state.HistoricalReplayRejectedCheckpoints = result.RejectedCheckpointCount;
        state.HistoricalReplayPricedResponses = result.PricedResponseCount;
        state.HistoricalReplayUnpricedResponses = result.UnpricedResponseCount;
        state.HistoricalReplayUnattributedIntervals = result.UnattributedIntervalCount;
        state.HistoricalReplayAwaitingLogIntervals = result.AwaitingLogIntervalCount;
        state.HistoricalReplayUnattributedUsedPercents = result.UnattributedUsedPercents.ToList();
        state.HistoricalReplayMalformedLines = result.MalformedLineCount;
    }

    /// <summary>
    /// 按相交百分比区间一起合并，支持一段拆多段、多段合一；保留未覆盖点。
    /// 同一或更宽时间范围下响应数、输入或输出减少意味着覆盖退化；金额下降本身不是拒绝依据。
    /// 更早的首次观察边界允许排除迟到响应。返回拒绝替换的区间数，供当前周和归档共同报告。
    /// </summary>
    internal static int MergeCoveredSamples(MonitorState state, HistoricalReplayResult result)
    {
        var pending = result.Samples.ToList();
        var existing = state.Samples.Where(sample =>
            PublicApiPricing.IsSampleForVersion(sample, result.PricingVersion) &&
            sample.LimitId == result.LimitId && sample.Timestamp >= result.WindowStart &&
            sample.Timestamp <= result.WindowEnd).ToArray();
        var rejected = 0;
        while (pending.Count > 0)
        {
            var replacements = new List<QuotaSample> { pending[0] };
            pending.RemoveAt(0);
            var start = replacements[0].UsedPercent - replacements[0].DeltaPercent;
            var end = replacements[0].UsedPercent;
            QuotaSample[] overlaps;
            bool expanded;
            do
            {
                var previousStart = start;
                var previousEnd = end;
                overlaps = existing.Where(sample => sample.UsedPercent > start &&
                    sample.UsedPercent - sample.DeltaPercent < end).ToArray();
                if (overlaps.Length > 0)
                {
                    start = Math.Min(start, overlaps.Min(sample => sample.UsedPercent - sample.DeltaPercent));
                    end = Math.Max(end, overlaps.Max(sample => sample.UsedPercent));
                }
                var connected = pending.Where(sample => sample.UsedPercent > start &&
                    sample.UsedPercent - sample.DeltaPercent < end).ToArray();
                foreach (var sample in connected)
                {
                    replacements.Add(sample);
                    pending.Remove(sample);
                    start = Math.Min(start, sample.UsedPercent - sample.DeltaPercent);
                    end = Math.Max(end, sample.UsedPercent);
                }
                expanded = previousStart != start || previousEnd != end || connected.Length > 0;
            } while (expanded);

            // 历史上同一百分比区间可能保留多份观察，比较最完整一份，不能把重复记录相加。
            var prior = overlaps.GroupBy(sample => (sample.UsedPercent, sample.DeltaPercent))
                .Select(group => group.OrderByDescending(sample => sample.ModelResponseCount)
                    .ThenByDescending(sample => sample.Usage.InputTokens).First()).ToArray();
            var samePercentRange = prior.Length > 0 &&
                replacements.Min(sample => sample.UsedPercent - sample.DeltaPercent) == start &&
                replacements.Max(sample => sample.UsedPercent) == end &&
                replacements.Sum(sample => sample.DeltaPercent) == prior.Sum(sample => sample.DeltaPercent);
            var comparableTime = prior.Length > 0 &&
                replacements.Max(sample => sample.Timestamp) >= prior.Max(sample => sample.Timestamp);
            var missingDetails = false;
            if (prior.All(SampleRepricer.HasCompleteUsage) && replacements.All(SampleRepricer.HasCompleteUsage))
            {
                var available = replacements.SelectMany(sample => sample.PricingUsages)
                    .GroupBy(item => item.Usage).ToDictionary(group => group.Key, group => group.Count());
                missingDetails = prior.SelectMany(sample => sample.PricingUsages).GroupBy(item => item.Usage)
                    .Any(group => !available.TryGetValue(group.Key, out var count) || count < group.Count());
            }
            if (samePercentRange && comparableTime &&
                (missingDetails ||
                 replacements.Sum(sample => sample.ModelResponseCount) < prior.Sum(sample => sample.ModelResponseCount) ||
                 replacements.Sum(sample => sample.Usage.InputTokens) < prior.Sum(sample => sample.Usage.InputTokens) ||
                 replacements.Sum(sample => sample.Usage.OutputTokens) < prior.Sum(sample => sample.Usage.OutputTokens)))
            {
                rejected += replacements.Count;
                continue;
            }

            // 新结果不能只覆盖旧聚合区间的一部分，否则既删旧区间又丢掉未重建的百分比。
            if (prior.Any(sample => !replacements.Any(candidate =>
                    candidate.UsedPercent >= sample.UsedPercent &&
                    candidate.UsedPercent - candidate.DeltaPercent <= sample.UsedPercent - sample.DeltaPercent)) &&
                !samePercentRange)
            {
                rejected += replacements.Count;
                continue;
            }
            foreach (var old in overlaps) state.Samples.Remove(old);
            state.Samples.AddRange(replacements);
        }
        return rejected;
    }

    /// <summary>
    /// 从稳定重置聚类中保留百分比单调上升的首次观察点，并在需要时追加当前权威快照。
    /// </summary>
    /// <param name="snapshot">当前权威周额度快照。</param>
    /// <param name="candidates">已通过窗口和 resetsAt 聚类筛选的候选。</param>
    /// <returns>按时间排序且百分比严格递增的时间线。</returns>
    private static List<HistoricalRateLimitCheckpoint> BuildCanonicalTimeline(
        RateLimitSnapshot snapshot,
        IReadOnlyList<HistoricalRateLimitCheckpoint> candidates)
    {
        var canonical = new List<HistoricalRateLimitCheckpoint>();
        decimal? currentPercent = null;
        foreach (var candidate in candidates)
        {
            if (currentPercent is not null && candidate.UsedPercent <= currentPercent.Value)
            {
                continue;
            }

            canonical.Add(candidate);
            currentPercent = candidate.UsedPercent;
        }

        if (currentPercent is null || currentPercent.Value < snapshot.UsedPercent)
        {
            canonical.Add(new(
                snapshot.SampledAt,
                snapshot.UsedPercent,
                snapshot.WindowDurationMinutes,
                snapshot.ResetsAt));
        }

        return canonical;
    }

    /// <summary>
    /// 按相邻可信百分比观察点切分响应事实；任一区间含未定价响应或没有本机响应时不生成样本。
    /// </summary>
    /// <param name="snapshot">提供额度桶标识的当前权威快照。</param>
    /// <param name="facts">包含全局时间排序前原始响应的历史事实。</param>
    /// <param name="timeline">百分比严格递增的可信时间线。</param>
    /// <param name="unattributedIntervals">返回无法形成可信样本的区间数。</param>
    /// <returns>当前价格版本的历史重放样本。</returns>
    private static IReadOnlyList<QuotaSample> BuildSamples(
        RateLimitSnapshot snapshot,
        HistoricalRolloutFacts facts,
        IReadOnlyList<HistoricalRateLimitCheckpoint> timeline,
        out int unattributedIntervals,
        out int awaitingLogIntervals,
        out IReadOnlyList<decimal> unattributedUsedPercents,
        out IReadOnlyList<string> unresolvedSourceFiles)
    {
        unattributedIntervals = 0;
        awaitingLogIntervals = 0;
        var unattributedPercents = new HashSet<decimal>();
        var unresolvedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (timeline.Count < 2)
        {
            unattributedUsedPercents = [];
            unresolvedSourceFiles = [];
            return [];
        }

        var responses = facts.Responses.OrderBy(response => response.Timestamp).ToArray();
        var samples = new List<QuotaSample>();
        var previousPercent = timeline[0].UsedPercent;
        var previousBoundary = previousPercent == 0 ? facts.WindowStart : timeline[0].Timestamp;
        for (var index = 1; index < timeline.Count; index++)
        {
            var checkpoint = timeline[index];
            var deltaPercent = checkpoint.UsedPercent - previousPercent;
            var intervalResponses = responses
                .Where(response => response.Timestamp > previousBoundary && response.Timestamp <= checkpoint.Timestamp)
                .ToArray();
            if (deltaPercent <= 0 ||
                intervalResponses.Length == 0 ||
                intervalResponses.Any(response => !response.PricingSucceeded))
            {
                unattributedIntervals++;
                if (intervalResponses.Length == 0)
                {
                    awaitingLogIntervals++;
                }

                unattributedPercents.Add(checkpoint.UsedPercent);
                foreach (var response in intervalResponses.Where(response => !response.PricingSucceeded))
                {
                    unresolvedFiles.Add(response.SourceFile);
                }

                previousPercent = checkpoint.UsedPercent;
                previousBoundary = checkpoint.Timestamp;
                continue;
            }

            samples.Add(BuildSample(
                snapshot,
                checkpoint,
                deltaPercent,
                intervalResponses,
                facts.PricingVersion));
            previousPercent = checkpoint.UsedPercent;
            previousBoundary = checkpoint.Timestamp;
        }

        unattributedUsedPercents = unattributedPercents.OrderBy(percent => percent).ToArray();
        unresolvedSourceFiles = unresolvedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        return samples;
    }

    /// <summary>
    /// 将一个完整可定价的历史区间聚合成与实时采样结构一致的双口径样本。
    /// </summary>
    /// <param name="snapshot">提供额度桶标识的当前权威快照。</param>
    /// <param name="checkpoint">区间结束的百分比观察点。</param>
    /// <param name="deltaPercent">本区间额度百分比增量。</param>
    /// <param name="responses">区间内全部已定价响应。</param>
    /// <param name="pricingVersion">生成响应金额的不可变价格配置版本。</param>
    /// <returns>历史重放来源的当前价格版本样本。</returns>
    private static QuotaSample BuildSample(
        RateLimitSnapshot snapshot,
        HistoricalRateLimitCheckpoint checkpoint,
        decimal deltaPercent,
        IReadOnlyList<HistoricalResponseFact> responses,
        string pricingVersion)
    {
        var usage = responses.Aggregate(TokenUsage.Zero, (total, response) => total.Add(response.Usage));
        var cost = responses.Sum(response => response.ApiEquivalentUsd);
        var officialLongContextCost = responses.Sum(response => response.OfficialLongContextApiEquivalentUsd);
        var models = responses.Select(response => response.Model)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase);
        var tiers = responses.Select(response => RolloutLogReader.FormatServiceTierEvidence(
                response.NormalizedServiceTier,
                response.ServiceTierRecoveredFromConfig))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tier => tier, StringComparer.OrdinalIgnoreCase);
        var multipliers = responses
            .Select(response => $"{response.CreditMultiplier.ToString("0.###", CultureInfo.InvariantCulture)}x")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(multiplier => multiplier, StringComparer.OrdinalIgnoreCase);
        return new(
            checkpoint.Timestamp,
            snapshot.LimitId,
            checkpoint.UsedPercent,
            deltaPercent,
            cost,
            cost * 100m / deltaPercent,
            usage,
            responses.Count,
            string.Join(", ", models))
        {
            AmountDefinition = PublicApiPricing.AmountDefinition,
            OfficialLongContextIntervalApiEquivalentUsd = officialLongContextCost,
            OfficialLongContextEstimatedWeeklyQuotaUsd = officialLongContextCost * 100m / deltaPercent,
            OfficialLongContextAmountDefinition = PublicApiPricing.OfficialLongContextAmountDefinition,
            PricingVersion = pricingVersion,
            ServiceTiers = string.Join(", ", tiers),
            CreditMultipliers = string.Join(", ", multipliers),
            SampleSource = HistoricalSampleSource,
            PricingUsages = responses.Select(response =>
                new ResponsePricingUsage(response.Model, response.ServiceTier, response.Usage)).ToArray()
        };
    }

    /// <summary>
    /// 返回时间跨度的绝对值，避免以有符号差值误判 resetsAt 聚类。
    /// </summary>
    /// <param name="value">需要取绝对值的时间跨度。</param>
    /// <returns>非负时间跨度。</returns>
    private static TimeSpan Absolute(TimeSpan value) => value < TimeSpan.Zero ? -value : value;
}
