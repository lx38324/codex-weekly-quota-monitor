using System.Text.Json;
using ProtoBuf;
using WeeklyQuotaMonitor;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor.BusinessTests;

internal static class Gpt62PricingTests
{
    /// <summary>验证两种新模型的官方价格、订阅速度倍率、配置迁移、旧摘要补读及遥测分组，不调用收费接口。</summary>
    public static void Run()
    {
        foreach (var (model, scale) in new[] { ("gpt-6.2-astra", 4m), ("gpt-6.2-sol", 1m) })
        {
            var boundary = PublicApiPricing.Calculate(model, "default", new(272000, 200000, 0, 1000, 0));
            Check(boundary.Success && !boundary.IsLongContext && boundary.CostUsd == 0.174m * scale,
                model + " 的 272K 边界价格错误");
            var usage = new TokenUsage(272001, 200000, 0, 1000, 0);
            var standard = PublicApiPricing.Calculate(model, "standard", usage);
            Check(standard.Success && standard.IsLongContext && standard.CostUsd == 0.174002m * scale &&
                standard.OfficialLongContextCostUsd == 0.343004m * scale, model + " 双口径计价错误");
            foreach (var (tier, multiplier) in new[] { ("fast", 2m), ("priority", 2m), ("ultrafast", 6m) })
            {
                // 订阅内含额度没有 API 的 3x 促销截止日，过去与未来均使用官方订阅倍率。
                foreach (var date in new[] { new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2027, 3, 2, 0, 0, 0, TimeSpan.Zero) })
                {
                    var priced = PublicApiPricing.Calculate(model, tier, usage, date);
                    Check(priced.Success && priced.CreditMultiplier == multiplier && priced.CostUsd == standard.CostUsd * multiplier &&
                        priced.OfficialLongContextCostUsd == standard.OfficialLongContextCostUsd * multiplier,
                        model + " 的 " + tier + " 误用了 API 价格或旧倍率");
                }
            }
            Check(PublicApiPricing.Calculate(model, "standard", new(100000, 80000, 1000, 1000, 0)).CostUsd == 0.0585m * scale,
                model + " API 缓存写分项错误");
            Check(!PublicApiPricing.Calculate(model, "unknown", usage).Success, "未知速度被推断成标准速度");
        }
        foreach (var model in new[] { "gpt-6-astra", "gpt-6.1-sol" })
            Check(PublicApiPricing.Calculate(model, "ultrafast", new(1000, 500, 0, 100, 0)) is
                { Success: true, CreditMultiplier: 8m, NormalizedServiceTier: "ultrafast" }, "旧模型 Ultrafast 未按订阅 8 倍计费");
        Check(!PublicApiPricing.Calculate("gpt-6.1-terra", "ultrafast", new(1000, 500, 0, 100, 0)).Success,
            "未公布 Ultrafast 的模型被猜价");
        VerifyMigrationAndRepricing();
        VerifyLogBackfill();
        VerifyTelemetry();
    }

    /// <summary>旧设置只补缺失新模型；可重算历史使用持久化明细，保留旧记录且重复重算不新增重复点。</summary>
    private static void VerifyMigrationAndRepricing()
    {
        var custom = new ModelPriceProfile("gpt-6.2-sol", new(7, 1, 0, 30), null, 272000);
        var settings = new AppSettings { SettingsSchemaVersion = 11, ModelPrices = PublicApiPricing.GetBuiltInProfiles()
            .Where(p => !p.Model.StartsWith("gpt-6.2-", StringComparison.Ordinal)).Append(custom).ToList() };
        Check(AppSettingsMigration.Apply(settings) && settings.ModelPrices.Single(p => p.Model == custom.Model) == custom &&
            settings.ModelPrices.Count(p => p.Model == "gpt-6.2-astra") == 1, "迁移覆盖自定义价格或未补入 Astra");
        Check(!AppSettingsMigration.Apply(settings), "重复迁移改变配置");
        ResponsePricingUsage[] details = [new("gpt-6.2-astra", "ultrafast", new(272001, 200000, 0, 1000, 0)),
            new("gpt-6.2-sol", "priority", new(272001, 200000, 0, 1000, 0))];
        var sample = new QuotaSample(DateTimeOffset.UtcNow, "codex", 10, 1, 999, 99900,
            details.Aggregate(TokenUsage.Zero, (sum, item) => sum.Add(item.Usage)), 2, "gpt-6.2-astra, gpt-6.2-sol")
            { PricingVersion = "previous", PricingUsages = details };
        var state = JsonSerializer.Deserialize<MonitorState>(JsonSerializer.Serialize(new MonitorState { Samples = [sample] }))!;
        var result = SampleRepricer.Apply(state, PublicApiPricing.BuiltInCatalog);
        var updated = state.Samples.Single(PublicApiPricing.IsCurrentSample);
        Check(result.Errors.Count == 0 && updated.IntervalApiEquivalentUsd == 4.524052m &&
            updated.PricingUsages.SequenceEqual(details) && state.Samples.Count == 2, "新模型混合速度历史重算错误或丢失证据");
        SampleRepricer.Apply(state, PublicApiPricing.BuiltInCatalog);
        Check(state.Samples.Count == 2, "重算产生重复样本");
    }

    /// <summary>用新版 Desktop 的 thread_settings_applied 结构验证实时额度、TPS 档位和已有未知速度摘要的补读。</summary>
    private static void VerifyLogBackfill()
    {
        var root = Path.Combine(Path.GetTempPath(), "quota-gpt62-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var time = DateTimeOffset.UtcNow.AddMinutes(-2);
        var path = Path.Combine(root, "rollout-" + Guid.NewGuid().ToString("D") + ".jsonl");
        object[] rows = [
            new { timestamp = time, type = "event_msg", payload = new { type = "task_started" } },
            new { timestamp = time, type = "event_msg", payload = new { type = "thread_settings_applied",
                thread_settings = new { model = "gpt-6.2-astra", service_tier = "ultrafast" } } },
            new { timestamp = time, type = "turn_context", payload = new { model = "gpt-6.2-astra" } },
            new { timestamp = time.AddSeconds(4), type = "response_item", payload = new { type = "reasoning" } },
            new { timestamp = time.AddSeconds(4.1), type = "event_msg", payload = new { type = "token_count",
                info = new { last_token_usage = new { input_tokens = 1000, cached_input_tokens = 500,
                    output_tokens = 100, reasoning_output_tokens = 20 } } } }
        ];
        File.WriteAllLines(path, rows.Select(row => JsonSerializer.Serialize(row)));
        var quota = new RolloutLogReader().ScanNew(root, new MonitorState { RolloutFilesPrimed = true });
        Check(quota.UnpricedModelResponses == 0 && quota.ApiEquivalentUsd == 0.0492m &&
            quota.ServiceTiers.Single() == "ultrafast", "新版日志的 Ultrafast 额度不可计价或倍率错误");
        var speed = SpeedMonitoring.Scan(new SpeedState(), [root], time.AddMinutes(2), 24);
        Check(speed.Samples.Single().ServiceTier == "ultrafast", "TPS 将明确的 Ultrafast 记为未知");
        speed.Samples[0] = speed.Samples[0] with { ServiceTier = "unknown" };
        speed.Cursors.Values.Single().EvidenceVersion = 1;
        var updated = SpeedMonitoring.Scan(speed, [root], time.AddMinutes(2), 24);
        Check(updated.Samples.Single().ServiceTier == "ultrafast" && speed.Samples.Single().ServiceTier == "unknown",
            "已有速度摘要未补读或原地改写旧缓存");
        Check(SpeedMonitoring.Scan(updated, [root], time.AddMinutes(2), 24).Samples.Count == 1, "再次补读制造重复速度点");
    }

    /// <summary>实际 protobuf 解码、SQLite 查询和唯一响应关联均保留 Ultrafast；不对无档位的服务端聚合桶猜测速度。</summary>
    private static void VerifyTelemetry()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var conversation = Guid.NewGuid().ToString("D");
        var speed = new SpeedSample("rollout-" + conversation + ".jsonl", now.AddSeconds(-4), now, "gpt-6.2-astra", "ultrafast", 100, 20);
        var request = new RequestLatency(conversation, now, speed.Model, 123, null, 100, 20);
        var index = new RequestModeIndex([speed]);
        Check(index.Resolve(request) == ("ultrafast", "rollout") &&
            index.Resolve(request with { ReportedTier = "ultrafast" }) == ("ultrafast", "event"), "请求 TTFT 丢失 Ultrafast 直报或关联");
        var point = new OtlpPoint { Start = (ulong)(now.AddSeconds(-10).UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100,
            End = (ulong)(now.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100, Count = 2, Sum = 100 };
        point.Bounds.Add(100); point.Counts.Add(2); point.Counts.Add(0);
        point.Attributes.Add(new() { Key = "model", Value = new() { Text = speed.Model } });
        point.Attributes.Add(new() { Key = "service_tier", Value = new() { Text = "ultrafast" } });
        var histogram = new OtlpHistogram { Temporality = 1 }; histogram.Points.Add(point);
        var scope = new OtlpScopeMetrics(); scope.Metrics.Add(new() { Name = TelemetryMetrics.Names[0], Unit = "ms", Histogram = histogram });
        var resource = new OtlpResourceMetrics(); resource.Scopes.Add(scope);
        var packet = new OtlpMetrics(); packet.Resources.Add(resource);
        using var stream = new MemoryStream(); Serializer.Serialize(stream, packet); stream.Position = 0;
        var decoded = TelemetryMetrics.Decode(stream).Single();
        Check(decoded.Tier == "ultrafast", "服务端直方图把 Ultrafast 转为未知");
        var directory = Path.Combine(Path.GetTempPath(), "quota-gpt62-otel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = new TelemetryStore(Path.Combine(directory, "telemetry.sqlite"), new TelemetryOptions());
        store.AppendRequests([request], index);
        Check(store.Query(RequestTelemetry.Metric, null, now.AddMinutes(-1), now.AddMinutes(1), 5, "ultrafast").Buckets.Single().Count == 1 &&
            store.Query(RequestTelemetry.Metric, null, now.AddMinutes(-1), now.AddMinutes(1), 5, "fast").Buckets.Count == 0,
            "Ultrafast 遥测筛选失败或混入 Fast");
        foreach (var language in new[] { UiLanguage.English, UiLanguage.ChineseSimplified })
        {
            UiText.SetLanguage(language);
            Check(UiText.SpeedTierName("ultrafast").Contains("Ultrafast"), "Ultrafast 本地化显示为未知");
        }
        UiText.SetLanguage(UiLanguage.ChineseSimplified);
    }

    /// <summary>业务验收失败时保留具体原因并终止发布。</summary>
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
