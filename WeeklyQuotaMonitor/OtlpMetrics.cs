using ProtoBuf;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WeeklyQuotaMonitor;

/// <summary>仅接收标准 OTLP 显式直方图字段；字段编号来自 opentelemetry-proto v1.5.0，未知字段由 protobuf-net 跳过。</summary>
[ProtoContract] public sealed class OtlpMetrics
{
    [ProtoMember(1)] public List<OtlpResourceMetrics> Resources { get; } = [];
}
[ProtoContract] public sealed class OtlpResourceMetrics
{
    [ProtoMember(1)] public OtlpResource? Resource { get; set; }
    [ProtoMember(2)] public List<OtlpScopeMetrics> Scopes { get; } = [];
}
[ProtoContract] public sealed class OtlpResource
{
    [ProtoMember(1)] public List<OtlpAttribute> Attributes { get; } = [];
}
[ProtoContract] public sealed class OtlpScopeMetrics
{
    [ProtoMember(1)] public OtlpScope? Scope { get; set; }
    [ProtoMember(2)] public List<OtlpMetric> Metrics { get; } = [];
}
[ProtoContract] public sealed class OtlpScope
{
    [ProtoMember(1)] public string Name { get; set; } = "";
    [ProtoMember(2)] public string Version { get; set; } = "";
    [ProtoMember(3)] public List<OtlpAttribute> Attributes { get; } = [];
}
[ProtoContract] public sealed class OtlpAttribute
{
    [ProtoMember(1)] public string Key { get; set; } = "";
    [ProtoMember(2)] public OtlpValue? Value { get; set; }
}
[ProtoContract] public sealed class OtlpValue
{
    [ProtoMember(1)] public string? Text { get; set; }
    [ProtoMember(2)] public bool? Boolean { get; set; }
    [ProtoMember(3)] public long? Integer { get; set; }
    [ProtoMember(4)] public double? Number { get; set; }
    [ProtoMember(5)] public OtlpArray? Array { get; set; }
    [ProtoMember(6)] public OtlpResource? Map { get; set; }
    [ProtoMember(7)] public byte[]? Bytes { get; set; }
}
[ProtoContract] public sealed class OtlpArray
{
    [ProtoMember(1)] public List<OtlpValue> Values { get; } = [];
}
[ProtoContract] public sealed class OtlpMetric
{
    [ProtoMember(1)] public string Name { get; set; } = "";
    [ProtoMember(3)] public string Unit { get; set; } = "";
    [ProtoMember(9)] public OtlpHistogram? Histogram { get; set; }
}
[ProtoContract] public sealed class OtlpHistogram
{
    [ProtoMember(1)] public List<OtlpPoint> Points { get; } = [];
    [ProtoMember(2)] public int Temporality { get; set; }
}
[ProtoContract] public sealed class OtlpPoint
{
    [ProtoMember(8)] public List<OtlpExemplar> Exemplars { get; } = [];
    [ProtoMember(9)] public List<OtlpAttribute> Attributes { get; } = [];
    [ProtoMember(2, DataFormat = DataFormat.FixedSize)] public ulong Start { get; set; }
    [ProtoMember(3, DataFormat = DataFormat.FixedSize)] public ulong End { get; set; }
    [ProtoMember(4, DataFormat = DataFormat.FixedSize)] public ulong Count { get; set; }
    [ProtoMember(5)] public double? Sum { get; set; }
    [ProtoMember(6, DataFormat = DataFormat.FixedSize, IsPacked = true)] public List<ulong> Counts { get; } = [];
    [ProtoMember(7, IsPacked = true)] public List<double> Bounds { get; } = [];
    [ProtoMember(10)] public uint Flags { get; set; }
}

/// <summary>显式直方图的稀疏 trace 关联样本，仅用于显式诊断，不推断为全部请求。</summary>
[ProtoContract] public sealed class OtlpExemplar
{
    [ProtoMember(2, DataFormat = DataFormat.FixedSize)] public ulong Time { get; set; }
    [ProtoMember(3)] public double? Value { get; set; }
    [ProtoMember(4)] public byte[] SpanId { get; set; } = [];
    [ProtoMember(5)] public byte[] TraceId { get; set; } = [];
    [ProtoMember(7)] public List<OtlpAttribute> Attributes { get; } = [];
}

/// <summary>只保留数值直方图与模型维度；序列摘要用于外部 OTLP 去重，不存储原始资源属性。</summary>
public sealed record TelemetryPoint(string Series, string Metric, string Model, string Tier,
    ulong Start, ulong End, int Temporality, long Count, double Sum, double[] Bounds, long[] Counts);

public static class TelemetryMetrics
{
    public static readonly string[] Names = ["codex.turn.ttft.duration_ms",
        "codex.responses_api_engine_service_ttft.duration_ms", "codex.responses_api_engine_iapi_ttft.duration_ms",
        "codex.responses_api_engine_service_tbt.duration_ms", "codex.responses_api_inference_time.duration_ms",
        "codex.turn.e2e_duration_ms", "codex.tool.call.duration_ms"];
    public static readonly string[] LabelKeys = ["OtelTurnTtft", "OtelServiceTtft", "OtelIapiTtft", "OtelTbt", "OtelInference", "OtelTurnDuration", "OtelToolDuration"];

    /// <summary>验证所需直方图；显式诊断回调可在筛选前检查属性，正常运行不持有原始包。</summary>
    public static IReadOnlyList<TelemetryPoint> Decode(Stream body, Action<OtlpMetrics>? inspect = null)
    {
        var request = Serializer.Deserialize<OtlpMetrics>(body);
        inspect?.Invoke(request);
        var result = new List<TelemetryPoint>();
        foreach (var resource in request.Resources)
        foreach (var scope in resource.Scopes)
        foreach (var metric in scope.Metrics.Where(m => Names.Contains(m.Name)))
        {
            var histogram = metric.Histogram ?? throw new InvalidDataException($"{metric.Name}: expected explicit histogram");
            if (metric.Unit is not ("ms" or "")) throw new InvalidDataException($"Unsupported unit: {metric.Unit}");
            if (histogram.Temporality is not (1 or 2)) throw new InvalidDataException("Missing aggregation temporality");
            foreach (var p in histogram.Points)
            {
                if ((p.Flags & 1) != 0) continue;
                if (p.Start == 0 || p.End <= p.Start || p.End / 100 > (ulong)(DateTimeOffset.MaxValue.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) ||
                    p.Count > long.MaxValue || p.Sum is not { } sum || !double.IsFinite(sum) || sum < 0 ||
                    p.Bounds.Count > 128 || p.Counts.Count != p.Bounds.Count + 1 ||
                    p.Counts.Any(c => c > long.MaxValue) || p.Counts.Aggregate(0m, (a, c) => a + c) != p.Count ||
                    p.Bounds.Any(b => !double.IsFinite(b)) || !p.Bounds.SequenceEqual(p.Bounds.OrderBy(b => b).Distinct()) ||
                    (p.Count == 0 && sum != 0)) throw new InvalidDataException("Invalid histogram count, sum, time or buckets");
                var attributes = (resource.Resource?.Attributes ?? []).Concat(p.Attributes).ToArray();
                var model = attributes.LastOrDefault(a => a.Key == "model")?.Value?.Text ?? "unknown";
                var tier = attributes.LastOrDefault(a => a.Key == "service_tier")?.Value?.Text switch
                { "priority" or "fast" => "fast", "default" or "standard" => "standard", _ => "unknown" };
                if (model.Length > 160) throw new InvalidDataException("Model dimension exceeds limit");
                // 每个资源、作用域、全部属性及启动时间共同识别序列，避免多个进程的累计值互相覆盖。
                var identity = JsonSerializer.Serialize(new { Resource = Canonical(resource.Resource?.Attributes ?? []),
                    Scope = scope.Scope?.Name, Version = scope.Scope?.Version, ScopeAttributes = Canonical(scope.Scope?.Attributes ?? []),
                    Attributes = Canonical(p.Attributes), metric.Name, metric.Unit,
                    Epoch = histogram.Temporality == 2 ? p.Start : 0UL });
                result.Add(new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))), metric.Name, model, tier,
                    p.Start, p.End, histogram.Temporality, (long)p.Count, sum, p.Bounds.ToArray(), p.Counts.Select(c => (long)c).ToArray()));
            }
        }
        if (result.Count > 10000) throw new InvalidDataException("Too many selected metric points");
        return result;
    }

    /// <summary>属性键排序后生成身份输入；正文属性只在内存参与摘要，不写入数据库。</summary>
    private static object[] Canonical(IEnumerable<OtlpAttribute> attributes) => attributes.OrderBy(a => a.Key, StringComparer.Ordinal)
        .Select(a => (object)new { a.Key, Value = CanonicalValue(a.Value) }).ToArray();

    /// <summary>保留属性类型及数组顺序，嵌套映射排序，防止上报顺序变化产生重复序列。</summary>
    private static object? CanonicalValue(OtlpValue? v) => v is null ? null : new
    { v.Text, v.Boolean, v.Integer, v.Number, v.Bytes, Array = v.Array?.Values.Select(CanonicalValue).ToArray(), Map = v.Map is null ? null : Canonical(v.Map.Attributes) };
}
