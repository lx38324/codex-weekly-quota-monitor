using System.Text.Json;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor.BusinessTests;

internal static class StreamingSpeedTests
{
    /// <summary>覆盖逐响应用量核对、真实ID关联、缺失失败路径、增量重启及旧缓存补读，不读取账号或改正式状态。</summary>
    public static void Run()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds());
        foreach (var (scenario, status) in new[] { ("good", "available"), ("missing_usage", "unverified_usage"),
                     ("mismatch", "usage_mismatch"), ("foreign_owner", "unverified_usage"), ("missing_time", "missing_item_time"),
                     ("collapsed", "collapsed_time"), ("outside", "outside_response"), ("tool_first", "tool_first"), ("native_tool_first", "tool_first"),
                     ("hidden", "hidden_reasoning"), ("retry", "retried"), ("short", "available") })
        {
            var root = TemporaryRoot(); var path = Path.Combine(root, "rollout-test.jsonl");
            File.WriteAllLines(path, Lines(now, scenario));
            var state = SpeedMonitoring.Scan(new(), [root], now.AddHours(1), 24);
            Check(state.Samples.Count == 1 && state.Samples[0].StreamingStatus == status, $"{scenario}: 未正确保留原TPS或标明缺失原因。");
            var bucket = SpeedMonitoring.Aggregate(state.Samples, new SpeedOptions(), now.AddHours(1)).Single();
            Check(bucket.TotalTps == 20, $"{scenario}: 流式处理改变原始TPS。");
            Check(bucket.StreamingCount == (scenario == "good" ? 1 : 0), $"{scenario}: 无证据记录进入流式估计。");
            if (scenario == "good") Check(Math.Abs(bucket.StreamingTps!.Value - 200d / 7) < 1e-6 && bucket.MatchedEffectiveTps == 20,
                "流式分子分母或同批原TPS对照错误。");
            else Check(bucket.StreamingTps is null && bucket.StreamingExclusions.Values.Sum() == 1,
                "缺失流式记录产生伪零或未计入覆盖缺口。");
        }

        var directory = TemporaryRoot(); var file = Path.Combine(directory, "rollout-incremental.jsonl");
        var lines = Lines(now, "good");
        File.WriteAllLines(file, lines[..^1]);
        var partial = SpeedMonitoring.Scan(new(), [directory], now.AddHours(1), 24);
        Check(partial.Samples.Count == 0, "用量未完成就提前生成响应。");
        var reloaded = JsonSerializer.Deserialize<SpeedState>(JsonSerializer.Serialize(partial))!;
        File.AppendAllText(file, lines[^1]);
        Check(SpeedMonitoring.Scan(reloaded, [directory], now.AddHours(1), 24).Samples.Count == 0, "半行token被提前消费。");
        File.AppendAllText(file, "\n");
        var completed = SpeedMonitoring.Scan(reloaded, [directory], now.AddHours(1), 24);
        Check(completed.Samples.Single().StreamingStatus == "available", "跨重启丢失item与用量关联。");
        Check(reloaded.Cursors.Values.Single().Streaming.FirstItemId is not null, "成功扫描原地清空旧游标关联。");
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            Check(SpeedMonitoring.Scan(completed, [directory], now.AddHours(1), 24).Samples.Count == 1, "未变文件仍被重复打开。");

        var legacy = JsonSerializer.Deserialize<SpeedState>(JsonSerializer.Serialize(completed))!;
        legacy.Version = 1; legacy.Cursors.Values.Single().EvidenceVersion = 0;
        legacy.Samples[0] = legacy.Samples[0] with { StreamingStartedAt = null, StreamingStatus = "legacy" };
        var upgraded = SpeedMonitoring.Scan(legacy, [directory], now.AddHours(1), 24);
        Check(upgraded.Version == 2 && upgraded.Samples.Count == 1 && upgraded.Samples[0].StreamingStartedAt.HasValue,
            "旧缓存补证据丢样本或未补读。");
        Check(legacy.Version == 1 && legacy.Samples[0].StreamingStartedAt is null, "迁移原地修改旧缓存。");
        File.Delete(file);
        Check(SpeedMonitoring.Scan(upgraded, [directory], now.AddHours(1), 24).Samples.Count == 1, "缺源文件时删除仍在窗口内的旧样本。");

        var duplicateRoot = TemporaryRoot(); var duplicatePath = Path.Combine(duplicateRoot, "rollout-duplicate.jsonl");
        File.WriteAllLines(duplicatePath, Lines(now, "good").Concat(Lines(now.AddMinutes(1), "good")));
        Check(SpeedMonitoring.Scan(new(), [duplicateRoot], now.AddHours(1), 24).Samples.Count == 1,
            "重复response ID产生第二条响应。");
        var good = upgraded.Samples[0];
        var mixed = SpeedMonitoring.Aggregate([good, good with { EndedAt = good.EndedAt.AddSeconds(1),
            StartedAt = good.StartedAt.AddSeconds(1), StreamingStartedAt = null, StreamingStatus = "legacy", OutputTokens = 900 }],
            new SpeedOptions(), now.AddHours(1)).Single();
        Check(mixed.Count == 2 && mixed.StreamingCount == 1 && Math.Abs(mixed.StreamingTps!.Value - 200d / 7) < 1e-6 &&
              mixed.MatchedEffectiveTps == 20, "流式分子混入未匹配样本或对照用了不同响应集合。");
        var modesRoot = TemporaryRoot();
        File.WriteAllLines(Path.Combine(modesRoot, "normal.jsonl"), Lines(now, "good"));
        File.WriteAllLines(Path.Combine(modesRoot, "fast.jsonl"), Lines(now, "good", "gpt-6.1-sol", "priority", "response-2"));
        var modes = SpeedMonitoring.Scan(new(), [modesRoot], now.AddHours(1), 24);
        var separated = SpeedMonitoring.Aggregate(modes.Samples, new SpeedOptions(), now.AddHours(1));
        Check(separated.Count == 2 && separated.Single(b => b.Model == "gpt-6.1-sol") is { ServiceTier: "fast", StreamingCount: 1 } &&
              separated.Single(b => b.Model == "gpt-6-astra") is { ServiceTier: "standard", StreamingCount: 1 },
            "流式估计合并了不同模型或Standard/Fast模式。");
    }

    /// <summary>验证速度页指标切换、缺数据模式、覆盖计数、同批对照及语言切换，高缩放可输出合成截图。</summary>
    public static void VerifyUi(string? output = null)
    {
        var now = DateTimeOffset.Now.AddMinutes(-2);
        SpeedSample[] samples = [
            new("a", now.AddSeconds(-10), now, "gpt-6-astra", "standard", 200, 50)
                { StreamingStartedAt = now.AddSeconds(-5), StreamingStatus = "available" },
            new("b", now.AddSeconds(-10), now.AddSeconds(1), "gpt-6-astra", "standard", 900, 0),
            new("c", now.AddSeconds(-10), now.AddSeconds(2), "gpt-6.1-sol", "fast", 100, 0)
        ];
        var options = new SpeedOptions();
        var view = new MonitorViewSnapshot(now, "stream test", null, null, 0, 0, 0, [], [])
        { SpeedSamples = samples, SpeedSampleCount = samples.Length, SpeedHistoryHours = 24, SpeedStatus = "合成数据 · 不代表账号用量" };
        using var form = new ChartForm(new AppSettings { Speed = options }, _ => { });
        form.UpdateView(view, new()); form.ShowSpeedTab();
        form.Scale(new SizeF(1.5f, 1.5f)); form.ClientSize = new Size(1260, 860); Application.DoEvents();
        var panel = Find<SpeedPanel>(form).Single(); var metric = Find<ComboBox>(panel).Single(c => c.Name == "SpeedMetric");
        var chart = Find<SpeedChartControl>(panel).Single(); var grid = Find<DataGridView>(panel).Single();
        metric.SelectedIndex = 2; Application.DoEvents();
        Check(chart.DisplayedSeriesCount == 1, "流式图混入没有证据的Fast模型。");
        Check(Find<MetricCard>(panel).Any(c => c.AccessibleName!.StartsWith(UiText.Get("SpeedStreamingTps") + ": " + 40d.ToString("N2"))),
            "流式主卡未使用流式耗时。");
        Check(Find<MetricCard>(panel).Any(c => c.AccessibleName!.StartsWith(UiText.Get("SpeedMatchedEffectiveTps") + ": " + 20d.ToString("N2"))),
            "对照卡不是同批样本。");
        Check(Find<MetricCard>(panel).Any(c => c.AccessibleName!.Contains("1 / 3")), "覆盖卡计数与图表不一致。");
        Check(grid.Rows.Count == 2 && grid.Columns.Cast<DataGridViewColumn>().Any(c => c.DataPropertyName == "StreamingExclusions"),
            "表格丢弃缺失桶或没有排除原因。");
        Find<ComboBox>(panel).Single(c => c.Name == "SpeedTierFilter").SelectedIndex = 2;
        Check(chart.DisplayedSeriesCount == 0 && Find<MetricCard>(panel).Any(c =>
            c.AccessibleName!.StartsWith(UiText.Get("SpeedStreamingTps") + ": —")), "Fast无证据时回退了原TPS。");
        Find<ComboBox>(panel).Single(c => c.Name == "SpeedTierFilter").SelectedIndex = 0;
        UiText.SetLanguage(UiLanguage.English);
        form.UpdateView(view, new());
        Check(metric.SelectedIndex == 2 && metric.Text == UiText.Get("SpeedStreamingTps"), "语言切换丢失流式选择。");
        UiText.SetLanguage(UiLanguage.ChineseSimplified); form.UpdateView(view, new()); Application.DoEvents();
        Check(chart.Height >= 180, "高缩放说明区挤掉流式图表。");
        if (output is not null)
        {
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(output);
        }
    }

    /// <summary>只读运行正式扫描器核对本地近期日志结果，不保存speed.json、不调用模型、不输出会话或正文。</summary>
    public static void Audit(string root)
    {
        var now = DateTimeOffset.Now;
        var state = SpeedMonitoring.Scan(new(), [root], now, 24);
        var options = new SpeedOptions();
        var buckets = SpeedMonitoring.Aggregate(state.Samples, options, now, now.AddHours(-4), now);
        var cohorts = buckets.GroupBy(b => (b.Model, b.ServiceTier)).Select(g => new
        {
            g.Key.Model, g.Key.ServiceTier, Responses = g.Sum(b => b.Count), Streaming = g.Sum(b => b.StreamingCount),
            StreamingTps = g.Sum(b => b.StreamingSeconds) > 0 ? g.Sum(b => b.StreamingOutputTokens) / g.Sum(b => b.StreamingSeconds) : (double?)null,
            MatchedEffectiveTps = g.Sum(b => b.MatchedEffectiveSeconds) > 0 ? g.Sum(b => b.StreamingOutputTokens) / g.Sum(b => b.MatchedEffectiveSeconds) : (double?)null
        });
        Console.WriteLine(JsonSerializer.Serialize(new { state.Version, state.MalformedLines, state.MissingBoundaries, Cohorts = cohorts }));
    }

    /// <summary>生成没有正文的真实日志结构；故障场景只改变关键证据，用于验证业务结果而非冻结实现。</summary>
    private static string[] Lines(DateTimeOffset start, string scenario, string model = "gpt-6-astra", string tier = "default", string response = "response-1")
    {
        const string owner = "test-owner";
        var rows = new List<object> {
            new { timestamp = start, type = "session_meta", payload = new { id = owner } },
            new { timestamp = start, type = "event_msg", payload = new { type = "task_started" } },
            new { timestamp = start, type = "turn_context", payload = new { model, service_tier = tier } }
        };
        var usage = new { input_tokens = 1000, cached_input_tokens = 500, output_tokens = 200, reasoning_output_tokens = 50 };
        if (scenario == "native_tool_first") rows.Add(new { timestamp = start.AddSeconds(2), type = "response_item",
            payload = new { type = "web_search_call", id = "native-tool-1" } });
        if (scenario == "retry") rows.Add(new { timestamp = start.AddSeconds(2), type = "event_msg", payload = new { type = "stream_error" } });
        if (scenario != "missing_time") rows.Add(new { timestamp = start.AddSeconds(10), type = "event_msg", payload = new {
            type = "item_completed", item = new { type = scenario == "hidden" ? "AgentMessage" : "Reasoning", id = "item-1" },
            started_at_ms = (scenario == "collapsed" ? start.AddSeconds(10) : scenario == "outside" ? start.AddSeconds(-2) :
                scenario == "short" ? start.AddSeconds(9.5) : start.AddSeconds(3)).ToUnixTimeMilliseconds(),
            completed_at_ms = start.AddSeconds(10).ToUnixTimeMilliseconds() } });
        rows.Add(new { timestamp = start.AddSeconds(10), type = "response_item", payload = new {
            type = scenario == "tool_first" ? "function_call" : scenario == "hidden" ? "message" : "reasoning",
            id = "item-1", call_id = "tool-1", role = "assistant" } });
        if (scenario != "missing_usage") rows.Add(new { timestamp = start.AddSeconds(10.05), type = "token_usage_record", payload = new {
            thread_id = scenario == "foreign_owner" ? "other-owner" : owner, response_id = response,
            usage = new { input_tokens = 1000, cached_input_tokens = 500, output_tokens = scenario == "mismatch" ? 100 : 200, reasoning_output_tokens = 50 } } });
        rows.Add(new { timestamp = start.AddSeconds(10.1), type = "event_msg", payload = new { type = "token_count", info = new { last_token_usage = usage } } });
        return rows.Select(row => JsonSerializer.Serialize(row)).ToArray();
    }

    /// <summary>临时夹具目录只属于测试，失败时保留数据供检查。</summary>
    private static string TemporaryRoot() { var path = Path.Combine(Path.GetTempPath(), "quota-stream-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    /// <summary>断言失败直接抛出原始业务原因。</summary>
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    /// <summary>按控件类型与公开名称查找业务区域，不依赖私有实现字段。</summary>
    private static IEnumerable<T> Find<T>(Control root) where T : Control => root.Controls.Cast<Control>().SelectMany(c =>
        (c is T match ? new[] { match } : Enumerable.Empty<T>()).Concat(Find<T>(c)));
}
