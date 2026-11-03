using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Net;
using System.Threading.Channels;
using ProtoBuf;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor;

/// <summary>仅绑定 IPv4 回环地址的 OTLP/HTTP 接收端；队列、连接、压缩后正文均有上限，不保存原始包。</summary>
public sealed class TelemetryService : IAsyncDisposable
{
    private const int MaxBody = 2 * 1024 * 1024;
    private readonly string _path;
    private readonly Action<OtlpMetrics>? _inspect;
    private readonly Action<string, Stream>? _inspectEvents;
    private WebApplication? _app;
    private TelemetryStore? _store;
    private Task? _worker;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _bodySlots = new(4, 4);
    private TelemetryOptions _options = new();
    private Channel<Pending>? _queue;
    private long _accepted, _failed, _ignored;
    private long _lastReceivedTicks;
    private long _revision;
    private long _bytes, _evicted;
    private string _error = "";
    private long _lastDiagnosticFailureTicks;
    private IReadOnlyList<SpeedSample> _speedSamples = Array.Empty<SpeedSample>();

    /// <summary>接收已扫描的响应摘要并唤醒后台关联；队列满时由下一包处理最新快照，不阻塞 UI。</summary>
    public void UpdateSpeedSamples(IReadOnlyList<SpeedSample> samples)
    {
        Interlocked.Exchange(ref _speedSamples, samples);
        _queue?.Writer.TryWrite(new("reconcile", [], new(TaskCreationOptions.RunContinuationsAsynchronously)));
    }
    public bool Listening => _app is not null && _worker?.IsFaulted != true;
    public long Revision => Interlocked.Read(ref _revision);
    public TelemetryOptions Options => _options;
    public long Accepted => Interlocked.Read(ref _accepted);
    public long Failed => Interlocked.Read(ref _failed);
    public long Ignored => Interlocked.Read(ref _ignored);
    public long Bytes => Interlocked.Read(ref _bytes);
    public long Evicted => Interlocked.Read(ref _evicted);
    public string Error => _error;
    public long Baselines => _store?.Baselines ?? 0;
    public long Duplicates => _store?.Duplicates ?? 0;
    public long Discontinuities => _store?.Discontinuities ?? 0;
    public DateTimeOffset? LastReceived => Interlocked.Read(ref _lastReceivedTicks) is var ticks && ticks != 0 ? new(ticks, TimeSpan.Zero) : null;

    /// <summary>保存数据路径及仅供显式诊断使用的回调；构造不占用端口，不改 Codex 配置。</summary>
    public TelemetryService(string path, Action<OtlpMetrics>? inspect = null, Action<string, Stream>? inspectEvents = null)
    { _path = path; _inspect = inspect; _inspectEvents = inspectEvents; }

    /// <summary>应用配置时顺序关闭旧接收端并启动新端口；启动失败保持明确错误，额度监控仍可独立运行。</summary>
    public async Task ConfigureAsync(TelemetryOptions options)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            _options = options;
            if (!options.IsValid()) throw new InvalidDataException("Invalid telemetry options");
            _error = "";
            if (!options.Enabled) { Interlocked.Increment(ref _revision); return; }
            _store = await Task.Run(() => new TelemetryStore(_path, options)).ConfigureAwait(false);
            var usage = _store.Usage(); Interlocked.Exchange(ref _bytes, usage.Bytes); Interlocked.Exchange(ref _evicted, usage.Evicted);
            _queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(8) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server =>
            {
                server.Listen(IPAddress.Loopback, options.Port);
                server.Limits.MaxRequestBodySize = MaxBody;
                server.Limits.MaxConcurrentConnections = 64;
                server.Limits.MaxRequestHeadersTotalSize = 16384;
                server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
                server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            });
            var app = builder.Build();
            app.MapPost("/v1/metrics", ReceiveAsync);
        // 日志仅解析请求延迟白名单；trace 不保存，正文不持久化。
            app.MapPost("/v1/logs", ReceiveAsync);
            app.MapPost("/v1/traces", _inspectEvents is null ? IgnoreAsync : ReceiveAsync);
            try { await app.StartAsync().ConfigureAwait(false); }
            catch { await app.DisposeAsync().ConfigureAwait(false); throw; }
            _app = app;
            _worker = Task.Run(ProcessAsync);
            Interlocked.Increment(ref _revision);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Net.Sockets.SocketException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            _error = e.ToString(); Interlocked.Increment(ref _failed); Interlocked.Increment(ref _revision);
            RuntimeLog.Write("OTel receiver failed: " + e);
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>处理不采集的日志/trace 管线，返回标准空 OTLP 响应并单独记录丢弃包数。</summary>
    private Task IgnoreAsync(HttpContext context)
    {
        Interlocked.Increment(ref _ignored);
        Interlocked.Increment(ref _revision);
        context.Response.ContentType = "application/x-protobuf";
        context.Response.StatusCode = 200;
        return Task.CompletedTask;
    }

    /// <summary>限定 protobuf/gzip 和解压后大小；排队失败返回 503，只有数据提交成功才返回成功响应。</summary>
    private async Task ReceiveAsync(HttpContext context)
    {
        Interlocked.Increment(ref _revision);
        if (context.Request.ContentType?.Split(';')[0].Trim() != "application/x-protobuf")
        { RejectHttp(context, 415, "unsupported content type"); return; }
        var encoding = context.Request.Headers.ContentEncoding.ToString();
        if (encoding is not ("" or "gzip")) { RejectHttp(context, 415, "unsupported content encoding"); return; }
        if (!_bodySlots.Wait(0)) { RejectHttp(context, 503, "body slots exhausted"); return; }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var unpacked = encoding == "gzip" ? new GZipStream(context.Request.Body, CompressionMode.Decompress, leaveOpen: true) : null;
            var input = (Stream?)unpacked ?? context.Request.Body;
            using var body = new MemoryStream(); var buffer = new byte[16384];
            int read;
            while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (body.Length + read > MaxBody) { RejectHttp(context, 413, "body exceeds limit"); return; }
                body.Write(buffer, 0, read);
            }
            var pending = new Pending(context.Request.Path.Value!, body.ToArray(), new(TaskCreationOptions.RunContinuationsAsynchronously));
            if (_queue is null || _worker?.IsFaulted == true || !_queue.Writer.TryWrite(pending))
            { RejectHttp(context, 503, "worker unavailable or queue full"); return; }
            var result = await pending.Completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            context.Response.StatusCode = result;
            context.Response.ContentType = "application/x-protobuf";
        }
        catch (OperationCanceledException e)
        {
            _error = e.ToString(); Interlocked.Increment(ref _failed);
            if (!context.RequestAborted.IsCancellationRequested) context.Response.StatusCode = 408;
            RecordRejection(context.Request.Path.Value!, e);
        }
        catch (InvalidDataException e)
        { _error = e.ToString(); Interlocked.Increment(ref _failed); context.Response.StatusCode = 400; RecordRejection(context.Request.Path.Value!, e); }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException e)
        { _error = e.ToString(); Interlocked.Increment(ref _failed); context.Response.StatusCode = e.StatusCode; RecordRejection(context.Request.Path.Value!, e); }
        finally { _bodySlots.Release(); }
    }

    /// <summary>单消费者执行差分、事务及限额维护；坏包返回 400，存储故障返回 503，保留原异常供诊断。</summary>
    private async Task ProcessAsync()
    {
        var queue = _queue!; var store = _store!;
        var maintained = DateTimeOffset.MinValue;
        IReadOnlyList<SpeedSample>? indexed = null;
        var index = new RequestModeIndex([]);
        await foreach (var pending in queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            IReadOnlyList<TelemetryDiagnosticEvent> received = [];
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (now - maintained > TimeSpan.FromMinutes(1)) { store.Maintain(_options, now); maintained = now; }
                var snapshot = Volatile.Read(ref _speedSamples);
                if (indexed is null || !indexed.SequenceEqual(snapshot))
                {
                    index = new RequestModeIndex(snapshot);
                    store.ReconcileRequests(index);
                    indexed = snapshot;
                }
                if (pending.Path == "reconcile") { pending.Completion.TrySetResult(200); continue; }
                using var body = new MemoryStream(pending.Body, writable: false);
                if (pending.Path != "/v1/metrics" && _inspectEvents is not null)
                {
                    _inspectEvents!(pending.Path, body);
                    Interlocked.Increment(ref _ignored); pending.Completion.TrySetResult(200); continue;
                }
                if (pending.Path == "/v1/logs")
                {
                    var requests = RequestTelemetry.Decode(body, now);
                    received = requests.Select(r => new TelemetryDiagnosticEvent("logs", RequestTelemetry.Metric, r.Model, "received", r.Time)).ToArray();
                    store.AppendRequests(requests, index, now);
                }
                else
                {
                    var points = TelemetryMetrics.Decode(body, packet =>
                    { received = InspectMetricDimensions(packet); _inspect?.Invoke(packet); });
                    store.Append(points, now, received);
                }
                var usage = store.Usage(); Interlocked.Exchange(ref _bytes, usage.Bytes); Interlocked.Exchange(ref _evicted, usage.Evicted);
                Interlocked.Increment(ref _accepted); Interlocked.Exchange(ref _lastReceivedTicks, now.UtcTicks);
                Interlocked.Increment(ref _revision);
                pending.Completion.TrySetResult(200);
            }
            catch (Exception e) when (e is InvalidDataException or ProtoException or EndOfStreamException or OverflowException)
            { _error = e.ToString(); Interlocked.Increment(ref _failed); RecordRejection(pending.Path, e, received); pending.Completion.TrySetResult(400); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                _error = e.ToString(); Interlocked.Increment(ref _failed); pending.Completion.TrySetResult(503);
                RecordRejection(pending.Path, e, received);
            }
            catch (Exception e)
            {
                // 未分类错误不能吞掉：让 worker 失败，后续请求明确返回 503，保留原始异常。
                _error = e.ToString(); Interlocked.Increment(ref _failed); RecordRejection(pending.Path, e, received); pending.Completion.TrySetResult(503); throw;
            }
            finally { Interlocked.Increment(ref _revision); }
        }
    }

    /// <summary>图表按需查询 SQLite；失败由调用者显式显示，不能返回伪空数据替代失败。</summary>
    public TelemetryQuery Query(string metric, string? model, DateTimeOffset start, DateTimeOffset end, int minutes, string? tier = null, string? origin = null, bool mismatchesOnly = false)
    {
        if (_store is not { } store) return new([], [], null, null, false);
        var data = store.Query(metric, model, start, end, minutes, tier, origin, mismatchesOnly);
        var diagnostics = store.QueryDiagnostics(metric, null, start, end);
        // 当前有请求但没有直方图的模型也须可选；只补维度，不能生成零观测。
        var models = data.Models.Concat(diagnostics.Where(d => d.RequestEvents > 0 && d.Model != "unknown").Select(d => d.Model))
            .Distinct().OrderBy(m => m).ToArray();
        return data with { Models = models, Diagnostics = diagnostics.Where(d => model is null || d.Model == model || d.Model == "unknown").ToArray() };
    }

    /// <summary>查询当前接收端的全部指标诊断明细；未初始化时明确报错，不将采集失败表示为空报告。</summary>
    public string DiagnosticReport(DateTimeOffset start, DateTimeOffset end) =>
        _store?.DiagnosticReport(start, end) ?? throw new InvalidDataException("Telemetry diagnostics are not initialized");

    /// <summary>解码筛选前统计实际收到的指标名、模型与无记录标记，未知指标也可追查；不保留资源属性或正文。</summary>
    private static IReadOnlyList<TelemetryDiagnosticEvent> InspectMetricDimensions(OtlpMetrics packet)
    {
        var result = new List<TelemetryDiagnosticEvent>();
        foreach (var resource in packet.Resources)
        foreach (var scope in resource.Scopes)
        foreach (var metric in scope.Metrics)
        {
            var points = metric.Histogram?.Points;
            var resourceModel = resource.Resource?.Attributes.LastOrDefault(a => a.Key == "model")?.Value?.Text ?? "unknown";
            if (points is null || points.Count == 0)
            {
                result.Add(new("metrics", metric.Name, resourceModel, "received", Reason: "no explicit histogram points"));
                if (!TelemetryMetrics.Names.Contains(metric.Name))
                    result.Add(new("metrics", metric.Name, resourceModel, "ignored", Reason: "metric not selected"));
            }
            if (points is not null) foreach (var point in points)
            {
                var model = point.Attributes.LastOrDefault(a => a.Key == "model")?.Value?.Text ?? resourceModel;
                DateTimeOffset? eventAt = point.End / 1_000_000 <= (ulong)DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)(point.End / 1_000_000)) : null;
                result.Add(new("metrics", metric.Name, model, "received", eventAt));
                // timing 事件计数不保证包含服务端数值；保留导出点数，仅用于确认传输证据，不能当作 TBT。
                if (metric.Name.Contains("websocket", StringComparison.OrdinalIgnoreCase) && point.Count > 0)
                {
                    var kind = point.Attributes.LastOrDefault(a => a.Key == "kind")?.Value?.Text ?? "unknown";
                    var safeKind = System.Text.RegularExpressions.Regex.IsMatch(kind, @"^[A-Za-z0-9_.-]{1,160}$") ? kind : "unknown";
                    var reason = $"kind={safeKind}; exported_count={point.Count}; temporality={metric.Histogram!.Temporality}";
                    result.Add(new("transport", "transport.websocket_event", model, "received", eventAt, Reason: reason));
                    if (safeKind == "responsesapi.websocket_timing")
                        result.Add(new("transport", "transport.websocket_timing", model, "received", eventAt, Reason: reason));
                }
                if (!TelemetryMetrics.Names.Contains(metric.Name) || (point.Flags & 1) != 0)
                    result.Add(new("metrics", metric.Name, model, "ignored", eventAt, Reason:
                        !TelemetryMetrics.Names.Contains(metric.Name) ? "metric not selected" : "no recorded value flag"));
                if (result.Count > 20000) throw new InvalidDataException("Diagnostic metric point limit exceeded");
            }
        }
        return result;
    }

    /// <summary>协议或背压拒绝也写入接收诊断，错误字符串不包含外部请求头值。</summary>
    private void RejectHttp(HttpContext context, int status, string reason)
    {
        context.Response.StatusCode = status; Interlocked.Increment(ref _failed);
        var error = new InvalidDataException($"HTTP {status}: {reason}"); _error = error.ToString();
        RecordRejection(context.Request.Path.Value!, error);
    }

    /// <summary>保存整包拒绝维度及原始失败原因；诊断数据库也失败时显式报出双重错误，并限频写运行日志。</summary>
    private void RecordRejection(string pipeline, Exception error, IReadOnlyList<TelemetryDiagnosticEvent>? received = null)
    {
        var events = received?.ToList() ?? [];
        var selected = events.Where(e => e.Outcome == "received" &&
            (TelemetryMetrics.Names.Contains(e.Metric) || e.Metric == RequestTelemetry.Metric)).ToArray();
        var reason = error.GetType().Name + ": " + error.Message;
        if (selected.Length == 0) events.Add(new(pipeline, "packet", "unknown", "rejected", Reason: reason));
        else events.AddRange(selected.Select(e => e with { Outcome = "rejected", Reason = reason }));
        try { _store?.RecordDiagnostics(events, DateTimeOffset.UtcNow); }
        catch (Exception diagnosticError) when (diagnosticError is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _error = error + "\nDiagnostic persistence failed: " + diagnosticError;
            var now = DateTimeOffset.UtcNow.UtcTicks;
            if (now - Interlocked.Read(ref _lastDiagnosticFailureTicks) > TimeSpan.TicksPerMinute)
            { Interlocked.Exchange(ref _lastDiagnosticFailureTicks, now); RuntimeLog.Write(_error); }
        }
    }

    /// <summary>停止接受新包，待已排队事务完成后释放端口；关闭托盘不会清空已保留摘要。</summary>
    private async Task StopAsync()
    {
        if (_app is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await _app.StopAsync(timeout.Token).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false); _app = null;
        }
        _queue?.Writer.TryComplete();
        if (_worker is not null) await _worker.ConfigureAwait(false);
        _queue = null; _worker = null;
    }

    /// <summary>顺序停止接收端，避免更新程序时文件或监听仍被占用。</summary>
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopAsync().ConfigureAwait(false); } finally { _lifecycle.Release(); }
    }
    private sealed record Pending(string Path, byte[] Body, TaskCompletionSource<int> Completion);
}
