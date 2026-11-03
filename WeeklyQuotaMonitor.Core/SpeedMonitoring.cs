using System.Text.Json;

namespace WeeklyQuotaMonitor.Core;

public enum SpeedAggregationMode { TimeWindow, ResponseCount }

/// <summary>速度统计独立于额度采样；保留原始响应摘要，改变分组参数即可重新聚合。</summary>
public sealed class SpeedOptions
{
    public bool Enabled { get; set; } = true;
    public SpeedAggregationMode Mode { get; set; }
    public int BucketMinutes { get; set; } = 5;
    public int ResponsesPerBucket { get; set; } = 20;
    public int HistoryHours { get; set; } = 24;
    public double MinimumDurationSeconds { get; set; } = 1;
}

/// <summary>保存一次可还原边界的响应摘要；耗时包含等待、预填充和传输，不是纯解码时间。</summary>
public sealed record SpeedSample(string Source, DateTimeOffset StartedAt, DateTimeOffset EndedAt,
    string Model, string ServiceTier, long OutputTokens, long ReasoningTokens)
{
    public double DurationSeconds => (EndedAt - StartedAt).TotalSeconds;
    public DateTimeOffset? StreamingStartedAt { get; init; }
    public string StreamingStatus { get; init; } = "legacy";
}

/// <summary>独立速度文件状态，不与额度游标、计价或重置基线共享消费位置。</summary>
public sealed class SpeedState
{
    public int Version { get; set; } = 2;
    public Dictionary<string, SpeedCursor> Cursors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SpeedSample> Samples { get; set; } = [];
    public long MalformedLines { get; set; }
    public long MissingBoundaries { get; set; }
    public string LastIssue { get; set; } = string.Empty;
    public DateTimeOffset? UpdatedAt { get; set; }
    public int HistoryHours { get; set; }
}

/// <summary>只持久化计时锚点、调用标识和模型层级，不保存提示词或工具正文。</summary>
public sealed record SpeedCursor
{
    public long Offset { get; set; }
    public long CreationTicks { get; set; }
    public long Length { get; set; }
    public long WriteTicks { get; set; }
    public string Model { get; set; } = "unknown";
    public string Tier { get; set; } = "unknown";
    public DateTimeOffset? Start { get; set; }
    public DateTimeOffset? LastOutput { get; set; }
    public DateTimeOffset? NextStart { get; set; }
    public bool UnknownToolBoundary { get; set; }
    public HashSet<string> PendingTools { get; set; } = [];
    public int EvidenceVersion { get; set; }
    public StreamingSpeedEvidence Streaming { get; set; } = new();
}

/// <summary>一组同模型、同层级的响应；未结束时间桶或未满 N 次响应的组明确标注为进行中。</summary>
public sealed record SpeedBucket(DateTimeOffset Start, DateTimeOffset End, string Model, string ServiceTier,
    int Count, long OutputTokens, long VisibleTokens, double DurationSeconds, bool Complete)
{
    public double TotalTps => OutputTokens / DurationSeconds;
    public double VisibleTps => VisibleTokens / DurationSeconds;
    public int StreamingCount { get; init; }
    public long StreamingOutputTokens { get; init; }
    public double StreamingSeconds { get; init; }
    public double MatchedEffectiveSeconds { get; init; }
    public double? StreamingTps => StreamingSeconds > 0 ? StreamingOutputTokens / StreamingSeconds : null;
    public double? MatchedEffectiveTps => MatchedEffectiveSeconds > 0 ? StreamingOutputTokens / MatchedEffectiveSeconds : null;
    public IReadOnlyDictionary<string, int> StreamingExclusions { get; init; } = new Dictionary<string, int>();
}

/// <summary>还原日志边界并汇总速度；不依赖账号额度接口，也不推断日志中不存在的 TTFT。</summary>
public static class SpeedMonitoring
{
    /// <summary>读取新增完整日志；旧速度缓存保留样本并一次性补读有近期数据的来源以补流式证据，不影响额度游标。</summary>
    public static SpeedState Scan(SpeedState previous, IReadOnlyCollection<string> roots,
        DateTimeOffset now, int historyHours, CancellationToken cancellationToken = default)
    {
        if (previous.Version is not (1 or 2)) throw new InvalidDataException("不支持的速度数据版本。");
        if (historyHours < 1) throw new ArgumentOutOfRangeException(nameof(historyHours));
        var state = new SpeedState
        {
            Samples = previous.Samples.ToList(),
            Cursors = previous.Cursors.ToDictionary(pair => pair.Key,
                pair => pair.Value with { PendingTools = [.. pair.Value.PendingTools], Streaming = pair.Value.Streaming.Copy() }, StringComparer.OrdinalIgnoreCase),
            MalformedLines = previous.MalformedLines, MissingBoundaries = previous.MissingBoundaries,
            LastIssue = previous.LastIssue, HistoryHours = historyHours
        };
        if (historyHours > previous.HistoryHours && previous.HistoryHours > 0) state.Cursors.Clear();
        var cutoff = now.AddHours(-historyHours);
        var files = roots.SelectMany(root => Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.MaxBy(path => new FileInfo(path).Length)!).ToArray();
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var known = state.Samples.Select((sample, index) => (sample, index))
            .ToDictionary(p => (p.sample.Source, p.sample.StartedAt, p.sample.EndedAt), p => p.index);
        var recentSources = state.Samples.Where(s => s.EndedAt >= cutoff).Select(s => s.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.GetFileName(path);
            sources.Add(source);
            var info = new FileInfo(path);
            if (!state.Cursors.TryGetValue(source, out var cursor) || cursor.CreationTicks != info.CreationTimeUtc.Ticks ||
                info.Length < cursor.Length || (info.Length == cursor.Length && info.LastWriteTimeUtc.Ticks != cursor.WriteTicks) ||
                cursor.EvidenceVersion < 1 && (info.Length > cursor.Offset || recentSources.Contains(source)))
                cursor = new SpeedCursor { CreationTicks = info.CreationTimeUtc.Ticks, EvidenceVersion = 1 };
            // 元数据已在上方验证轮转、截短和同长改写；已消费到末尾时无需再次打开文件。
            // 未完成的尾行仍保留旧游标，追加换行后会继续读取。
            var offset = cursor.Offset == info.Length ? cursor.Offset : RolloutLogReader.ProcessCompleteLines(path, cursor.Offset, line =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var sample = Observe(document.RootElement, cursor, source);
                    if (sample is not null && sample.EndedAt >= cutoff)
                    {
                        var key = (sample.Source, sample.StartedAt, sample.EndedAt);
                        if (known.TryGetValue(key, out var index))
                        {
                            var old = state.Samples[index];
                            if (old.OutputTokens != sample.OutputTokens || old.ReasoningTokens != sample.ReasoningTokens)
                                throw new InvalidDataException("旧速度摘要与日志用量冲突，未覆盖旧样本。");
                            state.Samples[index] = old with { StreamingStartedAt = sample.StreamingStartedAt, StreamingStatus = sample.StreamingStatus };
                        }
                        else { known[key] = state.Samples.Count; state.Samples.Add(sample); }
                    }
                }
                catch (JsonException exception)
                {
                    state.MalformedLines++;
                    state.LastIssue = exception.Message;
                    ResetTiming(cursor);
                }
                catch (InvalidDataException exception)
                {
                    state.MissingBoundaries++;
                    state.LastIssue = exception.Message;
                    ResetTiming(cursor);
                }
            });
            cursor.Offset = offset;
            cursor.Length = info.Length;
            cursor.WriteTicks = info.LastWriteTimeUtc.Ticks;
            state.Cursors[source] = cursor;
        }
        foreach (var missing in state.Cursors.Keys.Where(key => !sources.Contains(key)).ToArray()) state.Cursors.Remove(missing);
        state.Samples.RemoveAll(sample => sample.EndedAt < cutoff);
        state.Samples.Sort((a, b) => a.EndedAt.CompareTo(b.EndedAt));
        state.UpdatedAt = now;
        return state;
    }

    /// <summary>按任务开始、工具全部返回和模型输出完成重建代理耗时；心跳不能重复生成样本。</summary>
    private static SpeedSample? Observe(JsonElement root, SpeedCursor cursor, string source)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("timestamp", out var time) ||
            time.ValueKind != JsonValueKind.String || !time.TryGetDateTimeOffset(out var timestamp) ||
            !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("速度日志缺少有效时间或载荷。");
        var type = Text(root, "type");
        var kind = Text(payload, "type");
        cursor.Streaming.Observe(root, payload);
        if (type == "turn_context")
        {
            cursor.Model = Text(payload, "model") ?? "unknown";
            if (payload.TryGetProperty("service_tier", out _)) cursor.Tier = NormalizeTier(Text(payload, "service_tier"));
            cursor.Start ??= timestamp;
        }
        if (type == "event_msg" && kind == "thread_settings_applied")
        {
            cursor.Tier = payload.TryGetProperty("thread_settings", out var settings) && settings.ValueKind == JsonValueKind.Object
                ? NormalizeTier(Text(settings, "service_tier")) : "unknown";
        }
        if (type == "event_msg" && kind == "task_started")
        {
            ResetTiming(cursor);
            cursor.Start = timestamp;
        }
        if (type == "event_msg" && kind is "task_complete" or "task_completed" or "turn_aborted") ResetTiming(cursor);
        if (type == "response_item")
        {
            if (kind is "function_call_output" or "custom_tool_call_output")
            {
                var id = Text(payload, "call_id");
                if (id is null || !cursor.PendingTools.Remove(id)) cursor.UnknownToolBoundary = true;
                cursor.NextStart = timestamp;
                if (cursor.LastOutput is null && cursor.PendingTools.Count == 0 && !cursor.UnknownToolBoundary)
                {
                    cursor.Start = timestamp;
                    cursor.NextStart = null;
                }
            }
            if (kind is "reasoning" or "function_call" or "custom_tool_call" ||
                (kind == "message" && Text(payload, "role") == "assistant"))
            {
                cursor.LastOutput = timestamp;
                if (kind is "function_call" or "custom_tool_call")
                {
                    var id = Text(payload, "call_id");
                    if (id is null) cursor.UnknownToolBoundary = true;
                    else cursor.PendingTools.Add(id);
                }
            }
        }
        if (type != "event_msg" || kind != "token_count" || cursor.LastOutput is null) return null;
        if (!payload.TryGetProperty("info", out var info) || info.ValueKind == JsonValueKind.Null) return null;
        if (info.ValueKind != JsonValueKind.Object || !info.TryGetProperty("last_token_usage", out var usage) ||
            usage.ValueKind != JsonValueKind.Object || !usage.TryGetProperty("output_tokens", out var output) ||
            output.ValueKind != JsonValueKind.Number || !output.TryGetInt64(out var outputTokens))
            throw new InvalidDataException("速度日志缺少输出 token。");
        long reasoning = 0;
        if (usage.TryGetProperty("reasoning_output_tokens", out var reasoningValue) &&
            (reasoningValue.ValueKind != JsonValueKind.Number || !reasoningValue.TryGetInt64(out reasoning)))
            throw new InvalidDataException("速度日志 reasoning token 无效。");
        var start = cursor.Start;
        var end = cursor.LastOutput.Value;
        var duplicate = cursor.Streaming.ResponseId is { } responseId && responseId == cursor.Streaming.LastResponseId;
        var streaming = start.HasValue ? cursor.Streaming.Evaluate(start.Value, end, usage) : (null, "outside_response");
        var acceptedId = cursor.Streaming.ResponseId;
        cursor.Streaming.ResetResponse();
        cursor.LastOutput = null;
        cursor.Start = cursor.PendingTools.Count == 0 && !cursor.UnknownToolBoundary ? cursor.NextStart ?? timestamp : null;
        cursor.NextStart = null;
        if (duplicate) return null;
        if (start is null || end <= start || outputTokens < 0 || reasoning < 0 || reasoning > outputTokens ||
            cursor.Model is "unknown" or "") throw new InvalidDataException("无法确认速度响应边界或 token 分类。");
        if (acceptedId is not null) cursor.Streaming.LastResponseId = acceptedId;
        return new(source, start.Value, end, cursor.Model, cursor.Tier, outputTokens, reasoning)
        { StreamingStartedAt = streaming.Item1, StreamingStatus = streaming.Item2 };
    }

    /// <summary>清空无法继续关联的计时和工具等待状态，不跨中断拼接响应。</summary>
    private static void ResetTiming(SpeedCursor cursor)
    {
        cursor.Start = null; cursor.LastOutput = null; cursor.NextStart = null;
        cursor.PendingTools.Clear(); cursor.UnknownToolBoundary = false;
        cursor.Streaming.ResetResponse();
    }

    /// <summary>仅读取字符串字段，缺失或异型数据不猜测实际值。</summary>
    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var field) &&
        field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    /// <summary>严格区分日志明确记录的 Standard、Fast 和未知层级，不使用当前配置追认过去。</summary>
    private static string NormalizeTier(string? tier) => tier switch
    { "default" or "standard" or "auto" => "standard", "priority" or "fast" => "fast", _ => "unknown" };

    /// <summary>按完成时间筛选后分模型/层级聚合；流式分子分母仅使用同一批有效证据，缺失不会用原TPS补值。</summary>
    public static IReadOnlyList<SpeedBucket> Aggregate(IReadOnlyList<SpeedSample> samples, SpeedOptions options, DateTimeOffset now,
        DateTimeOffset? windowStart = null, DateTimeOffset? windowEnd = null)
    {
        if (options.BucketMinutes < 1 || options.ResponsesPerBucket < 1 || options.HistoryHours < 1 ||
            !double.IsFinite(options.MinimumDurationSeconds) || options.MinimumDurationSeconds < 1 ||
            !Enum.IsDefined(options.Mode)) throw new ArgumentOutOfRangeException(nameof(options));
        var result = new List<SpeedBucket>();
        var lower = windowStart ?? now.AddHours(-options.HistoryHours);
        var upper = windowEnd is { } requestedEnd && requestedEnd < now ? requestedEnd : now;
        if (windowStart.HasValue && windowEnd.HasValue && windowStart >= windowEnd)
            throw new ArgumentException("速度时间窗的开始必须早于结束。");
        var valid = samples.Where(sample => sample.EndedAt >= lower && sample.EndedAt <= upper &&
            sample.DurationSeconds >= options.MinimumDurationSeconds && sample.OutputTokens > 0 &&
            sample.ReasoningTokens >= 0 && sample.ReasoningTokens <= sample.OutputTokens);
        foreach (var group in valid.GroupBy(sample => (sample.Model, sample.ServiceTier)))
        {
            var ordered = group.OrderBy(sample => sample.EndedAt).ToArray();
            var batches = options.Mode == SpeedAggregationMode.ResponseCount
                ? ordered.Chunk(options.ResponsesPerBucket).Select(chunk => (IEnumerable<SpeedSample>)chunk)
                : ordered.GroupBy(sample => sample.EndedAt.ToUnixTimeSeconds() / (options.BucketMinutes * 60L))
                    .Select(bucket => (IEnumerable<SpeedSample>)bucket);
            foreach (var batch in batches)
            {
                var items = batch.ToArray();
                var start = items[0].EndedAt;
                var end = items[^1].EndedAt;
                var complete = items.Length == options.ResponsesPerBucket;
                if (options.Mode == SpeedAggregationMode.TimeWindow)
                {
                    var seconds = options.BucketMinutes * 60L;
                    start = DateTimeOffset.FromUnixTimeSeconds(start.ToUnixTimeSeconds() / seconds * seconds);
                    end = start.AddSeconds(seconds);
                    complete = start >= lower && end <= upper;
                    if (windowStart.HasValue && start < lower) start = lower;
                    if (windowEnd.HasValue && end > upper) end = upper;
                }
                var streaming = items.Where(s => StreamingUnavailableReason(s, options.MinimumDurationSeconds) is null).ToArray();
                result.Add(new(start, end, group.Key.Model, group.Key.ServiceTier, items.Length,
                    items.Sum(sample => sample.OutputTokens), items.Sum(sample => sample.OutputTokens - sample.ReasoningTokens),
                    items.Sum(sample => sample.DurationSeconds), complete)
                {
                    StreamingCount = streaming.Length,
                    StreamingOutputTokens = streaming.Sum(s => s.OutputTokens),
                    StreamingSeconds = streaming.Sum(s => (s.EndedAt - s.StreamingStartedAt!.Value).TotalSeconds),
                    MatchedEffectiveSeconds = streaming.Sum(s => s.DurationSeconds),
                    StreamingExclusions = items.Select(s => StreamingUnavailableReason(s, options.MinimumDurationSeconds))
                        .Where(reason => reason is not null).GroupBy(reason => reason!).ToDictionary(g => g.Key, g => g.Count())
                });
            }
        }
        return result.OrderByDescending(bucket => bucket.End).ThenBy(bucket => bucket.Model).ToArray();
    }

    /// <summary>统一流式样本资格及缺失原因，绘图、计数和均值共享同一最短时长规则。</summary>
    public static string? StreamingUnavailableReason(SpeedSample sample, double minimumSeconds)
    {
        if (sample.StreamingStatus != "available") return sample.StreamingStatus;
        if (sample.StreamingStartedAt is not { } start) return "missing_item_time";
        if (start < sample.StartedAt.AddMilliseconds(-50) || start >= sample.EndedAt) return "outside_response";
        return (sample.EndedAt - start).TotalSeconds < minimumSeconds ? "short_stream" : null;
    }
}
