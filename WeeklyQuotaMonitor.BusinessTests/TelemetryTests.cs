using System.Net;
using System.Net.Http.Headers;
using ProtoBuf;
using WeeklyQuotaMonitor;

namespace WeeklyQuotaMonitor.BusinessTests;

internal static class TelemetryTests
{
    /// <summary>验证真实 HTTP protobuf 接收、累计差分、重启去重、故障响应与限量持久化，不连接账号或读取对话。</summary>
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    /// <summary>所有状态写入临时目录；接收端绑定系统分配的测试端口，不占用生产端口。</summary>
    private static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quota-otel-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "telemetry.sqlite");
        var portProbe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        var options = new TelemetryOptions { Port = port, MaximumMegabytes = 16 };
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        await using var service = new TelemetryService(path);
        await service.ConfigureAsync(options);
        Check(service.Listening, service.Error);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
        var first = Packet(now, now.AddMinutes(1), 10, 1000);
        Check(await Send(client, first) == HttpStatusCode.OK, "First cumulative packet rejected");
        Check(Query(service, now).Buckets.Count == 0, "Initial cumulative history was attributed to now");
        var second = Packet(now, now.AddMinutes(2), 12, 1400);
        Check(await Send(client, second) == HttpStatusCode.OK, "Second packet rejected");
        var observed = Query(service, now).Buckets.Single();
        Check(observed.Count == 2 && observed.Mean == 200 && observed.Tier == "unknown", "Cumulative delta or missing tier is wrong");
        await Send(client, second); await Send(client, first);
        Check(Query(service, now).Buckets.Single().Count == 2, "Duplicate or out-of-order packet increased count");
        await service.ConfigureAsync(options);
        await Send(client, second);
        Check(Query(service, now).Buckets.Single().Count == 2, "Receiver restart lost deduplication state");
        var newEpoch = Packet(now.AddMinutes(2), now.AddMinutes(3), 1, 80);
        await Send(client, newEpoch);
        Check(Query(service, now).Buckets.Single().Count == 2, "Producer restart fabricated a delta");
        await Send(client, Packet(now.AddMinutes(2), now.AddMinutes(4), 2, 180));
        Check(Query(service, now).Buckets.Sum(b => b.Count) == 3, "Producer restart missed the next actual delta");
        using (var bad = new ByteArrayContent([255, 255, 255]))
        {
            bad.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
            using var response = await client.PostAsync("/v1/metrics", bad);
            Check(response.StatusCode == HttpStatusCode.BadRequest, "Malformed protobuf acknowledged as success");
        }
        using (var json = new StringContent("{}"))
        { using var response = await client.PostAsync("/v1/metrics", json); Check(response.StatusCode == HttpStatusCode.UnsupportedMediaType, "Unsupported encoding accepted"); }
        using (var large = new ByteArrayContent(new byte[2 * 1024 * 1024 + 1]))
        {
            large.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
            using var response = await client.PostAsync("/v1/metrics", large);
            Check(response.StatusCode == HttpStatusCode.RequestEntityTooLarge, "Oversized body accepted");
        }
        var diagnostic = Query(service, now).Diagnostics.Single(d => d.Model == "gpt-6-astra");
        Check(diagnostic.Received == 7 && diagnostic.Stored == 3 && diagnostic.Baselines == 2 && diagnostic.Duplicates == 3,
            "Diagnostics confused incoming cumulative points with committed observations");
        Check(Query(service, now).Diagnostics.Single(d => d.Model == "unknown").Rejected >= 3,
            "Protocol/protobuf rejections were not persisted");
        var flagged = Packet(now.AddMinutes(2), now.AddMinutes(4.1), 2, 180);
        flagged.Resources[0].Scopes[0].Metrics[0].Histogram!.Points[0].Flags = 1;
        Check(await Send(client, flagged) == HttpStatusCode.OK && Query(service, now).Buckets.Sum(b => b.Count) == 3,
            "No-recorded-value flag became a speed observation");
        Check(Query(service, now).Diagnostics.Single(d => d.Model == "gpt-6-astra").Ignored == 1,
            "No-recorded-value point disappeared without a diagnostic");
        var renamed = Packet(now.AddMinutes(2), now.AddMinutes(4.2), 2, 180);
        const string changedMetric = "codex.changed_metric.duration_ms";
        renamed.Resources[0].Scopes[0].Metrics[0].Name = changedMetric;
        Check(await Send(client, renamed) == HttpStatusCode.OK, "Unselected metric packet failed");
        var unselected = service.Query(changedMetric, null, now, now.AddMinutes(10), 5).Diagnostics.Single(d => d.Model == "gpt-6-astra");
        Check(unselected.Received == 1 && unselected.Ignored == 1 && unselected.Stored == 0,
            "Metric rename was invisible to diagnostics");
        var timing = Packet(now.AddMinutes(2), now.AddMinutes(4.25), 2, 180);
        timing.Resources[0].Scopes[0].Metrics[0].Name = "codex.websocket.event.duration_ms";
        timing.Resources[0].Scopes[0].Metrics[0].Histogram!.Points[0].Attributes.Add(new OtlpAttribute
        { Key = "kind", Value = new OtlpValue { Text = "responsesapi.websocket_timing" } });
        Check(await Send(client, timing) == HttpStatusCode.OK, "Transport diagnostic rejected valid metrics");
        Check(service.Query("transport.websocket_timing", null, now, now.AddMinutes(10), 5).Diagnostics
            .Single(d => d.Model == "gpt-6-astra").Received == 1,
            "WebSocket timing event was invisible to diagnostics");
        var invalidUnit = Packet(now.AddMinutes(2), now.AddMinutes(4.3), 3, 280);
        invalidUnit.Resources[0].Scopes[0].Metrics[0].Unit = "seconds";
        Check(await Send(client, invalidUnit) == HttpStatusCode.BadRequest && Query(service, now).Buckets.Sum(b => b.Count) == 3,
            "Invalid metric unit was acknowledged or committed");
        Check(Query(service, now).Diagnostics.Single(d => d.Model == "gpt-6-astra") is { Rejected: 1, Stored: 3 } rejected &&
            rejected.Reason.Contains("Unsupported unit"), "Metric rejection lacked model and original reason");
        using (var log = new StringContent("PRIVATE_TEST_PROMPT_DO_NOT_STORE"))
        { using var response = await client.PostAsync("/v1/traces", log); Check(response.IsSuccessStatusCode, "Explicit trace discard route failed"); }
        Check(service.Ignored == 1 && !System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("PRIVATE_TEST_PROMPT"), "Raw log body was stored");
        var sources = Enumerable.Range(0, 8).Select(_ => new HttpClient
        { BaseAddress = client.BaseAddress, Timeout = TimeSpan.FromSeconds(10) }).ToArray();
        try
        {
            foreach (var source in sources)
                Check(await Send(source, second) == HttpStatusCode.OK, "Persistent connections from other sources blocked metrics");
            Check(Query(service, now).Buckets.Sum(b => b.Count) == 3, "Multiple sources retransmitting a packet duplicated observations");
        }
        finally { foreach (var source in sources) source.Dispose(); }
        await using (var conflict = new TelemetryService(Path.Combine(directory, "conflict.sqlite")))
        {
            await conflict.ConfigureAsync(options);
            Check(!conflict.Listening && conflict.Error.Length > 0, "Port conflict reported success");
        }
        Check((await Send(client, Packet(now.AddMinutes(2), now.AddMinutes(4).AddSeconds(1), 3, 280))) == HttpStatusCode.OK,
            "Bad request or port conflict killed healthy receiver");
        await service.DisposeAsync();

        var store = new TelemetryStore(path, options);
        var old = now.AddDays(-10);
        store.Append([Point("old", old, old.AddSeconds(30), 2, 300, 1)], now);
        store.Maintain(options, now);
        var history = store.Query(TelemetryMetrics.Names[0], null, now.AddDays(-11), now.AddDays(-9), 5).Buckets.Single();
        Check(history.Count == 2 && history.Sum == 300 && history.ResolutionSeconds == 60, "Minute compaction changed statistics");
        store.Maintain(options, now.AddDays(25));
        history = store.Query(TelemetryMetrics.Names[0], null, now.AddDays(-11), now.AddDays(-9), 5).Buckets.Single();
        Check(history.Count == 2 && history.Sum == 300 && history.ResolutionSeconds == 3600, "Hourly compaction changed statistics");
        Check(store.Usage().Bytes < options.MaximumMegabytes * 1048576L, "Physical telemetry files exceeded budget");
        var tierStore = new TelemetryStore(Path.Combine(directory, "tiers.sqlite"), options);
        tierStore.Append([
            Point("standard", now, now.AddSeconds(10), 2, 600, 1) with { Tier = "standard" },
            Point("fast", now, now.AddSeconds(20), 4, 400, 1) with { Tier = "fast" },
            Point("unknown", now, now.AddSeconds(30), 1, 900, 1)
        ], now.AddMinutes(1));
        var fast = tierStore.Query(TelemetryMetrics.Names[0], "gpt-6-astra", now, now.AddMinutes(1), 5, "fast");
        Check(fast.Buckets.Single().Mean == 100 && fast.Buckets.Single().Count == 4 &&
            fast.First?.ToUnixTimeMilliseconds() == now.AddSeconds(20).ToUnixTimeMilliseconds() &&
            fast.Last?.ToUnixTimeMilliseconds() == now.AddSeconds(20).ToUnixTimeMilliseconds(), "Fast query mixed modes or navigation bounds");
        Check(tierStore.Query(TelemetryMetrics.Names[0], null, now, now.AddMinutes(1), 5, "standard").Buckets.Single().Mean == 300,
            "Standard query included unknown or fast observations");
        var empty = tierStore.Query(TelemetryMetrics.Names[0], "gpt-5.6-sol", now, now.AddMinutes(1), 5, "fast");
        Check(empty.Buckets.Count == 0 && empty.First is null && empty.Last is null, "Empty mode filter inherited another model's bounds");
        tierStore.Maintain(options, now.AddDays(10));
        Check(tierStore.Query(TelemetryMetrics.Names[0], null, now.AddMinutes(-1), now.AddMinutes(2), 5, "fast").Buckets.Single().Mean == 100,
            "Compaction merged Standard and Fast");
        // 生成足够多的可聚合数值摘要，容量维护必须可观测地淘汰，而不是只配置一个无效上限。
        var pressure = Enumerable.Range(0, 18000).Select(i => Point("pressure-" + i, now.AddSeconds(-2), now.AddSeconds(-1).AddTicks(i), 1, 20, 1) with
        { Model = new string('m', 140) + i }).ToArray();
        for (var i = 0; i < pressure.Length; i += 500)
        {
            try { store.Append(pressure.Skip(i).Take(500).ToArray(), now); }
            catch (Microsoft.Data.Sqlite.SqliteException e) when (e.SqliteErrorCode == 13) { break; }
            store.Maintain(options, now);
        }
        Check(store.Usage().Bytes <= options.MaximumMegabytes * 1048576L, "Disk cap exceeded under pressure");
    }

    /// <summary>只读查询 TTFT 测试时间段，避免把其它指标混入断言。</summary>
    private static TelemetryQuery Query(TelemetryService service, DateTimeOffset start) => service.Query(TelemetryMetrics.Names[0], null, start, start.AddMinutes(10), 5);

    /// <summary>构造标准 protobuf 显式累计直方图；模型存在但不伪造未提供的速度层级。</summary>
    private static OtlpMetrics Packet(DateTimeOffset start, DateTimeOffset end, ulong count, double sum)
    {
        var point = new OtlpPoint { Start = Nano(start), End = Nano(end), Count = count, Sum = sum };
        point.Bounds.Add(1000); point.Counts.Add(count); point.Counts.Add(0);
        point.Attributes.Add(new OtlpAttribute { Key = "model", Value = new OtlpValue { Text = "gpt-6-astra" } });
        var histogram = new OtlpHistogram { Temporality = 2 }; histogram.Points.Add(point);
        var metric = new OtlpMetric { Name = TelemetryMetrics.Names[0], Unit = "ms", Histogram = histogram };
        var scope = new OtlpScopeMetrics { Scope = new OtlpScope { Name = "codex" } }; scope.Metrics.Add(metric);
        var resource = new OtlpResourceMetrics(); resource.Scopes.Add(scope);
        var request = new OtlpMetrics(); request.Resources.Add(resource); return request;
    }

    /// <summary>向实际 Kestrel 端点发送 protobuf 并读取状态码，不绕过协议解析。</summary>
    private static async Task<HttpStatusCode> Send(HttpClient client, OtlpMetrics packet)
    {
        using var buffer = new MemoryStream(); Serializer.Serialize(buffer, packet);
        using var body = new ByteArrayContent(buffer.ToArray()); body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        using var response = await client.PostAsync("/v1/metrics", body).ConfigureAwait(false); return response.StatusCode;
    }

    /// <summary>生成不含正文的已解析观测，供存储保留期和容量测试。</summary>
    private static TelemetryPoint Point(string series, DateTimeOffset start, DateTimeOffset end, long count, double sum, int temporality) =>
        new(series, TelemetryMetrics.Names[0], "gpt-6-astra", "unknown", Nano(start), Nano(end), temporality, count, sum, [1000], [count, 0]);
    /// <summary>转换测试时间为 OTLP 纳秒时间戳。</summary>
    private static ulong Nano(DateTimeOffset time) => checked((ulong)(time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100);
    /// <summary>业务断言失败保留具体原因。</summary>
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    /// <summary>临时接收真实 Codex metrics，保存的是数值摘要；只输出计数、指标、均值，不输出原始属性或日志。</summary>
    public static void Capture(string directory)
    {
        var service = new TelemetryService(Path.Combine(directory, "telemetry.sqlite"));
        try
        {
            service.ConfigureAsync(new TelemetryOptions()).GetAwaiter().GetResult();
            Check(service.Listening, service.Error);
            for (var i = 0; i < 12; i++)
            {
                Thread.Sleep(10000);
                Console.WriteLine($"accepted={service.Accepted}; failed={service.Failed}; ignored={service.Ignored}; bytes={service.Bytes}; error={service.Error.Split('\n')[0]}");
                foreach (var metric in TelemetryMetrics.Names)
                {
                    var data = service.Query(metric, null, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, 1);
                    if (data.Buckets.Count > 0) Console.WriteLine($"{metric}: observations={data.Buckets.Sum(b => b.Count)}; models={string.Join(',', data.Models)}");
                }
            }
        }
        finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    /// <summary>使用合成直方图验证真实遥测页面的异步查询、模型筛选和高缩放布局，并可输出无私人数据的截图。</summary>
    public static void VerifyUi(string? output = null)
    {
        if (output is not null)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
        }
        // 与正式 Program.Main 相同：独立截图程序没有 Application.Run，须显式安装 UI 同步上下文。
        var previousContext = SynchronizationContext.Current;
        using var uiContext = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(uiContext);
        var directory = Path.Combine(Path.GetTempPath(), "quota-otel-ui-" + Guid.NewGuid().ToString("N"));
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var service = new TelemetryService(Path.Combine(directory, "telemetry.sqlite"));
        service.ConfigureAsync(new TelemetryOptions { Port = port }).GetAwaiter().GetResult();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var start = DateTimeOffset.UtcNow.AddHours(-6);
            double total = 0;
            for (var i = 1; i <= 24; i++)
            {
                total += 500 + i * 20 + Math.Sin(i) * 100;
                var packet = Packet(start, start.AddMinutes(i * 14), (ulong)i, total);
                Check(Send(client, packet).GetAwaiter().GetResult() == HttpStatusCode.OK, "UI sample rejected");
                var point = packet.Resources[0].Scopes[0].Metrics[0].Histogram!.Points[0];
                point.Attributes.Add(new OtlpAttribute { Key = "service_tier", Value = new OtlpValue { Text = "priority" } });
                point.Sum = total * 0.5;
                Check(Send(client, packet).GetAwaiter().GetResult() == HttpStatusCode.OK, "Fast model rejected");
                point.Attributes[^1].Value!.Text = "default"; point.Sum = total * 1.2;
                Check(Send(client, packet).GetAwaiter().GetResult() == HttpStatusCode.OK, "Standard model rejected");
                point.Attributes.RemoveAt(point.Attributes.Count - 1);
                point.Attributes[0].Value!.Text = "gpt-5.6-sol"; point.Sum = total * 0.8;
                Check(Send(client, packet).GetAwaiter().GetResult() == HttpStatusCode.OK, "Second model rejected");
            }
            using var form = new ChartForm(new AppSettings(), _ => { }, service);
            form.ShowSpeedTab();
            var panel = Find<TelemetryPanel>(form).Single();
            var tabs = Find<TabControl>(form).Single(t => t.TabPages.Cast<TabPage>().Any(p => p.Text == "OTel · TTFT"));
            tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(p => p.Text == "OTel · TTFT");
            form.Scale(new SizeF(1.5f, 1.5f)); form.ClientSize = new Size(1260, 820);
            var grid = panel.Controls.Find("TelemetryGrid", true).OfType<DataGridView>().Single();
            var range = panel.Controls.Find("TelemetryRange", true).OfType<ComboBox>().Single();
            Check(range.Text == UiText.Get("TimeRange4Hours"), "OTel default is not four hours");
            // 本夹具刻意覆盖六小时：显式选择保留数据，随后仍验证全部模型/模式筛选。
            range.SelectedIndex = 0;
            PumpUntil(() => grid.Rows.Count == 92);
            var chart = Find<SpeedChartControl>(panel).Single();
            Check(chart.DisplayedSeriesCount == 4, "OTel model/mode series were merged");
            Check(chart.Height >= 180 && chart.Width >= 300, "High-DPI TTFT chart was squeezed out");
            foreach (var card in Find<MetricCard>(panel))
            {
                Check(card.Height >= card.GetPreferredSize(new Size(card.Width, 0)).Height,
                    "High-DPI TTFT card is shorter than its wrapped text");
                foreach (var label in Find<Label>(card))
                {
                    var bounds = card.RectangleToClient(label.RectangleToScreen(label.ClientRectangle));
                    Check(card.ClientRectangle.Contains(bounds), "High-DPI TTFT card clipped its caption");
                }
            }
            if (output is not null)
            {
                using var overview = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(overview, new Rectangle(System.Drawing.Point.Empty, form.Size));
                overview.Save(Path.ChangeExtension(output, ".series.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            var filter = panel.Controls.Find("TelemetryModel", true).OfType<ComboBox>().Single();
            filter.SelectedItem = "gpt-6-astra"; PumpUntil(() => chart.DisplayedSeriesCount == 3 && grid.Rows.Count == 69);
            var tier = panel.Controls.Find("TelemetryTier", true).OfType<ComboBox>().Single();
            tier.SelectedIndex = 2; PumpUntil(() => chart.DisplayedSeriesCount == 1 && grid.Rows.Count == 23 &&
                grid.Rows.Cast<DataGridViewRow>().All(row => Equals(row.Cells["SpeedTier"].Value, UiText.SpeedTierName("fast"))));
            var expectedMean = (total - (520 + Math.Sin(1) * 100)) / 23 * 0.5;
            Check(Find<MetricCard>(panel).Any(card => card.AccessibleName!.StartsWith(UiText.Get("OtelMean") + ": " + expectedMean.ToString("N2"))), "Fast TTFT card mixed speed modes");
            panel.ApplyLanguage(); Check(tier.SelectedIndex == 2, "Language refresh lost selected mode");
            tier.SelectedIndex = 1; PumpUntil(() => grid.Rows.Count == 23 &&
                grid.Rows.Cast<DataGridViewRow>().All(row => Equals(row.Cells["SpeedTier"].Value, UiText.SpeedTierName("standard"))));
            tier.SelectedIndex = 3; PumpUntil(() => grid.Rows.Count == 23 &&
                grid.Rows.Cast<DataGridViewRow>().All(row => Equals(row.Cells["SpeedTier"].Value, UiText.SpeedTierName("unknown"))));
            filter.SelectedItem = "gpt-5.6-sol"; tier.SelectedIndex = 2;
            PumpUntil(() => grid.Rows.Count == 0 && chart.DisplayedSeriesCount == 0);
            filter.SelectedIndex = 0; tier.SelectedIndex = 0;
            PumpUntil(() => chart.DisplayedSeriesCount == 4 && grid.Rows.Count == 92);
            var conversation = Guid.NewGuid().ToString("D");
            var local = new List<WeeklyQuotaMonitor.Core.SpeedSample>();
            for (var i = 1; i <= 24; i++)
            {
                var time = start.AddMinutes(i * 14);
                local.Add(new("rollout-" + conversation + ".jsonl", time.AddSeconds(-10), time, "gpt-6-astra", "standard", 100, 20));
                foreach (var (modelName, mode, offset) in new[] { ("gpt-6-astra", (string?)null, 0), ("gpt-6-astra", "fast", 5), ("gpt-5.6-sol", (string?)null, 10) })
                    Check(RequestTelemetryTests.Send(client, new(conversation, time.AddSeconds(offset), modelName, 500 + i * 20 + offset * 25,
                        mode, 100, 20)).GetAwaiter().GetResult() == HttpStatusCode.OK, "Request UI sample rejected");
            }
            service.UpdateSpeedSamples(local);
            var metricSelector = panel.Controls.Find("TelemetryMetric", true).OfType<ComboBox>().Single();
            metricSelector.SelectedIndex = TelemetryMetrics.Names.Length;
            PumpUntil(() => grid.Rows.Count == 72 && chart.DisplayedSeriesCount == 3);
            tier.SelectedIndex = 1;
            PumpUntil(() => grid.Rows.Count == 24 && grid.Rows.Cast<DataGridViewRow>().All(row =>
                Equals(row.Cells["OtelTierSource"].Value, UiText.Get("OtelSource_rollout"))));
            Check(Find<MetricCard>(panel).Any(card => card.AccessibleName!.StartsWith(UiText.Get("OtelMean") + ": " + 750d.ToString("N2"))), "Request mean mixed modes");
            tier.SelectedIndex = 2;
            PumpUntil(() => grid.Rows.Count == 24 && grid.Rows.Cast<DataGridViewRow>().All(row =>
                Equals(row.Cells["OtelTierSource"].Value, UiText.Get("OtelSource_event"))));
            tier.SelectedIndex = 0; PumpUntil(() => grid.Rows.Count == 72 && chart.DisplayedSeriesCount == 3);
            var mismatchTime = start.AddMinutes(14);
            Check(RequestTelemetryTests.Send(client, new(conversation, mismatchTime, "gpt-5.6-luna", 321,
                "fast", 100, 20)).GetAwaiter().GetResult() == HttpStatusCode.OK, "Mismatch UI sample rejected");
            var mismatches = panel.Controls.Find("TelemetryMismatches", true).OfType<CheckBox>().Single();
            mismatches.Checked = true;
            PumpUntil(() => grid.Rows.Count == 1 && chart.DisplayedSeriesCount == 1);
            Check(Equals(grid.Rows[0].Cells["OtelLocalModel"].Value, "gpt-6-astra") &&
                Equals(grid.Rows[0].Cells["OtelMismatchCount"].Value, 1L), "UI concealed local-model mismatch");
            Check(Find<MetricCard>(panel).Any(card => card.AccessibleName!.StartsWith(UiText.Get("OtelMean") + ": " + 321d.ToString("N2"))), "Mismatch card did not share filtered query");
            if (output is not null)
            {
                Application.DoEvents(); form.Refresh(); Application.DoEvents();
                Check(grid.Visible && grid.IsHandleCreated && grid.Height >= 100, "Telemetry table is not visible");
                using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(System.Drawing.Point.Empty, form.Size));
                bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                using var gridImage = new Bitmap(grid.Width, grid.Height);
                grid.DrawToBitmap(gridImage, new Rectangle(System.Drawing.Point.Empty, grid.Size));
                gridImage.Save(Path.ChangeExtension(output, ".grid.png"), System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine($"grid={grid.Bounds}; columns={grid.Columns.Count}; rowHeight={grid.Rows[0].Height}; header={grid.ColumnHeadersHeight}; parent={grid.Parent!.Size}");
            }
            // 在最后一次查询在途时销毁窗口：查询线程只能访问数据库，不得向已销毁的句柄回调。
            filter.SelectedItem = "gpt-6-astra";
        }
        finally
        {
            service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    /// <summary>验证旧TBT存在但近窗只有请求与WS timing时，不跳回旧历史；缺指标模型可选，原因区分传输证据，真正TBT随后到达即时显示。</summary>
    public static void VerifyMissingServiceTiming()
    {
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(null);
        var directory = Path.Combine(Path.GetTempPath(), "quota-missing-tbt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var portProbe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        var path = Path.Combine(directory, "telemetry.sqlite");
        var now = DateTimeOffset.UtcNow;
        var options = new TelemetryOptions { Port = port };
        new TelemetryStore(path, options).Append([Point("old-tbt", now.AddDays(-2), now.AddDays(-2).AddMinutes(1), 2, 100, 1)
            with { Metric = TelemetryMetrics.Names[3] }], now.AddDays(-2).AddMinutes(1));
        var service = new TelemetryService(path);
        service.ConfigureAsync(options).GetAwaiter().GetResult();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var timing = Packet(now.AddMinutes(-2), now.AddMinutes(-1), 2, 50);
            var metric = timing.Resources[0].Scopes[0].Metrics[0];
            metric.Name = "codex.websocket.event.duration_ms";
            metric.Histogram!.Points[0].Attributes.Add(new OtlpAttribute { Key = "kind", Value = new OtlpValue { Text = "responsesapi.websocket_timing" } });
            Check(Send(client, timing).GetAwaiter().GetResult() == HttpStatusCode.OK, "Timing rejected");
            var conversation = Guid.NewGuid().ToString("D");
            foreach (var model in new[] { "gpt-6-astra", "gpt-6.1-sol" })
                Check(RequestTelemetryTests.Send(client, new(conversation, now.AddMinutes(-1), model, 500, "standard", 100, 20)).GetAwaiter().GetResult() == HttpStatusCode.OK, "Request rejected");
            var absent = service.Query(TelemetryMetrics.Names[3], null, now.AddHours(-4), now, 5);
            Check(absent.Buckets.Count == 0 && absent.Models.Contains("gpt-6.1-sol"), "Missing model hidden or fake TBT generated");
            Check(absent.Diagnostics.Single(d => d.Model == "gpt-6-astra") is { Received: 0, RequestEvents: 1, WebSocketTimingPoints: 1 }, "Timing evidence misclassified as TBT");
            using var form = new Form { Size = new Size(1250, 850) };
            using var panel = new TelemetryPanel(service, 8 * 60); form.Controls.Add(panel); form.Show();
            var selector = panel.Controls.Find("TelemetryMetric", true).OfType<ComboBox>().Single(); selector.SelectedIndex = 3;
            var status = panel.Controls.Find("TelemetryStatus", true).OfType<Label>().Single();
            PumpUntil(() => status.Text.Contains(UiText.Get("OtelServerMetricNotExported")) && status.Text.Contains(UiText.Get("OtelServerTransportUnconfirmed")));
            var chart = Find<SpeedChartControl>(panel).Single();
            Check(chart.Navigation.Window!.Value.Start > now.AddHours(-4).AddSeconds(-10) && chart.DisplayedSeriesCount == 0,
                "Empty current window jumped to old TBT");
            var models = panel.Controls.Find("TelemetryModel", true).OfType<ComboBox>().Single();
            Check(models.Items.Contains("gpt-6.1-sol"), "Request-only model unavailable for filtering");
            var empty = panel.Controls.Find("TelemetryEmptyState", true).OfType<Label>().Single();
            var recover = panel.Controls.Find("TelemetryUseRequestTtft", true).OfType<LinkLabel>().Single();
            Check(empty.Visible && empty.Text == UiText.Get("UxOtelRequestsOnly") && recover.Visible,
                "有请求但缺服务端指标时没有解释及可操作恢复入口");
            typeof(Control).Assembly.GetType("System.Windows.Forms.LinkLabel")!
                .GetMethod("OnLinkClicked", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(recover, [new LinkLabelLinkClickedEventArgs(new LinkLabel.Link())]);
            PumpUntil(() => chart.DisplayedSeriesCount == 2);
            selector.SelectedIndex = 3;
            PumpUntil(() => chart.DisplayedSeriesCount == 0);
            var range = panel.Controls.Find("TelemetryRange", true).OfType<ComboBox>().Single(); range.SelectedIndex = 2;
            PumpUntil(() => chart.Navigation.Window!.Value.Start == ChartTimeWindow.CurrentDay(DateTimeOffset.UtcNow, 480).Start);
            range.SelectedIndex = 1;
            var fresh = Packet(now.AddMinutes(-1), now, 4, 80);
            fresh.Resources[0].Scopes[0].Metrics[0].Name = TelemetryMetrics.Names[3];
            fresh.Resources[0].Scopes[0].Metrics[0].Histogram!.Temporality = 1;
            Check(Send(client, fresh).GetAwaiter().GetResult() == HttpStatusCode.OK, "Real service TBT rejected");
            PumpUntil(() => chart.DisplayedSeriesCount == 1);
            Check(Find<MetricCard>(panel).Any(c => c.AccessibleName!.StartsWith(UiText.Get("OtelMean") + ": " + 20d.ToString("N2"))), "Actual service TBT not displayed");
        }
        finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); SynchronizationContext.SetSynchronizationContext(previous); }
    }

    /// <summary>只等待已知 UI 业务条件，超时输出失败而不是把未刷新界面当作成功。</summary>
    private static void PumpUntil(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ready() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(15); }
        Check(ready(), "Telemetry UI did not reach the expected observation/model state");
    }

    /// <summary>遍历当前窗口控件以检查业务图表，不依赖控件排列顺序。</summary>
    private static IEnumerable<T> Find<T>(Control root) where T : Control => root.Controls.Cast<Control>().SelectMany(c =>
        (c is T match ? new[] { match } : Enumerable.Empty<T>()).Concat(Find<T>(c)));
}
