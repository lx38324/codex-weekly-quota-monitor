using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ProtoBuf;
using WeeklyQuotaMonitor;

namespace WeeklyQuotaMonitor.BusinessTests;

/// <summary>一次性 OTLP 属性诊断：仅保留字段名及白名单模式值，不保存正文、标识符或原始请求。</summary>
internal sealed class TelemetryAttributeAudit
{
    private const int MaximumPackets = 40, MaximumFields = 256, MaximumVisits = 8192;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _duration;
    private readonly Dictionary<string, Field> _fields = [];
    private readonly Dictionary<string, int> _metrics = [];
    private int _packets, _points, _omitted;
    public int Packets => Volatile.Read(ref _packets);
    public bool Finished => _clock.Elapsed >= _duration || Packets >= MaximumPackets;

    /// <summary>诊断必须显式设置不超过九十秒的期限；零期限仅供验证立即停止的边界。</summary>
    public TelemetryAttributeAudit(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero || duration > TimeSpan.FromSeconds(90)) throw new ArgumentOutOfRangeException(nameof(duration));
        _duration = duration;
    }

    /// <summary>在生产解析器归一化前检查三层属性及嵌套映射；按包、字段数和遍历量停止额外采集。</summary>
    public void Observe(OtlpMetrics request)
    {
        if (Finished) return;
        Interlocked.Increment(ref _packets);
        var budget = MaximumVisits;
        foreach (var resource in request.Resources)
        foreach (var scope in resource.Scopes)
        foreach (var metric in scope.Metrics.Where(m => TelemetryMetrics.Names.Contains(m.Name)))
        {
            Visit(metric.Name, "resource", resource.Resource?.Attributes ?? [], 0, ref budget);
            Visit(metric.Name, "scope", scope.Scope?.Attributes ?? [], 0, ref budget);
            foreach (var point in metric.Histogram?.Points ?? [])
            {
                if (--budget < 0 || Finished && _clock.Elapsed >= _duration) { _omitted++; return; }
                _points++;
                _metrics[metric.Name] = _metrics.GetValueOrDefault(metric.Name) + 1;
                Visit(metric.Name, "point", point.Attributes, 0, ref budget);
            }
        }
    }

    /// <summary>只记录语法受限的键；任意字符串值均丢弃，模式字段也只保留固定枚举或布尔值。</summary>
    private void Visit(string metric, string location, IEnumerable<OtlpAttribute> attributes, int depth, ref int budget)
    {
        foreach (var attribute in attributes)
        {
            if (--budget < 0 || _clock.Elapsed >= _duration) { _omitted++; return; }
            var key = attribute.Key;
            if (key.Length is < 1 or > 96 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            { _omitted++; continue; }
            var identity = metric + "|" + location + "|" + key;
            if (!_fields.TryGetValue(identity, out var field))
            {
                if (_fields.Count >= MaximumFields) { _omitted++; continue; }
                field = new(metric, location, key); _fields.Add(identity, field);
            }
            field.Occurrences++;
            var modeKey = key.Replace("_", "").Replace(".", "").Replace("-", "").ToLowerInvariant();
            if (modeKey is "servicetier" or "speed" or "speedmode" or "fastmode" or "isfast" or
                "genairequestservicetier" or "genairesponseservicetier" or "reasoningeffort")
            {
                var value = attribute.Value?.Text;
                if (value is "default" or "standard" or "priority" or "fast" or "auto" or "flex" or "unknown" or
                    "low" or "medium" or "high" or "xhigh" or "max" or "ultra" or "minimal" or "none") field.ModeValues.Add(value);
                else if (attribute.Value?.Boolean is { } boolean) field.ModeValues.Add(boolean ? "true" : "false");
                else field.ModeValues.Add("<unrecognized-value-redacted>");
            }
            if (depth < 3 && attribute.Value?.Map is { } map) Visit(metric, location + ".map", map.Attributes, depth + 1, ref budget);
            if (depth < 3 && attribute.Value?.Array is { } array)
                foreach (var item in array.Values.Take(16))
                    if (item.Map is { } nested) Visit(metric, location + ".array-map", nested.Attributes, depth + 1, ref budget);
        }
    }

    /// <summary>在接收端停止后生成限量摘要；不把未观测到字段解释为所有 Codex 版本均不支持。</summary>
    private string Report() => JsonSerializer.Serialize(new
    {
        CapturedAtUtc = DateTimeOffset.UtcNow, ElapsedSeconds = Math.Round(_clock.Elapsed.TotalSeconds, 2),
        Packets, HistogramPoints = _points, MaximumPackets, MaximumSeconds = _duration.TotalSeconds,
        StopReason = Packets >= MaximumPackets ? "packet-limit" : "time-limit", Omitted = _omitted,
        Metrics = _metrics, Fields = _fields.Values.OrderBy(f => f.Metric).ThenBy(f => f.Location).ThenBy(f => f.Key),
        Privacy = "Attribute keys and allowlisted mode enum values only. No prompt, identifiers, tool output, raw logs, traces or packet bytes."
    }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>临时使用正式数值库接收既有输出，不改 Codex 配置；九十秒后关闭接收端并保存单份摘要。</summary>
    public static void Capture(string dataDirectory, string output)
    {
        if (File.Exists(output)) throw new IOException("诊断输出已存在，拒绝覆盖。");
        var options = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(dataDirectory, "settings.json")))!.Telemetry;
        if (!options.Enabled) throw new InvalidOperationException("遥测未启用，诊断不得代替用户开启。");
        var audit = new TelemetryAttributeAudit(TimeSpan.FromSeconds(90));
        var service = new TelemetryService(Path.Combine(dataDirectory, "telemetry.sqlite"), audit.Observe);
        try
        {
            service.ConfigureAsync(options).GetAwaiter().GetResult();
            if (!service.Listening) throw new InvalidOperationException(service.Error);
            Console.WriteLine("诊断接收端已启动：最多 90 秒 / 40 个指标包；不记录原始值。");
            while (!audit.Finished)
            {
                Thread.Sleep(1000);
                if (!service.Listening || service.Failed > 0) throw new InvalidOperationException("诊断接收失败：" + service.Error);
            }
        }
        finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        var report = audit.Report();
        if (Encoding.UTF8.GetByteCount(report) > 128 * 1024) throw new InvalidDataException("诊断摘要超出 128 KiB 限额。");
        File.WriteAllText(output, report, new UTF8Encoding(false));
        Console.WriteLine($"诊断完成：packets={audit.Packets}; points={audit._points}; fields={audit._fields.Count}; omitted={audit._omitted}");
        if (audit._points == 0) throw new InvalidOperationException("未收到目标指标，不能得出字段缺失的结论。");
    }

    /// <summary>经实际 protobuf 解码验证三层标记可见、正文及伪装模式值被丢弃，过期与满额均停止采集。</summary>
    public static void Verify()
    {
        const string secret = "PRIVATE_TEST_PROMPT_AND_TOKEN";
        var request = new OtlpMetrics(); var resource = new OtlpResourceMetrics { Resource = new() };
        var scope = new OtlpScopeMetrics { Scope = new() }; var metric = new OtlpMetric { Name = TelemetryMetrics.Names[0], Unit = "ms", Histogram = new() { Temporality = 1 } };
        request.Resources.Add(resource); resource.Scopes.Add(scope); scope.Metrics.Add(metric);
        resource.Resource.Attributes.Add(new() { Key = "service_tier", Value = new() { Text = "priority" } });
        scope.Scope.Attributes.Add(new() { Key = "speed_mode", Value = new() { Text = "fast" } });
        var point = new OtlpPoint { Start = 1_000_000, End = 2_000_000, Count = 1, Sum = 1 };
        point.Counts.Add(1); metric.Histogram.Points.Add(point);
        point.Attributes.Add(new() { Key = "prompt", Value = new() { Text = secret } });
        point.Attributes.Add(new() { Key = "service_tier", Value = new() { Text = secret } });
        var audit = new TelemetryAttributeAudit(TimeSpan.FromSeconds(90));
        using var buffer = new MemoryStream(); Serializer.Serialize(buffer, request); buffer.Position = 0;
        TelemetryMetrics.Decode(buffer, audit.Observe);
        var report = audit.Report();
        if (report.Contains(secret) || !report.Contains("priority") || !report.Contains("fast") || !report.Contains("prompt"))
            throw new InvalidOperationException("诊断隐私或三层字段采集错误。");
        for (var i = 0; i < 60; i++) audit.Observe(request);
        if (audit.Packets != MaximumPackets) throw new InvalidOperationException("包数上限未生效。");
        var expired = new TelemetryAttributeAudit(TimeSpan.Zero); expired.Observe(request);
        if (expired.Packets != 0) throw new InvalidOperationException("超时后仍在采集。");
    }

    private sealed class Field(string metric, string location, string key)
    {
        public string Metric { get; } = metric;
        public string Location { get; } = location;
        public string Key { get; } = key;
        public int Occurrences { get; set; }
        public HashSet<string> ModeValues { get; } = [];
    }
}
