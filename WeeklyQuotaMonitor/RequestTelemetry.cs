using System.Globalization;
using System.Text.RegularExpressions;
using ProtoBuf;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor;

/// <summary>响应完成事件的最小摘要；不包含提示词、工具内容、账号或邮箱。</summary>
public sealed record RequestLatency(string Conversation, DateTimeOffset Time, string Model, double TtftMs,
    string? ReportedTier, long? OutputTokens, long? ReasoningTokens, string Origin = "unknown");

public sealed record RequestAssociation(string LocalModel, string State, string Basis);

/// <summary>读取日志白名单并按本地响应关联模式；请求日志 TTFT 独立于回合与服务端直方图。</summary>
public static class RequestTelemetry
{
    public const string Metric = "codex.log_request.ttft";

    /// <summary>只接收 response.completed 的有效 TTFT，优先原始事件时间；不使用导出或接收时刻补造响应时间。</summary>
    public static IReadOnlyList<RequestLatency> Decode(Stream body, DateTimeOffset now)
    {
        var packet = Serializer.Deserialize<RequestLogPacket>(body);
        var result = new List<RequestLatency>();
        var records = 0;
        foreach (var resource in packet.Resources)
        foreach (var scope in resource.Scopes)
        foreach (var log in scope.Records)
        {
            if (++records > 10000) throw new InvalidDataException("Too many OTLP log records");
            var attributes = (resource.Resource?.Attributes ?? []).Concat(scope.Scope?.Attributes ?? []).Concat(log.Attributes)
                .GroupBy(a => a.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Value);
            if (Text(attributes, "event.name") != "codex.sse_event" || Text(attributes, "event.kind") != "response.completed") continue;
            if (!attributes.ContainsKey("ttft_ms")) continue;
            if (!Guid.TryParse(Text(attributes, "conversation.id"), out var conversation)) throw new InvalidDataException("Request TTFT lacks conversation ID");
            var model = Text(attributes, "model");
            if (model is null || !Regex.IsMatch(model, @"^[A-Za-z0-9_.-]{1,160}$")) throw new InvalidDataException("Invalid request model");
            var value = Number(attributes, "ttft_ms");
            // 某些不含首 token 的完成事件没有 TTFT：不是零延迟，也不应伪造一条记录。
            if (value is null) continue;
            if (!double.IsFinite(value.Value) || value < 0) throw new InvalidDataException("Invalid request TTFT");
            DateTimeOffset time;
            var eventTime = Text(attributes, "event.timestamp");
            if (eventTime is not null)
            {
                if (!Regex.IsMatch(eventTime, @"T.*(Z|[+-]\d{2}:\d{2})$") ||
                    !DateTimeOffset.TryParse(eventTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
                    throw new InvalidDataException("Invalid request event timestamp");
            }
            else if (log.Time > 0 && log.Time / 100 <= (ulong)(DateTimeOffset.MaxValue.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks))
                time = new DateTimeOffset(DateTimeOffset.UnixEpoch.UtcTicks + (long)(log.Time / 100), TimeSpan.Zero);
            else throw new InvalidDataException("Request TTFT lacks original timestamp");
            if (time < DateTimeOffset.UnixEpoch || time > now.AddMinutes(5)) throw new InvalidDataException("Request timestamp outside valid range");
            var output = Tokens(attributes, "output_token_count"); var reasoning = Tokens(attributes, "reasoning_token_count");
            if (output.HasValue && reasoning > output) throw new InvalidDataException("Invalid request reasoning tokens");
            var rawTier = attributes.ContainsKey("service_tier") ? Text(attributes, "service_tier") ?? "unsupported" : null;
            result.Add(new(conversation.ToString("D"), time, model.ToLowerInvariant(), value.Value,
                rawTier is null ? null : NormalizeTier(rawTier), output, reasoning, ReadOrigin(attributes)));
        }
        return result;
    }

    /// <summary>缺失与不支持的层级不能冒充 Standard；是否缺失由调用方单独保留。</summary>
    public static string NormalizeTier(string? tier) => tier switch
    { "priority" or "fast" => "fast", "default" or "standard" => "standard", _ => "unknown" };

    /// <summary>只识别明确的用途元数据；缺失或冲突不根据模型名、会话 ID 推断用途，不保存原始属性。</summary>
    private static string ReadOrigin(Dictionary<string, OtlpValue?> attributes)
    {
        var origins = new[] { "session_source", "thread_source", "turn_trigger", "request_kind" }
            .Select(key => Text(attributes, key)).Select(value => value switch
            {
                "memory_extraction" or "memory_extract" => "memory_extraction",
                "memory_consolidation" or "{\"internal\":\"memory_consolidation\"}" => "memory_consolidation",
                "auto_review" => "auto_review",
                "subagent" => "subagent",
                _ => "unknown"
            }).Where(value => value != "unknown").Distinct().ToArray();
        return origins.Length == 1 ? origins[0] : origins.Length > 1 ? "conflicting" : "unknown";
    }

    /// <summary>仅从指定元数据键读取文本，正文 protobuf 字段没有定义，解码时直接跳过。</summary>
    private static string? Text(Dictionary<string, OtlpValue?> attributes, string key) => attributes.GetValueOrDefault(key)?.Text;

    /// <summary>接受 OTLP 数值或不含单位的数值文本；不支持的值明确报错，避免包装为成功。</summary>
    private static double? Number(Dictionary<string, OtlpValue?> attributes, string key)
    {
        if (!attributes.TryGetValue(key, out var value)) return null;
        if (value?.Number is { } number) return number;
        if (value?.Integer is { } integer) return integer;
        if (double.TryParse(value?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        throw new InvalidDataException("Invalid numeric request field: " + key);
    }

    /// <summary>可选 token 计数用于核对同一次响应；非整数、负数或溢出一律拒绝。</summary>
    private static long? Tokens(Dictionary<string, OtlpValue?> attributes, string key)
    {
        var value = Number(attributes, key);
        if (value is null) return null;
        if (!double.IsFinite(value.Value) || value < 0 || value >= long.MaxValue || Math.Truncate(value.Value) != value)
            throw new InvalidDataException("Invalid request token count");
        return (long)value;
    }
}

/// <summary>按会话建立轻量时间索引，模型仅用于匹配后的比较，不另扫全盘或读取当前配置。</summary>
public sealed class RequestModeIndex
{
    private readonly Dictionary<string, SpeedSample[]> _groups;
    public DateTimeOffset? First { get; }

    /// <summary>只索引可识别会话文件名的记录；重复摘要去重，但冲突层级保留为歧义候选。</summary>
    public RequestModeIndex(IReadOnlyList<SpeedSample> samples)
    {
        var valid = samples.Select(s => (Sample: s, Name: Path.GetFileNameWithoutExtension(s.Source)))
            .Where(p => p.Name.Length >= 36 && Guid.TryParse(p.Name[^36..], out _))
            .Select(p => (p.Sample, Conversation: Guid.Parse(p.Name[^36..]).ToString("D"))).ToArray();
        First = valid.Length == 0 ? null : valid.Min(p => p.Sample.EndedAt).AddSeconds(-2);
        _groups = valid.GroupBy(p => p.Conversation).ToDictionary(g => g.Key,
            g => g.Select(p => p.Sample).Distinct().OrderBy(s => s.EndedAt).ToArray());
    }

    /// <summary>直报模式优先；跨模型候选不继承本地速度，以免后台请求误用主对话层级。</summary>
    public (string Tier, string Source) Resolve(RequestLatency request)
    {
        if (request.ReportedTier is not null)
            return (RequestTelemetry.NormalizeTier(request.ReportedTier), request.ReportedTier == "unknown" ? "unknown-event" : "event");
        var (association, sample) = Match(request);
        if (association.State == "ambiguous") return ("unknown", "ambiguous");
        if (association.State == "model-mismatch") return ("unknown", "rollout-model-mismatch");
        return sample?.ServiceTier is "standard" or "fast" ? (sample.ServiceTier, "rollout") : ("unknown", "unknown");
    }

    /// <summary>独立记录本地响应关联，即使事件已直报速度，也保留模型差异和匹配强度。</summary>
    public RequestAssociation Associate(RequestLatency request) => Match(request).Association;

    /// <summary>同会话、结束时间两秒内及所有可用 token 唯一匹配；不偏好同模型，明确后台用途不强拼本地响应。</summary>
    private (RequestAssociation Association, SpeedSample? Sample) Match(RequestLatency request)
    {
        if (request.Origin == "conflicting") return (new("", "unmatched", "explicit-origin"), null);
        if (request.Origin != "unknown") return (new("", "background", "explicit-origin"), null);
        if (!_groups.TryGetValue(request.Conversation, out var samples)) return (new("", "unmatched", "none"), null);
        var lower = request.Time.AddSeconds(-2); var upper = request.Time.AddSeconds(2);
        int left = 0, right = samples.Length;
        while (left < right) { var middle = (left + right) / 2; if (samples[middle].EndedAt < lower) left = middle + 1; else right = middle; }
        SpeedSample? matched = null;
        for (var i = left; i < samples.Length && samples[i].EndedAt <= upper; i++)
        {
            var sample = samples[i];
            if (request.OutputTokens is { } output && sample.OutputTokens != output ||
                request.ReasoningTokens is { } reasoning && sample.ReasoningTokens != reasoning) continue;
            if (matched is not null) return (new("", "ambiguous", "multiple-candidates"), null);
            matched = sample;
        }
        if (matched is null) return (new("", "unmatched", "none"), null);
        return (new(matched.Model, string.Equals(matched.Model, request.Model, StringComparison.OrdinalIgnoreCase) ? "matched" : "model-mismatch",
            request.OutputTokens.HasValue || request.ReasoningTokens.HasValue ? "time-tokens" : "time-only"), matched);
    }
}

// OTLP logs 只定义白名单所需字段，不定义 body、severity、账号等专用字段；任意属性不会被持久化。
[ProtoContract] public sealed class RequestLogPacket { [ProtoMember(1)] public List<RequestLogResource> Resources { get; } = []; }
[ProtoContract] public sealed class RequestLogResource { [ProtoMember(1)] public OtlpResource? Resource { get; set; } [ProtoMember(2)] public List<RequestLogScope> Scopes { get; } = []; }
[ProtoContract] public sealed class RequestLogScope { [ProtoMember(1)] public OtlpScope? Scope { get; set; } [ProtoMember(2)] public List<RequestLogRecord> Records { get; } = []; }
[ProtoContract] public sealed class RequestLogRecord
{
    [ProtoMember(1, DataFormat = DataFormat.FixedSize)] public ulong Time { get; set; }
    [ProtoMember(6)] public List<OtlpAttribute> Attributes { get; } = [];
}
