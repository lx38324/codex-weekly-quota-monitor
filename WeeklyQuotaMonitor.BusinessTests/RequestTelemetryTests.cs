using System.Net;
using System.Net.Http.Headers;
using ProtoBuf;
using WeeklyQuotaMonitor;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor.BusinessTests;

internal static class RequestTelemetryTests
{
    /// <summary>验证直接模式、唯一关联、晚到重试、冲突回滚、重启去重及保留期，使用独立临时数据库。</summary>
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    /// <summary>通过真实 HTTP logs 路径验证 protobuf 时间与后台关联，不接触生产数据。</summary>
    private static async Task RunAsync()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var conversation = Guid.NewGuid().ToString("D");
        var sample = new SpeedSample("rollout-" + conversation + ".jsonl", now.AddSeconds(-8), now, "gpt-6-astra", "standard", 100, 20);
        var request = new RequestLatency(conversation, now, sample.Model, 1234, null, 100, 20);
        var index = new RequestModeIndex([sample]);
        Check(index.Resolve(request) == ("standard", "rollout"), "Unique response did not resolve");
        Check(index.Resolve(request with { ReportedTier = "fast" }) == ("fast", "event"), "Event did not override local mode");
        Check(index.Resolve(request with { ReportedTier = "unknown" }) == ("unknown", "unknown-event"), "Unsupported event was guessed");
        Check(index.Resolve(request with { OutputTokens = 101 }).Tier == "unknown", "Token mismatch was ignored");
        Check(index.Resolve(request with { Conversation = Guid.NewGuid().ToString() }).Tier == "unknown", "Cross-session match");
        var ambiguous = new RequestModeIndex([sample, sample with { EndedAt = now.AddMilliseconds(200), ServiceTier = "fast" }]);
        Check(ambiguous.Resolve(request).Source == "ambiguous", "Ambiguous response was guessed");
        Check(new RequestModeIndex([sample with { ServiceTier = "unknown" }]).Resolve(request).Tier == "unknown", "Unknown became standard");
        var different = request with { Model = "gpt-5.6-luna" };
        Check(index.Associate(different) is { State: "model-mismatch", LocalModel: "gpt-6-astra", Basis: "time-tokens" }, "Cross-model evidence hidden");
        Check(index.Resolve(different).Tier == "unknown", "Different model inherited local tier");
        Check(index.Resolve(different with { ReportedTier = "fast" }) == ("fast", "event"), "Direct tier lost on mismatch");
        Check(index.Associate(different with { OutputTokens = null, ReasoningTokens = null }).Basis == "time-only", "Missing tokens overstated evidence");
        Check(new RequestModeIndex([sample, sample with { Model = different.Model }]).Associate(different).State == "ambiguous", "Same model biased candidate selection");
        Check(index.Associate(different with { Origin = "memory_extraction" }).State == "background", "Memory shared ID mistaken for local response");
        var directory = Path.Combine(Path.GetTempPath(), "quota-request-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "telemetry.sqlite");
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var options = new TelemetryOptions { Port = port, MaximumMegabytes = 16 };
        await using var service = new TelemetryService(path);
        await service.ConfigureAsync(options); Check(service.Listening, service.Error);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        Check(await Send(client, request) == HttpStatusCode.OK, service.Error);
        Check(Read(service, now).Single().Tier == "unknown", "Missing mode became standard");
        service.UpdateSpeedSamples([sample]);
        Check(await Send(client, request) == HttpStatusCode.OK, service.Error);
        Check(Read(service, now).Single() is { Count: 1, Tier: "standard", TierSource: "rollout", Mean: 1234 }, "Late response or dedup failed");
        service.UpdateSpeedSamples([sample, sample with { EndedAt = now.AddMilliseconds(200), ServiceTier = "fast" }]);
        await Send(client, request);
        Check(Read(service, now).Single().TierSource == "ambiguous", "New ambiguity did not revoke inferred mode");
        await Send(client, request with { ReportedTier = "fast" });
        Check(Read(service, now).Single() is { Tier: "fast", TierSource: "event", Count: 1 }, "Direct mode did not strengthen old record");
        await service.ConfigureAsync(options); await Send(client, request);
        Check(Read(service, now).Single() is { Tier: "fast", Count: 1 }, "Restart or weak duplicate lost direct mode");
        Check(await Send(client, request with { TtftMs = 222 }) == HttpStatusCode.BadRequest, "Conflicting retransmit accepted");
        Check(Read(service, now).Single().Mean == 1234, "Conflict changed existing latency");
        var store = new TelemetryStore(path, options);
        store.AppendRequests([request with { Time = now.AddDays(-400) }], index);
        store.Maintain(options, now);
        Check(store.Query(RequestTelemetry.Metric, null, now.AddDays(-500), now.AddMinutes(1), 5).Buckets.Sum(b => b.Count) == 1, "Request TTL failed");
        Check(service.Query(RequestTelemetry.Metric, null, now.AddMinutes(-1), now.AddMinutes(1), 5, "standard").Buckets.Count == 0, "Mode filter mixed requests");
        Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("PRIVATE_TEST_PROMPT"), "Private attribute persisted");
        var auditPath = Path.Combine(directory, "attribution.sqlite");
        // 旧版十列表真实迁移，不能靠新库测试替代用户已有历史。
        using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + auditPath))
        {
            legacy.Open(); using var cmd = legacy.CreateCommand();
            cmd.CommandText = "CREATE TABLE request_ttft(conversation TEXT NOT NULL,ticks INTEGER NOT NULL,model TEXT NOT NULL,end_ms INTEGER NOT NULL,ttft REAL NOT NULL,reported TEXT,output INTEGER,reasoning INTEGER,tier TEXT NOT NULL,source TEXT NOT NULL,PRIMARY KEY(conversation,ticks,model)); PRAGMA user_version=1;";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO request_ttft VALUES($c,$t,$m,$e,1234,'fast',100,20,'fast','event')";
            cmd.Parameters.AddWithValue("$c", conversation); cmd.Parameters.AddWithValue("$t", now.UtcTicks);
            cmd.Parameters.AddWithValue("$m", different.Model); cmd.Parameters.AddWithValue("$e", now.ToUnixTimeMilliseconds()); cmd.ExecuteNonQuery();
        }
        var audit = new TelemetryStore(auditPath, options);
        audit.ReconcileRequests(index);
        var migrated = audit.Query(RequestTelemetry.Metric, null, now.AddMinutes(-1), now.AddMinutes(1), 5, mismatchesOnly: true).Buckets.Single();
        Check(migrated is { Count: 1, Tier: "fast", ModelMismatches: 1, LocalModel: "gpt-6-astra" }, "Migration/direct-tier backfill lost evidence");
        audit.AppendRequests([different], new RequestModeIndex([]));
        Check(audit.Query(RequestTelemetry.Metric, null, now.AddMinutes(-1), now.AddMinutes(1), 5).Buckets.Single().ModelMismatches == 1, "Expired local index erased historical evidence");
        audit.AppendRequests([different with { Origin = "memory_extraction" }], index);
        Check(audit.Query(RequestTelemetry.Metric, null, now.AddMinutes(-1), now.AddMinutes(1), 5, origin: "memory_extraction").Buckets.Single() is { Association: "background", ModelMismatches: 0, Tier: "fast" }, "Origin strengthening failed");
        Check(audit.Query(RequestTelemetry.Metric, null, now.AddMinutes(-1), now.AddMinutes(1), 5, mismatchesOnly: true).Buckets.Count == 0, "Mismatch filter returned background work");
        Check(await Send(client, different with { Origin = "memory_consolidation" }) == HttpStatusCode.OK, "Explicit origin rejected");
        Check(Read(service, now).Single(b => b.Model == different.Model).Origin == "memory_consolidation", "HTTP origin not retained");
        Check(await Send(client, different with { Origin = "memory_extraction" }) == HttpStatusCode.BadRequest, "Conflicting explicit origins accepted");
    }

    /// <summary>构造缺少 OTLP 顶层时间、使用原始事件时间的真实白名单事件。</summary>
    internal static async Task<HttpStatusCode> Send(HttpClient client, RequestLatency request)
    {
        var record = new RequestLogRecord();
        foreach (var pair in new[] { ("event.name", "codex.sse_event"), ("event.kind", "response.completed"),
            ("conversation.id", request.Conversation), ("event.timestamp", request.Time.ToString("O")), ("model", request.Model),
            ("prompt", "PRIVATE_TEST_PROMPT_DO_NOT_STORE") })
            record.Attributes.Add(new() { Key = pair.Item1, Value = new() { Text = pair.Item2 } });
        record.Attributes.Add(new() { Key = "ttft_ms", Value = new() { Number = request.TtftMs } });
        if (request.ReportedTier is not null) record.Attributes.Add(new() { Key = "service_tier", Value = new() { Text = request.ReportedTier } });
        if (request.Origin != "unknown") record.Attributes.Add(new() { Key = "turn_trigger", Value = new() { Text = request.Origin } });
        if (request.OutputTokens is { } output) record.Attributes.Add(new() { Key = "output_token_count", Value = new() { Integer = output } });
        if (request.ReasoningTokens is { } reasoning) record.Attributes.Add(new() { Key = "reasoning_token_count", Value = new() { Integer = reasoning } });
        var scope = new RequestLogScope(); scope.Records.Add(record);
        var resource = new RequestLogResource(); resource.Scopes.Add(scope);
        var packet = new RequestLogPacket(); packet.Resources.Add(resource);
        using var stream = new MemoryStream(); Serializer.Serialize(stream, packet);
        using var body = new ByteArrayContent(stream.ToArray()); body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        using var response = await client.PostAsync("/v1/logs", body); return response.StatusCode;
    }

    /// <summary>固定测试窗口避免当前时间漂移改变计数口径。</summary>
    private static IReadOnlyList<TelemetryBucket> Read(TelemetryService service, DateTimeOffset now) =>
        service.Query(RequestTelemetry.Metric, null, now.AddMinutes(-1), now.AddMinutes(1), 5).Buckets;

    /// <summary>业务条件不满足时保留明确失败原因。</summary>
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
