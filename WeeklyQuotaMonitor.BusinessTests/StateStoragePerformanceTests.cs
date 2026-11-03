using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor.BusinessTests;

/// <summary>验证有界内存持久化、快照隔离及原子替换失败路径；基准仅写独立目录。</summary>
internal static class StateStoragePerformanceTests
{
    private static readonly JsonSerializerOptions LegacyOptions = new()
    { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>在保留逐响应重算依据的前提下验证紧凑文件回读、备份及写入失败不破坏原状态。</summary>
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "QuotaMonitor.Storage." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "state.json");
            var state = new MonitorState
            {
                FileCursors = new(StringComparer.OrdinalIgnoreCase) { ["TEST"] = new() { Offset = 100 } },
                PendingModels = new(StringComparer.OrdinalIgnoreCase) { "sol" },
                PendingPricingUsages = [new("sol", "fast", new(100, 50, 0, 30, 10))],
                Samples = [new(DateTimeOffset.Now, "codex", 10, 1, 5, 500, TokenUsage.Zero, 1, "sol")
                    { PricingUsages = Enumerable.Range(0, 20000).Select(i => new ResponsePricingUsage("gpt-6-sol", "standard",
                        new(i + 100, 50, 0, 30, 10)) { Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(i) }).ToArray() }]
            };
            state.PendingOfficialLongContextIssues.Add("长上下文对比缺价，不影响基础金额。");
            var copy = state.CopyForPersistence();
            state.FileCursors["TEST"].Offset = 900;
            state.PendingModels.Clear(); state.PendingPricingUsages.Clear();
            state.PendingOfficialLongContextIssues.Clear();
            ((ResponsePricingUsage[])state.Samples[0].PricingUsages)[0] = new("changed", "fast", TokenUsage.Zero);
            if (copy.FileCursors["test"].Offset != 100 || !copy.PendingModels.Contains("SOL") ||
                copy.PendingPricingUsages.Count != 1 || copy.Samples[0].PricingUsages[0].Model != "gpt-6-sol" ||
                copy.PendingOfficialLongContextIssues.Count != 1)
                throw new InvalidOperationException("后台快照受可变游标或集合的后续修改污染。");
            var allocated = GC.GetTotalAllocatedBytes(true);
            JsonStorage.SaveStateAsync(path, copy).GetAwaiter().GetResult();
            allocated = GC.GetTotalAllocatedBytes(true) - allocated;
            var bytes = new FileInfo(path).Length;
            if (allocated > bytes / 2) throw new InvalidOperationException($"流式写入仍分配接近整文件的缓冲：{allocated}/{bytes}。");
            var restored = JsonStorage.LoadState(path, PublicApiPricing.PricingVersion);
            VerifyHistory(copy, restored);
            // 下次替换应保留上一代完整文件。
            copy.LastObservedUsedPercent = 11;
            JsonStorage.SaveStateAsync(path, copy).GetAwaiter().GetResult();
            VerifyHistory(copy, JsonStorage.LoadState(path + ".bak", PublicApiPricing.PricingVersion));
            copy.LastObservedUsedPercent = 12;
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try
                {
                    JsonStorage.SaveStateAsync(path, copy).GetAwaiter().GetResult();
                    throw new InvalidOperationException("目标文件被锁定时仍报告保存成功。");
                }
                catch (IOException exception)
                {
                    if (!Equals(exception.Data["AtomicReplaceAttempts"], 6) ||
                        exception.Data["AtomicReplaceFirstFailure"] is not string || !File.Exists(path + ".tmp"))
                        throw new InvalidOperationException("持续占用时未限次停止或丢失了原始失败与待写入文件。", exception);
                }
            }
            if (JsonStorage.LoadState(path, PublicApiPricing.PricingVersion).LastObservedUsedPercent != 11)
                throw new InvalidOperationException("失败写入破坏了上一代完整状态。");
            JsonStorage.SaveStateAsync(path, copy).GetAwaiter().GetResult();
            if (JsonStorage.LoadState(path, PublicApiPricing.PricingVersion).LastObservedUsedPercent != 12)
                throw new InvalidOperationException("共享冲突解除后不能重试保存最新状态。");
            VerifyShortLivedLock(path, copy);
            VerifyMessagePump(path, copy);
            Console.WriteLine($"持久化业务验证：20000条计价明细完整，写分配={allocated:N0}B，文件={bytes:N0}B；备份、失败与重试通过。");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>用真实 Windows 共享锁阻止替换，待新文件开始写入后释放，确认限次等待可恢复且备份仍是上一代。</summary>
    private static void VerifyShortLivedLock(string path, MonitorState state)
    {
        state.LastObservedUsedPercent = 13;
        Task save;
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            save = JsonStorage.SaveStateAsync(path, state);
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(path + ".tmp") && !save.IsCompleted)
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("未开始写入独立临时文件。");
                Thread.Sleep(1);
            }
            Thread.Sleep(100);
            if (save.IsCompleted) throw new InvalidOperationException("共享锁未释放时写入提前结束，未正确执行占用等待。");
        }
        save.GetAwaiter().GetResult();
        if (JsonStorage.LoadState(path, PublicApiPricing.PricingVersion).LastObservedUsedPercent != 13 ||
            JsonStorage.LoadState(path + ".bak", PublicApiPricing.PricingVersion).LastObservedUsedPercent != 12)
            throw new InvalidOperationException("短时占用解除后的最新状态或上一代备份不完整。");
    }

    /// <summary>分别启动进程对比真实同源历史的旧/新读写，报告分配、峰值和耗时；不写生产文件。</summary>
    public static void Benchmark(string mode, string source, string directory)
    {
        if (mode is not ("legacy" or "stream")) throw new ArgumentException("仅支持 legacy/stream。");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, mode + "-state.json");
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("基准不得覆盖输入状态。");
        var before = GC.GetTotalAllocatedBytes(true);
        var timer = Stopwatch.StartNew();
        var state = mode == "legacy"
            ? JsonSerializer.Deserialize<MonitorState>(File.ReadAllText(source), LegacyOptions)!
            : JsonStorage.LoadState(source, PublicApiPricing.PricingVersion);
        var readMs = timer.Elapsed.TotalMilliseconds;
        var readAllocation = GC.GetTotalAllocatedBytes(true) - before;
        before = GC.GetTotalAllocatedBytes(true); timer.Restart();
        if (mode == "legacy")
        {
            var json = JsonSerializer.Serialize(state, LegacyOptions);
            using var stream = new FileStream(target, FileMode.Create, FileAccess.Write);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.Write(json); writer.Flush(); stream.Flush(true);
        }
        else JsonStorage.SaveStateAsync(target, state.CopyForPersistence()).GetAwaiter().GetResult();
        var writeMs = timer.Elapsed.TotalMilliseconds;
        var writeAllocation = GC.GetTotalAllocatedBytes(true) - before;
        using var process = Process.GetCurrentProcess();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            mode, readMs, readAllocation, writeMs, writeAllocation,
            sourceBytes = new FileInfo(source).Length, targetBytes = new FileInfo(target).Length,
            samples = state.Samples.Count, responses = state.Samples.Sum(s => (long)s.PricingUsages.Count),
            apiEquivalentUsd = state.Samples.Sum(s => s.IntervalApiEquivalentUsd),
            peakWorkingSet = process.PeakWorkingSet64
        }));
        // 度量结束后逐条核对全部历史，不让校验分配污染上述峰值和耗时。
        VerifyHistory(state, JsonStorage.LoadState(target, PublicApiPricing.PricingVersion));
    }

    /// <summary>在隔离桌面验证多次真实落盘等待期间仍可处理 UI 定时消息，而不是在界面线程同步等待磁盘。</summary>
    private static void VerifyMessagePump(string path, MonitorState state)
    {
        using var window = new Form();
        _ = window.Handle;
        var ticks = 0;
        using var timer = new System.Windows.Forms.Timer { Interval = 10 };
        timer.Tick += (_, _) => ticks++;
        timer.Start();
        var writes = Task.Run(async () =>
        {
            for (var i = 0; i < 8; i++)
            {
                try { await JsonStorage.SaveStateAsync(path, state); }
                catch (IOException exception)
                {
                    Console.Error.WriteLine($"UI 心跳落盘第{i + 1}次失败：HRESULT=0x{exception.HResult:X8}，" +
                        $"目标存在={File.Exists(path)}，备份存在={File.Exists(path + ".bak")}，路径={path}。");
                    throw;
                }
            }
        });
        var deadline = Stopwatch.StartNew();
        while (!writes.IsCompleted)
        {
            Application.DoEvents();
            if (deadline.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("后台持久化超时。");
            Thread.Sleep(1);
        }
        writes.GetAwaiter().GetResult();
        if (ticks == 0) throw new InvalidOperationException("保存期间未能处理 UI 心跳消息。");
    }

    /// <summary>比较每个样本及全部逐响应依据，不用样本总数代替历史完整性检查。</summary>
    private static void VerifyHistory(MonitorState expected, MonitorState actual)
    {
        if (expected.Samples.Count != actual.Samples.Count) throw new InvalidOperationException("历史样本丢失。");
        for (var index = 0; index < expected.Samples.Count; index++)
        {
            var left = expected.Samples[index]; var right = actual.Samples[index];
            if (left with { PricingUsages = right.PricingUsages } != right || !left.PricingUsages.SequenceEqual(right.PricingUsages))
                throw new InvalidOperationException($"第{index}个历史样本或其重算依据发生变化。");
        }
    }
}
