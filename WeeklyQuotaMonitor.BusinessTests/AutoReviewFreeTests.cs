using System.Text.Json;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor.BusinessTests;

internal static class AutoReviewFreeTests
{
    /// <summary>验证免费边界、混合区间离线重算、原始日志重放及缺少时间时不误免历史费用。</summary>
    public static void Run()
    {
        var cutoff = PublicApiPricing.AutoReviewFreeFrom;
        var usage = new TokenUsage(100000, 80000, 10000, 5000, 2000);
        var before = PublicApiPricing.Calculate("codex-auto-review", "standard", usage, cutoff.AddTicks(-1));
        Check(before.Success && before.CostUsd == 0.0121m, "公告前审批价格被误改");
        foreach (var tier in new[] { "standard", "priority", "unknown" })
        {
            var free = PublicApiPricing.Calculate(" CODEX-AUTO-REVIEW ", tier, usage, cutoff);
            Check(free.Success && free.CostUsd == 0 && free.OfficialLongContextCostUsd == 0 &&
                free.CreditMultiplier == 0, "公告后审批仍计费或依赖未知速度");
            if (tier == "unknown") Check(free.NormalizedServiceTier == "unknown", "免费审批猜测了速度模式");
        }
        Check(!PublicApiPricing.Calculate("codex-auto-review", "standard", usage).Success,
            "缺失时间的审批被猜测免费");
        Check(!PublicApiPricing.Calculate("codex-auto-review", "standard", new(1, 2, 0, 0, 0), cutoff).Success,
            "免费规则掩盖无效 token 分类");
        var luna = PublicApiPricing.Calculate("gpt-5.6-luna", "standard", usage, cutoff);
        var sol = PublicApiPricing.Calculate("gpt-6.1-sol", "priority", usage, cutoff);
        Check(luna.Success && luna.CostUsd == before.CostUsd && sol.CostUsd > 0,
            "普通 Luna 或 Sol 请求被误排除");
        var profiles = PublicApiPricing.GetBuiltInProfiles().Select(profile => profile.Model == "gpt-5.6-luna"
            ? profile with { ShortContext = new(100, 100, 100, 100) } : profile).ToArray();
        Check(PublicApiPricing.CreateCatalog(profiles).Calculate("codex-auto-review", "priority", usage, cutoff).CostUsd == 0,
            "自定义价格重新收取免费审批费用");

        var details = new[]
        {
            new ResponsePricingUsage("codex-auto-review", "standard", usage) { Timestamp = cutoff.AddSeconds(-1) },
            new ResponsePricingUsage("codex-auto-review", "unknown", usage) { Timestamp = cutoff },
            new ResponsePricingUsage("gpt-6.1-sol", "priority", usage) { Timestamp = cutoff.AddSeconds(1) }
        };
        var sample = new QuotaSample(cutoff.AddMinutes(1), "codex", 5, 1, 999, 99900,
            details.Aggregate(TokenUsage.Zero, (sum, item) => sum.Add(item.Usage)), 3,
            "codex-auto-review, gpt-6.1-sol") { PricingVersion = "previous", PricingUsages = details };
        var state = JsonSerializer.Deserialize<MonitorState>(JsonSerializer.Serialize(new MonitorState { Samples = [sample] }))!;
        var repriced = SampleRepricer.Apply(state, PublicApiPricing.BuiltInCatalog);
        var updated = state.Samples.Single(PublicApiPricing.IsCurrentSample);
        Check(repriced.Errors.Count == 0 && updated.IntervalApiEquivalentUsd == before.CostUsd + sol.CostUsd &&
            updated.Usage == sample.Usage && updated.ModelResponseCount == 3 && updated.PricingUsages.Count == 3,
            "跨公告区间金额错误或丢失免费响应 token 事实");
        SampleRepricer.Apply(state, PublicApiPricing.BuiltInCatalog);
        Check(state.Samples.Count == 2, "重复重算制造样本");
        var legacy = sample with { Timestamp = cutoff.AddDays(-1), ModelResponseCount = 1, Usage = usage,
            PricingUsages = [new("codex-auto-review", "standard", usage)] };
        var oldState = new MonitorState { Samples = [legacy] };
        Check(SampleRepricer.Apply(oldState, PublicApiPricing.BuiltInCatalog).Errors.Count == 0 &&
            oldState.Samples.Single(PublicApiPricing.IsCurrentSample).IntervalApiEquivalentUsd == before.CostUsd,
            "公告前无响应时间的旧样本不兼容");
        var missing = new MonitorState { Samples = [legacy with { Timestamp = cutoff.AddDays(1) }] };
        var rejected = SampleRepricer.Apply(missing, PublicApiPricing.BuiltInCatalog);
        Check(rejected.Errors.Count == 1 && missing.Samples.Count == 1,
            "公告后时间缺口未报错或丢失旧历史");
        missing.Samples.Add(missing.Samples[0] with { PricingVersion = PublicApiPricing.PricingVersion,
            PricingUsages = [new("codex-auto-review", "standard", usage) { Timestamp = cutoff.AddMinutes(1) }] });
        Check(SampleRepricer.Apply(missing, PublicApiPricing.BuiltInCatalog).Errors.Count == 0 &&
            missing.Samples.Count == 2 && missing.Samples.Single(PublicApiPricing.IsCurrentSample).IntervalApiEquivalentUsd == 0,
            "日志已补齐时间仍优先旧明细导致重复重算失败");
        VerifyLogReplay(cutoff, usage, before.CostUsd);
    }

    /// <summary>从真实 JSONL 结构验证实时扫描与历史重放使用同一逐响应时间，并保留免费请求。</summary>
    private static void VerifyLogReplay(DateTimeOffset cutoff, TokenUsage usage, decimal oldCost)
    {
        var directory = Path.Combine(Path.GetTempPath(), "quota-auto-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var lines = new List<string>();
            foreach (var time in new[] { cutoff.AddSeconds(-1), cutoff.AddSeconds(1) })
            {
                object turn = time < cutoff
                    ? new { timestamp = time.AddMilliseconds(-2), type = "turn_context",
                        payload = new { model = "codex-auto-review", service_tier = "standard" } }
                    : new { timestamp = time.AddMilliseconds(-2), type = "turn_context",
                        payload = new { model = "codex-auto-review" } };
                lines.Add(JsonSerializer.Serialize(turn));
                lines.Add(JsonSerializer.Serialize(new { timestamp = time.AddMilliseconds(-1), type = "response_item",
                    payload = new { type = "reasoning" } }));
                lines.Add(JsonSerializer.Serialize(new { timestamp = time, type = "event_msg", payload = new
                { type = "token_count", info = new { last_token_usage = new
                    { input_tokens = usage.InputTokens, cached_input_tokens = usage.CachedInputTokens,
                      cache_write_input_tokens = usage.CacheWriteInputTokens, output_tokens = usage.OutputTokens,
                      reasoning_output_tokens = usage.ReasoningOutputTokens } } } }));
            }
            File.WriteAllLines(Path.Combine(directory, "rollout-review.jsonl"), lines);
            var reader = new RolloutLogReader();
            var scan = reader.ScanNew(directory, new MonitorState { RolloutFilesPrimed = true });
            Check(scan.ApiEquivalentUsd == oldCost && scan.ModelResponseCount == 2 && scan.UnpricedModelResponses == 0 &&
                scan.Usage == usage.Add(usage) && scan.PricingUsages.All(item => item.Timestamp is not null),
                "日志扫描未分界免费计价或丢失用量事实");
            var start = cutoff.AddDays(-1);
            var end = cutoff.AddMinutes(1);
            var reset = start.AddDays(7);
            var snapshot = new RateLimitSnapshot(end, "codex", "Codex", 1, 10080, reset);
            var facts = reader.ReadHistoricalFacts([directory], start, end, 10080);
            var checkpoints = new[] { new AuthoritativeRateLimitCheckpoint(cutoff.AddMinutes(-1), "codex", 0, 10080, reset),
                new AuthoritativeRateLimitCheckpoint(end, "codex", 1, 10080, reset) };
            var replay = HistoricalReplayCalculator.Build(snapshot, facts, checkpoints);
            Check(replay.Samples.Count == 1 && replay.Samples[0].IntervalApiEquivalentUsd == oldCost &&
                replay.Samples[0].PricingUsages.All(item => item.Timestamp is not null), "历史重放未保留计价时间");
            var restored = JsonSerializer.Deserialize<MonitorState>(JsonSerializer.Serialize(new MonitorState
                { Samples = replay.Samples.ToList() }))!;
            Check(SampleRepricer.Apply(restored, PublicApiPricing.BuiltInCatalog).Errors.Count == 0 &&
                restored.Samples.Single().IntervalApiEquivalentUsd == oldCost, "重启后离线重算与日志重放不一致");
            var onlyFree = new RolloutScanResult(usage, 0, 1, ["codex-auto-review"], 0, [])
                { PricingVersion = PublicApiPricing.PricingVersion };
            var live = new MonitorState();
            QuotaEstimator.Process(live, snapshot with { UsedPercent = 0 }, RolloutScanResult.Empty, 0.1m);
            Check(!QuotaEstimator.Process(live, snapshot with { SampledAt = end.AddMinutes(1) }, onlyFree, 0.1m).SampleCreated,
                "仅免费审批制造了周额度估算点");
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>业务断言失败时保留实际失败信息，不将计价缺口包装为成功。</summary>
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
