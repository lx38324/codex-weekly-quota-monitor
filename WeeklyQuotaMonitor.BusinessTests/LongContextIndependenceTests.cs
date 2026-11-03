using System.Text.Json;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor.BusinessTests;

/// <summary>验证可选长上下文缺价不阻断基础采样，覆盖实时、重启、历史回放、重算与界面未知值。</summary>
internal static class LongContextIndependenceTests
{
    /// <summary>使用与本机相同的空长上下文档位复现大输入场景，不补价、不修改正式文件。</summary>
    public static void Run()
    {
        var profiles = PublicApiPricing.GetBuiltInProfiles().Select(p => p.Model is "gpt-6-astra" or "gpt-6.1-sol"
            ? p with { LongContext = null, ShortContext = p.ShortContext with { CacheWritePerMillion = null } } : p).ToArray();
        var catalog = PublicApiPricing.CreateCatalog(profiles);
        var usage = new TokenUsage(300000, 200000, 0, 1000, 100);
        foreach (var (model, expected) in new[] { ("gpt-6-astra", 1.25m), ("gpt-6.1-sol", 0.23m) })
        foreach (var tier in new[] { "standard", "fast" })
        {
            var result = catalog.Calculate(model, tier, usage);
            Check(result.Success && result.CostUsd == expected * (tier == "fast" ? 2.5m : 1m) &&
                !result.OfficialLongContextAvailable && result.OfficialLongContextIssue.Length > 0,
                "长上下文缺价仍阻断基础计费，或用基础价格冒充未知的对比价格。");
        }
        var shortResult = catalog.Calculate("gpt-6-astra", "standard", usage with { InputTokens = 272000 });
        Check(shortResult.Success && shortResult.OfficialLongContextAvailable, "272K 边界不应要求长档价格。");
        Check(!catalog.Calculate("gpt-6-astra", "unknown", usage).Success &&
            !catalog.Calculate("gpt-6-astra", "standard", usage with { CacheWriteInputTokens = 1000 }).Success,
            "基础层级或基础缓存写缺失被错误地当成可选问题忽略。");
        var writeProfiles = PublicApiPricing.GetBuiltInProfiles().Select(p => p.Model == "gpt-6-astra"
            ? p with { LongContext = p.LongContext! with { CacheWritePerMillion = null } } : p).ToArray();
        var writeResult = PublicApiPricing.CreateCatalog(writeProfiles).Calculate("gpt-6-astra", "standard", usage with { CacheWriteInputTokens = 1000 });
        Check(writeResult.Success && !writeResult.OfficialLongContextAvailable && writeResult.CostUsd > 0,
            "仅对比缓存写缺价时丢掉基础金额。");

        var root = Path.Combine(Path.GetTempPath(), "QuotaMonitor.LongContext." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var time = DateTimeOffset.UtcNow.AddMinutes(-2);
            var snapshot = new RateLimitSnapshot(time, "codex", null, 16, 10080, time.AddDays(6));
            var state = new MonitorState(); var reader = new RolloutLogReader(catalog);
            reader.PrimeExistingFiles(root, state, 24);
            QuotaEstimator.Process(state, snapshot, RolloutScanResult.Empty, 0.1m, catalog.PricingVersion);
            var checkpoints = new List<AuthoritativeRateLimitCheckpoint>();
            for (var index = 0; index < 3; index++) checkpoints.Add(new(time.AddSeconds(index * 20), "codex", 16 + index, 10080, snapshot.ResetsAt));
            WriteResponse(Path.Combine(root, "astra.jsonl"), time.AddSeconds(10), "gpt-6-astra", usage);
            var scan = reader.ScanNew(root, state);
            Check(scan.UnpricedModelResponses == 0 && !scan.OfficialLongContextAvailable && scan.ApiEquivalentUsd == 1.25m,
                "扫描器仍将可选缺价计入基础未定价响应。");
            var first = QuotaEstimator.Process(state, snapshot with { UsedPercent = 17, SampledAt = time.AddSeconds(20) }, scan, 0.1m, catalog.PricingVersion);
            Check(first.SampleCreated && first.Sample!.EstimatedWeeklyQuotaUsd == 125m && !first.Sample.OfficialLongContextAvailable,
                "剩余84→83%的基础样本没有生成。");
            var stateFile = Path.Combine(root, "state.json");
            JsonStorage.SaveStateAsync(stateFile, state.CopyForPersistence()).GetAwaiter().GetResult();
            state = JsonStorage.LoadState(stateFile, catalog.PricingVersion);
            Check(!state.Samples.Single().OfficialLongContextAvailable, "重启丢失对比不可用标记。");
            WriteResponse(Path.Combine(root, "sol.jsonl"), time.AddSeconds(30), "gpt-6.1-sol", usage);
            var second = QuotaEstimator.Process(state, snapshot with { UsedPercent = 18, SampledAt = time.AddSeconds(40) },
                reader.ScanNew(root, state), 0.1m, catalog.PricingVersion);
            Check(second.SampleCreated && second.Sample!.EstimatedWeeklyQuotaUsd == 23m && state.Samples.Count == 2,
                "剩余83→82%的基础样本没有生成，或重启重复消费日志。");
            var end = snapshot with { UsedPercent = 18, SampledAt = time.AddSeconds(40) };
            var facts = reader.ReadHistoricalFacts([root], QuotaEstimator.GetEffectiveWindowStart(end), end.SampledAt, 10080);
            var replay = HistoricalReplayCalculator.Build(end, facts, checkpoints);
            Check(replay.UnpricedResponseCount == 0 && replay.Samples.Count == 2 &&
                replay.Samples.All(s => !s.OfficialLongContextAvailable && SampleRepricer.HasCompleteUsage(s)),
                "历史回放仍因可选缺价丢掉基础区间。");
            var repriced = SampleRepricer.Apply(state, catalog);
            Check(repriced.Errors.Count == 0 && state.Samples.Count == 2 && state.Samples.All(s => s.IntervalApiEquivalentUsd > 0 &&
                !s.OfficialLongContextAvailable && s.OfficialLongContextIssue.Length > 0), "离线重算再次丢点或丢失原因。");
            var tamperedNumbers = state.Samples.Select(s => s with { OfficialLongContextEstimatedWeeklyQuotaUsd = 999m }).ToArray();
            Check(RegressionCalculator.BuildCurve(tamperedNumbers, new(), catalog.PricingVersion).Count == 2 &&
                RegressionCalculator.BuildOfficialLongContextCurve(tamperedNumbers, new(), catalog.PricingVersion).Count == 0,
                "对比不可用样本进入了对比回归，或影响基础回归。");
            VerifyMixedPending(catalog, usage, snapshot);
            VerifyUi(state.Samples, catalog);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>跨多次扫描累计时，任一次对比缺价即排除整个区间对比，不能展示其余响应的部分金额。</summary>
    private static void VerifyMixedPending(PricingCatalog catalog, TokenUsage usage, RateLimitSnapshot snapshot)
    {
        var state = new MonitorState();
        QuotaEstimator.Process(state, snapshot, RolloutScanResult.Empty, 1, catalog.PricingVersion);
        var knownUsage = usage with { InputTokens = 272000 };
        var known = catalog.Calculate("gpt-6-astra", "standard", knownUsage);
        var knownScan = new RolloutScanResult(knownUsage, known.CostUsd, 1, ["gpt-6-astra"], 0, [])
        { PricingVersion = catalog.PricingVersion, OfficialLongContextApiEquivalentUsd = known.OfficialLongContextCostUsd };
        QuotaEstimator.Process(state, snapshot with { UsedPercent = 16.5m }, knownScan, 1, catalog.PricingVersion);
        var missing = catalog.Calculate("gpt-6-astra", "standard", usage);
        var missingScan = new RolloutScanResult(usage, missing.CostUsd, 1, ["gpt-6-astra"], 0, [])
        { PricingVersion = catalog.PricingVersion, OfficialLongContextAvailable = false, OfficialLongContextIssues = [missing.OfficialLongContextIssue] };
        var update = QuotaEstimator.Process(state, snapshot with { UsedPercent = 17 }, missingScan, 1, catalog.PricingVersion);
        Check(update.SampleCreated && !update.Sample!.OfficialLongContextAvailable &&
            update.Sample.IntervalApiEquivalentUsd == known.CostUsd + missing.CostUsd &&
            update.Sample.OfficialLongContextIntervalApiEquivalentUsd == 0 && state.PendingOfficialLongContextAvailable &&
            state.PendingOfficialLongContextIssues.Count == 0, "混合区间产生部分对比金额，或缺失标记污染下一区间。");
    }

    /// <summary>关闭及开启可选对比时基础点均完整；缺价对比在真实表格显示未知值，开启时明确提示。</summary>
    private static void VerifyUi(IReadOnlyList<QuotaSample> samples, PricingCatalog catalog)
    {
        foreach (var enabled in new[] { false, true })
        {
            var settings = new AppSettings { EnableOfficialLongContextEstimate = enabled, ModelPrices = catalog.ExportProfiles().ToList() };
            using var form = new ChartForm(settings, _ => { });
            var view = new MonitorViewSnapshot(DateTimeOffset.Now, "双口径独立验证", null, null, samples.Count, 0, 0, samples, [])
                { PricingVersion = catalog.PricingVersion };
            form.UpdateView(view, new()); form.ShowDashboardTab(); Application.DoEvents();
            var grid = Find<DataGridView>(form).Single(g => g.Columns.Cast<DataGridViewColumn>().Any(c => c.DataPropertyName == "UsedPercent"));
            Check(grid.Rows.Count == 2, "切换对比开关丢掉基础点。");
            var column = grid.Columns.Cast<DataGridViewColumn>().Single(c => c.DataPropertyName == "OfficialLongContextEstimatedWeeklyQuotaUsd");
            Check(column.Visible == enabled && grid.Rows[0].Cells[column.Index].Value is null,
                "缺失对比显示为零金额或关闭对比未隐藏列。");
            if (enabled) Check(Find<Label>(form).Any(label => label.Text.Contains(UiText.Format("OfficialLongContextUnavailable", 2))),
                "开启对比时没有提示价格缺失。");
        }
    }

    /// <summary>写入无正文的最小完整 JSONL 响应，使用正式扫描器验证关联和用量累计。</summary>
    private static void WriteResponse(string path, DateTimeOffset timestamp, string model, TokenUsage usage)
    {
        object[] lines = [new { timestamp, type = "turn_context", payload = new { model, service_tier = "default" } },
            new { timestamp, type = "response_item", payload = new { type = "message", role = "assistant" } },
            new { timestamp, type = "event_msg", payload = new { type = "token_count", info = new { last_token_usage = new
                { input_tokens = usage.InputTokens, cached_input_tokens = usage.CachedInputTokens,
                  output_tokens = usage.OutputTokens, reasoning_output_tokens = usage.ReasoningOutputTokens } } } }];
        File.WriteAllLines(path, lines.Select(line => JsonSerializer.Serialize(line)));
    }

    /// <summary>只读正式目录，在内存回放被阻断的17/18%区间；不保存状态，不访问额度接口，不输出对话正文。</summary>
    public static void Audit(string directory)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(directory, "settings.json")))!;
        var catalog = PublicApiPricing.CreateCatalog(settings.ModelPrices);
        var state = JsonStorage.LoadState(Path.Combine(directory, "state.json"), catalog.PricingVersion);
        var last = state.AuthoritativeRateLimitCheckpoints.Where(c => c.UsedPercent == 18).OrderBy(c => c.Timestamp).Last();
        var snapshot = new RateLimitSnapshot(last.Timestamp, last.LimitId, null, last.UsedPercent, last.WindowDurationMinutes, last.ResetsAt);
        var reader = new RolloutLogReader(catalog);
        var facts = reader.ReadHistoricalFacts(RolloutLogReader.DiscoverHistoricalSessionRoots(settings.SessionRoot),
            QuotaEstimator.GetEffectiveWindowStart(snapshot), snapshot.SampledAt, snapshot.WindowDurationMinutes);
        var replay = HistoricalReplayCalculator.Build(snapshot, facts, state.AuthoritativeRateLimitCheckpoints);
        var recovered = replay.Samples.Where(s => s.UsedPercent is 17 or 18).ToArray();
        Check(recovered.Length == 2 && recovered.All(s => s.IntervalApiEquivalentUsd > 0 && SampleRepricer.HasCompleteUsage(s)),
            "真实17/18%区间未全部补回，不得宣称回算成功。");
        Console.WriteLine(JsonSerializer.Serialize(new { replay.UnpricedResponseCount, replay.UnattributedIntervalCount,
            Recovered = recovered.Select(s => new { s.UsedPercent, Remaining = 100 - s.UsedPercent,
                s.IntervalApiEquivalentUsd, s.EstimatedWeeklyQuotaUsd, s.ModelResponseCount, s.OfficialLongContextAvailable }) }));
    }

    /// <summary>遍历业务控件检查实际显示结果。</summary>
    private static IEnumerable<T> Find<T>(Control root) where T : Control => root.Controls.Cast<Control>().SelectMany(c =>
        (c is T match ? new[] { match } : Enumerable.Empty<T>()).Concat(Find<T>(c)));

    /// <summary>验收失败时保留业务原因。</summary>
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
