using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProtoBuf;
using WeeklyQuotaMonitor;

namespace WeeklyQuotaMonitor.BusinessTests;

/// <summary>一次性检查日志、trace 和 exemplar 的关联字段；不持久化正文、资源标识或完整请求。</summary>
internal sealed class TelemetryCorrelationAudit
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, int> _packets = [], _keys = [], _counts = [];
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, Dictionary<string, int>> _models = [];
    private readonly Dictionary<string, string> _aliases = [];
    private readonly bool _modelsOnly;
    /// <summary>模型探测只持续六十秒，并将关联标识替换为存在标记。</summary>
    public TelemetryCorrelationAudit(bool modelsOnly = false) { _modelsOnly = modelsOnly; }
    private int _omitted, _bytes;
    private bool Expired => _clock.Elapsed.TotalSeconds >= (_modelsOnly ? 60 : 90);

    /// <summary>对每条管线独立限量，避免高频 logs 挤掉尚未到达的 traces；正文始终只在内存解码。</summary>
    public void ObserveEvents(string path, Stream body)
    {
        if (Expired || _packets.GetValueOrDefault(path) >= 40) return;
        _packets[path] = _packets.GetValueOrDefault(path) + 1;
        if (path == "/v1/logs")
        {
            var packet = Serializer.Deserialize<Logs>(body);
            foreach (var resource in packet.Resources)
            foreach (var scope in resource.Scopes)
            foreach (var log in scope.Records)
            {
                if (Expired) return;
                var fields = Fields((resource.Resource?.Attributes ?? []).Concat(scope.Scope?.Attributes ?? []).Concat(log.Attributes), "log");
                AddId(fields, "trace_id", log.TraceId, 16); AddId(fields, "span_id", log.SpanId, 8);
                var name = Label(log.EventName);
                if (name == "<unclassified>") name = fields.GetValueOrDefault("event.name", Label(log.Body?.Text));
                var time = log.Time;
                if (time == 0 && fields.TryGetValue("event.timestamp", out var eventTime) &&
                    DateTimeOffset.TryParse(eventTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
                { time = checked((ulong)(instant.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100); fields["time_basis"] = "event.timestamp"; }
                if (time == 0) { time = log.ObservedTime; fields["time_basis"] = "observed_time"; }
                Add(new("log", name, time, time, fields));
            }
        }
        else
        {
            var packet = Serializer.Deserialize<Traces>(body);
            foreach (var resource in packet.Resources)
            foreach (var scope in resource.Scopes)
            foreach (var span in scope.Spans)
            {
                if (Expired) return;
                var fields = Fields((resource.Resource?.Attributes ?? []).Concat(scope.Scope?.Attributes ?? []).Concat(span.Attributes), "span");
                AddId(fields, "trace_id", span.TraceId, 16); AddId(fields, "span_id", span.SpanId, 8); AddId(fields, "parent_span_id", span.Parent, 8);
                Add(new("span", Label(span.Name), span.Start, span.End, fields));
                foreach (var item in span.Events.Take(64))
                {
                    var eventFields = Fields(item.Attributes, "span-event");
                    AddId(eventFields, "trace_id", span.TraceId, 16); AddId(eventFields, "span_id", span.SpanId, 8);
                    Add(new("span-event", Label(item.Name), item.Time, item.Time, eventFields));
                }
            }
        }
    }

    /// <summary>只检查所需指标的 exemplar，不把稀疏示例视为直方图中所有请求。</summary>
    public void ObserveMetrics(OtlpMetrics packet)
    {
        if (Expired || _packets.GetValueOrDefault("metrics") >= 40) return;
        _packets["metrics"] = _packets.GetValueOrDefault("metrics") + 1;
        foreach (var resource in packet.Resources)
        foreach (var scope in resource.Scopes)
        foreach (var metric in scope.Metrics.Where(m => TelemetryMetrics.Names.Contains(m.Name)))
        foreach (var point in metric.Histogram?.Points ?? [])
        {
            _counts["metric-points"] = _counts.GetValueOrDefault("metric-points") + 1;
            if (_modelsOnly) Fields((resource.Resource?.Attributes ?? []).Concat(scope.Scope?.Attributes ?? []).Concat(point.Attributes), "metric");
            foreach (var exemplar in point.Exemplars)
            {
                var fields = Fields(exemplar.Attributes, "exemplar");
                AddId(fields, "trace_id", exemplar.TraceId, 16); AddId(fields, "span_id", exemplar.SpanId, 8);
                if (exemplar.Value is { } value && double.IsFinite(value)) fields["value_ms"] = value.ToString("R", CultureInfo.InvariantCulture);
                Add(new("exemplar", metric.Name, exemplar.Time, exemplar.Time, fields));
            }
        }
    }

    /// <summary>键名限字符和数量；仅导出关联 ID、模型/模式枚举及计时数值，正文与错误详情不进入结果。</summary>
    private Dictionary<string, string> Fields(IEnumerable<OtlpAttribute> attributes, string kind, int depth = 0)
    {
        var result = new Dictionary<string, string>();
        foreach (var a in attributes.Take(256))
        {
            if (!Regex.IsMatch(a.Key, @"^[A-Za-z0-9_.-]{1,80}$")) { _omitted++; continue; }
            var catalog = kind + "." + a.Key;
            if (_keys.ContainsKey(catalog) || _keys.Count < 256) _keys[catalog] = _keys.GetValueOrDefault(catalog) + 1;
            else _omitted++;
            // 仅检查有限深度的协议容器，不递归正文或任意工具数据。
            if (a.Value?.Map is { } map && a.Key is "headers" or "response.headers" or "response" or "request")
            {
                if (depth < 3)
                    foreach (var pair in Fields(map.Attributes, kind + "." + a.Key, depth + 1))
                        if (result.Count < 32) result[a.Key + "." + pair.Key] = pair.Value;
                        else _omitted++;
                else _omitted++;
            }
            var key = a.Key.Replace("_", "").Replace(".", "").ToLowerInvariant();
            var text = a.Value?.Text;
            if (_modelsOnly && Regex.IsMatch(key, @"(^model$|modelid$|modelname$|modelslug$|modelversion$|model$)") &&
                text is not null && Regex.IsMatch(text, @"^[A-Za-z0-9][A-Za-z0-9_.:/-]{0,119}$"))
            {
                if (!_models.ContainsKey(catalog) && _models.Count < 64) _models[catalog] = [];
                if (_models.TryGetValue(catalog, out var values))
                {
                    if (values.ContainsKey(text) || values.Count < 64) values[text] = values.GetValueOrDefault(text) + 1;
                    else _omitted++;
                }
                result[a.Key] = text;
            }
            if (result.Count >= 16) { _omitted++; continue; }
            if (key is "conversationid" or "sessionid" or "threadid" or "turnid" or "requestid" or "responseid" or "traceid" or "spanid" or "parentturnid")
            {
                if (text is not null && (Guid.TryParse(text, out _) || Regex.IsMatch(text, @"^[a-fA-F0-9]{16,64}$|^(req|resp|call|turn|thread|session)[_-][A-Za-z0-9_-]{4,112}$"))) result[a.Key] = text;
            }
            else if (key is "model" && text is not null && Regex.IsMatch(text, @"^(gpt-[A-Za-z0-9.-]{1,60}|codex-auto-review)$")) result[a.Key] = text;
            else if (key is "servicetier" or "reasoningeffort" or "effort" or "speedmode" || key.EndsWith("reasoningeffort", StringComparison.Ordinal))
            {
                if (text is "default" or "standard" or "priority" or "fast" or "auto" or "unknown" or "low" or "medium" or "high" or "xhigh" or "max" or "ultra") result[a.Key] = text;
            }
            else if (key is "eventname" or "otelname" or "name" or "kind" or "eventkind")
            {
                if (Label(text) != "<unclassified>") result[a.Key] = Label(text);
            }
            else if (key == "eventtimestamp" && text is { Length: < 80 } && text.Contains('T') &&
                DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
                result[a.Key] = instant.ToUniversalTime().ToString("O");
            else if (Regex.IsMatch(key, "duration|elapsed|latency|ttft|tbt|timestamp|timeto"))
            {
                var number = a.Value?.Number ?? a.Value?.Integer;
                if (number is null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) number = parsed;
                if (number is { } value && double.IsFinite(value)) result[a.Key] = value.ToString("R", CultureInfo.InvariantCulture);
            }
        }
        return result;
    }

    /// <summary>仅保留固定长度、非全零的协议 ID。</summary>
    private static void AddId(Dictionary<string, string> fields, string key, byte[] value, int length)
    { if (value.Length == length && value.Any(b => b != 0)) fields[key] = Convert.ToHexString(value).ToLowerInvariant(); }

    /// <summary>事件名称采用固定白名单，不把任意日志正文或动态 span 名称保存下来。</summary>
    private static string Label(string? name) => name is "codex.conversation_starts" or "codex.api_request" or "codex.sse_event" or
        "codex.websocket_request" or "codex.websocket_event" or "codex.user_prompt" or "codex.tool_result" or "codex.tool_decision" or
        "response.created" or "response.completed" or "response.output_text.delta" or "response.reasoning_summary_text.delta" or
        "server_model" or "ServerModel" or "model/rerouted" or "model_rerouted" or "model_reroute" or
        "run_turn" or "run_task" or "run_sampling_request" or "model_request" or "api_request" or "sampling" or "tool_call" ? name : "<unclassified>";

    /// <summary>记录数及序列化大小双限量，截断明确计数；额外观测不影响正式 metrics 入库。</summary>
    private void Add(Entry entry)
    {
        if (_modelsOnly)
            foreach (var key in entry.Fields.Keys.Where(k => k.Replace("_", "").Replace(".", "").EndsWith("id", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                var value = entry.Fields[key];
                if (value.StartsWith("id-", StringComparison.Ordinal)) continue;
                if (!_aliases.ContainsKey(value) && _aliases.Count < 512) _aliases[value] = "id-" + (_aliases.Count + 1);
                entry.Fields[key] = _aliases.GetValueOrDefault(value, "<present-unlinked>");
            }
        _counts[entry.Kind] = _counts.GetValueOrDefault(entry.Kind) + 1;
        foreach (var label in new[] { entry.Name, entry.Fields.GetValueOrDefault("otel.name"), entry.Fields.GetValueOrDefault("event.kind") }
                     .Where(value => value is "server_model" or "ServerModel" or "model/rerouted" or "model_rerouted" or "model_reroute").Distinct())
            _counts["routing-event." + label] = _counts.GetValueOrDefault("routing-event." + label) + 1;
        if (entry.Kind == "span" && entry.Name == "<unclassified>" && !entry.Fields.ContainsKey("model") &&
            !entry.Fields.Keys.Any(k => k.Replace("_", "").Replace(".", "") is "turnid" or "threadid")) return;
        if (entry.Kind == "log" && entry.Name is "codex.tool_result" or "codex.tool_decision") return;
        var size = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(entry));
        if (Expired || _entries.Count(e => e.Kind == entry.Kind) >= 64 || _bytes + size > 80 * 1024) { _omitted++; return; }
        _entries.Add(entry); _bytes += size;
    }

    /// <summary>显式接管现有端口九十秒，正常 metrics 持续入库；结束后只保存一份不超过 128 KiB 的白名单摘要。</summary>
    public static void Capture(string directory, string output, bool modelsOnly = false)
    {
        if (File.Exists(output)) throw new IOException("报告已存在，拒绝覆盖。");
        var options = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(directory, "settings.json")))!.Telemetry;
        if (!options.Enabled) throw new InvalidOperationException("不自动开启已禁用的遥测。");
        var audit = new TelemetryCorrelationAudit(modelsOnly);
        var database = Path.Combine(directory, "telemetry.sqlite");
        var requestStore = modelsOnly ? new TelemetryStore(database, options) : null;
        var index = modelsOnly ? new RequestModeIndex(new JsonStorage().LoadSpeedState().Samples) : null;
        var service = new TelemetryService(database, audit.ObserveMetrics, (path, body) =>
        {
            audit.ObserveEvents(path, body);
            // 临时模型探测继续保存正常请求 TTFT；仅诊断摘要不落盘原始事件。
            if (modelsOnly && path == "/v1/logs")
            {
                body.Position = 0;
                requestStore!.AppendRequests(RequestTelemetry.Decode(body, DateTimeOffset.UtcNow), index!);
            }
        });
        try
        {
            service.ConfigureAsync(options).GetAwaiter().GetResult();
            if (!service.Listening) throw new InvalidOperationException(service.Error);
            Console.WriteLine($"诊断已启动：{(modelsOnly ? 60 : 90)} 秒，每条管线最多 40 包，每类最多 64 条白名单记录。");
            while (!audit.Expired) { Thread.Sleep(1000); if (!service.Listening || service.Failed > 0) throw new InvalidOperationException(service.Error); }
        }
        finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        var report = JsonSerializer.Serialize(new { EndUtc = DateTimeOffset.UtcNow, Seconds = audit._clock.Elapsed.TotalSeconds,
            Packets = audit._packets, Counts = audit._counts, Keys = audit._keys, Models = audit._models, Omitted = audit._omitted, Entries = audit._entries });
        if (Encoding.UTF8.GetByteCount(report) > 128 * 1024) throw new InvalidDataException("报告超过 128 KiB。");
        File.WriteAllText(output, report, new UTF8Encoding(false));
        Console.WriteLine($"完成：记录={audit._entries.Count}; 截断={audit._omitted}; 字节={Encoding.UTF8.GetByteCount(report)}");
        if (audit._entries.Count == 0) throw new InvalidOperationException("没有可检查事件，不能得出关联字段不存在的结论。");
    }

    /// <summary>验证实际 protobuf 日志中的 UUID 和计时可保留，正文不可保留；二进制 trace ID 的长度必须合法。</summary>
    public static void Verify()
    {
        const string secret = "PRIVATE_TEST_PROMPT"; const string id = "01a0234b-03f6-7442-8751-73b7f0171206";
        var packet = new Logs(); var resource = new LogResource(); var scope = new LogScope(); var log = new Log { Time = 100, Body = new() { Text = secret } };
        packet.Resources.Add(resource); resource.Scopes.Add(scope); scope.Records.Add(log);
        log.Attributes.Add(new() { Key = "conversation.id", Value = new() { Text = id } });
        log.Attributes.Add(new() { Key = "duration_ms", Value = new() { Number = 123 } });
        log.Time = 0;
        log.Attributes.Add(new() { Key = "event.timestamp", Value = new() { Text = "2026-09-11T08:00:00.123Z" } });
        log.Attributes.Add(new() { Key = "prompt", Value = new() { Text = secret } });
        var audit = new TelemetryCorrelationAudit(); using var stream = new MemoryStream(); Serializer.Serialize(stream, packet); stream.Position = 0;
        audit.ObserveEvents("/v1/logs", stream);
        var json = JsonSerializer.Serialize(audit._entries);
        if (json.Contains(secret) || !json.Contains(id) || !json.Contains("123")) throw new InvalidOperationException("关联白名单或隐私验证失败。");
        if (audit._entries[0].Start != 1789113600123000000UL) throw new InvalidOperationException("原始事件时间未正确恢复。");
        var traces = new Traces(); var tr = new TraceResource(); var ts = new TraceScope();
        traces.Resources.Add(tr); tr.Scopes.Add(ts);
        ts.Spans.Add(new Span { TraceId = Enumerable.Repeat((byte)1, 16).ToArray(), SpanId = Enumerable.Repeat((byte)2, 8).ToArray(),
            Name = "run_turn", Start = 100, End = 200 });
        using var traceStream = new MemoryStream(); Serializer.Serialize(traceStream, traces); traceStream.Position = 0;
        audit.ObserveEvents("/v1/traces", traceStream);
        var span = audit._entries.Single(e => e.Kind == "span");
        if (span.End - span.Start != 100 || span.Fields["trace_id"] != string.Concat(Enumerable.Repeat("01", 16)))
            throw new InvalidOperationException("trace ID 或 span 边界解析失败。");
        for (var i = 0; i < 150; i++) audit.Add(audit._entries[0]);
        if (audit._entries.Count(e => e.Kind == "log") > 64 || audit._omitted == 0) throw new InvalidOperationException("记录限量失败。");
        var modelAudit = new TelemetryCorrelationAudit(true);
        log.Attributes.Add(new() { Key = "gen_ai.response.model", Value = new() { Text = "test-model-2026" } });
        log.Attributes.Add(new() { Key = "gen_ai.request.model", Value = new() { Text = "test-model" } });
        log.Attributes.Add(new() { Key = "otel.name", Value = new() { Text = "server_model" } });
        var headers = new OtlpResource();
        headers.Attributes.Add(new() { Key = "openai-model", Value = new() { Text = "test-served-model" } });
        headers.Attributes.Add(new() { Key = "authorization", Value = new() { Text = secret } });
        log.Attributes.Add(new() { Key = "response.headers", Value = new() { Map = headers } });
        using var modelStream = new MemoryStream(); Serializer.Serialize(modelStream, packet); modelStream.Position = 0;
        modelAudit.ObserveEvents("/v1/logs", modelStream);
        var modelJson = JsonSerializer.Serialize(new { modelAudit._models, modelAudit._entries });
        if (!modelJson.Contains("test-model-2026") || !modelJson.Contains("gen_ai.request.model") || modelJson.Contains(secret) || modelJson.Contains(id))
            throw new InvalidOperationException("模型字段保留或标识脱敏失败。");
        if (!modelJson.Contains("test-served-model") || !modelJson.Contains("server_model") || !modelJson.Contains("id-1"))
            throw new InvalidOperationException("返回模型、事件类型或匿名关联未保留。");
    }

    private sealed record Entry(string Kind, string Name, ulong Start, ulong End, Dictionary<string, string> Fields);
    [ProtoContract] private sealed class Logs { [ProtoMember(1)] public List<LogResource> Resources { get; } = []; }
    [ProtoContract] private sealed class LogResource { [ProtoMember(1)] public OtlpResource? Resource { get; set; } [ProtoMember(2)] public List<LogScope> Scopes { get; } = []; }
    [ProtoContract] private sealed class LogScope { [ProtoMember(1)] public OtlpScope? Scope { get; set; } [ProtoMember(2)] public List<Log> Records { get; } = []; }
    [ProtoContract] private sealed class Log
    {
        [ProtoMember(1, DataFormat = DataFormat.FixedSize)] public ulong Time { get; set; }
        [ProtoMember(5)] public OtlpValue? Body { get; set; }
        [ProtoMember(6)] public List<OtlpAttribute> Attributes { get; } = [];
        [ProtoMember(9)] public byte[] TraceId { get; set; } = [];
        [ProtoMember(10)] public byte[] SpanId { get; set; } = [];
        [ProtoMember(11, DataFormat = DataFormat.FixedSize)] public ulong ObservedTime { get; set; }
        [ProtoMember(12)] public string EventName { get; set; } = "";
    }
    [ProtoContract] private sealed class Traces { [ProtoMember(1)] public List<TraceResource> Resources { get; } = []; }
    [ProtoContract] private sealed class TraceResource { [ProtoMember(1)] public OtlpResource? Resource { get; set; } [ProtoMember(2)] public List<TraceScope> Scopes { get; } = []; }
    [ProtoContract] private sealed class TraceScope { [ProtoMember(1)] public OtlpScope? Scope { get; set; } [ProtoMember(2)] public List<Span> Spans { get; } = []; }
    [ProtoContract] private sealed class Span
    {
        [ProtoMember(1)] public byte[] TraceId { get; set; } = [];
        [ProtoMember(2)] public byte[] SpanId { get; set; } = [];
        [ProtoMember(4)] public byte[] Parent { get; set; } = [];
        [ProtoMember(5)] public string Name { get; set; } = "";
        [ProtoMember(7, DataFormat = DataFormat.FixedSize)] public ulong Start { get; set; }
        [ProtoMember(8, DataFormat = DataFormat.FixedSize)] public ulong End { get; set; }
        [ProtoMember(9)] public List<OtlpAttribute> Attributes { get; } = [];
        [ProtoMember(11)] public List<SpanEvent> Events { get; } = [];
    }
    [ProtoContract] private sealed class SpanEvent
    {
        [ProtoMember(1, DataFormat = DataFormat.FixedSize)] public ulong Time { get; set; }
        [ProtoMember(2)] public string Name { get; set; } = "";
        [ProtoMember(3)] public List<OtlpAttribute> Attributes { get; } = [];
    }
}
