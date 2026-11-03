using System.Text.Json;

namespace WeeklyQuotaMonitor.Core;

/// <summary>仅保存响应关联及生命周期时间，不持久化正文；与速度游标一起跨增量读取和重启保存。</summary>
public sealed record StreamingSpeedEvidence
{
    public string? Owner { get; set; }
    public string? FirstItemId { get; set; }
    public string? FirstItemKind { get; set; }
    public Dictionary<string, SpeedItemTiming> Timings { get; set; } = [];
    public string? ResponseId { get; set; }
    public string? LastResponseId { get; set; }
    public SpeedResponseUsage? Usage { get; set; }
    public bool Retried { get; set; }

    /// <summary>复制可变时间字典，取消或失败不能污染协调器已提交的游标。</summary>
    public StreamingSpeedEvidence Copy() => this with { Timings = new(Timings) };

    /// <summary>结束或中断响应时释放临时关联；会话归属与最近响应ID保留以检查重复。</summary>
    public void ResetResponse()
    {
        FirstItemId = FirstItemKind = ResponseId = null;
        Timings.Clear(); Usage = null; Retried = false;
    }

    /// <summary>读取日志白名单：用item ID关联计时，用逐响应记录核对用量；工具起点不能作为生成首token。</summary>
    public void Observe(JsonElement root, JsonElement payload)
    {
        var type = Text(root, "type"); var kind = Text(payload, "type");
        if (type == "session_meta") Owner = Text(payload, "id");
        if (type == "token_usage_record")
        {
            ResponseId = Text(payload, "response_id");
            Usage = Text(payload, "thread_id") == Owner && Owner is not null &&
                    payload.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                ? ReadUsage(usage) : null;
        }
        if (type == "event_msg" && kind is "error" or "stream_error") Retried = true;
        if (type == "response_item" && (kind == "reasoning" || kind?.EndsWith("_call", StringComparison.Ordinal) == true ||
                                       kind == "message" && Text(payload, "role") == "assistant"))
        {
            // 缺ID也必须记下首项类别，不能改用后续可见消息起点而仍计入此前思考token。
            if (FirstItemKind is null) { FirstItemKind = kind; FirstItemId = Text(payload, "id"); }
        }
        if (type == "event_msg" && kind == "item_completed" && payload.TryGetProperty("item", out var item) &&
            item.ValueKind == JsonValueKind.Object && Text(item, "type") is "Reasoning" or "AgentMessage" &&
            Text(item, "id") is { } id)
        {
            var start = Integer(payload, "started_at_ms"); var end = Integer(payload, "completed_at_ms");
            if (start is null || end is null) return;
            if (start < 0 || end < 0 || start > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() ||
                end > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()) throw new InvalidDataException("流式item时间越界。");
            var timing = new SpeedItemTiming(DateTimeOffset.FromUnixTimeMilliseconds(start.Value), DateTimeOffset.FromUnixTimeMilliseconds(end.Value));
            if (Timings.TryGetValue(id, out var previous) && previous != timing)
                throw new InvalidDataException("同一流式item存在冲突时间记录。");
            if (Timings.Count >= 512 && !Timings.ContainsKey(id)) throw new InvalidDataException("单响应流式item数量超过限制。");
            Timings[id] = timing;
        }
    }

    /// <summary>仅在逐响应用量一致且首个生成item具备可信起点时返回估计；缺失保持独立原因，不回退到代理起点。</summary>
    public (DateTimeOffset? Start, string Status) Evaluate(DateTimeOffset start, DateTimeOffset end, JsonElement usage)
    {
        if (Retried) return (null, "retried");
        if (ResponseId is null || Usage is null) return (null, "unverified_usage");
        if (Usage != ReadUsage(usage)) return (null, "usage_mismatch");
        if (FirstItemKind is not ("reasoning" or "message")) return (null, "tool_first");
        if (FirstItemKind == "message" && Usage.Reasoning > 0) return (null, "hidden_reasoning");
        if (FirstItemId is null || !Timings.TryGetValue(FirstItemId, out var timing)) return (null, "missing_item_time");
        if ((timing.End - timing.Start).TotalMilliseconds < 50) return (null, "collapsed_time");
        if (timing.Start < start.AddMilliseconds(-50) || timing.Start >= end || timing.End > end.AddSeconds(2))
            return (null, "outside_response");
        return (timing.Start, "available");
    }

    /// <summary>读取四项必需用量用于交叉核对，缺字段或异型数据不当作零token。</summary>
    private static SpeedResponseUsage? ReadUsage(JsonElement value)
    {
        var input = Integer(value, "input_tokens"); var cached = Integer(value, "cached_input_tokens");
        var output = Integer(value, "output_tokens"); var reasoning = Integer(value, "reasoning_output_tokens");
        if (input is null || cached is null || output is null || reasoning is null) return null;
        if (input < 0 || cached < 0 || cached > input || output < 0 || reasoning < 0 || reasoning > output)
            throw new InvalidDataException("逐响应流式用量不合法。");
        return new(input.Value, cached.Value, output.Value, reasoning.Value);
    }

    /// <summary>可选整数字段缺失或null保持缺失，存在的错误类型明确报错。</summary>
    private static long? Integer(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetInt64(out var result))
            throw new InvalidDataException($"流式字段不是有效整数：{key}。");
        return result;
    }

    /// <summary>只读取指定元数据文本，不保存或输出任意正文。</summary>
    private static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var field) &&
        field.ValueKind == JsonValueKind.String ? field.GetString() : null;
}

public sealed record SpeedItemTiming(DateTimeOffset Start, DateTimeOffset End);
public sealed record SpeedResponseUsage(long Input, long Cached, long Output, long Reasoning);
