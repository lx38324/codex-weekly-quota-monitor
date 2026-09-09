using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WeeklyQuotaMonitor.Core;

/// <summary>
/// 按持久化字节游标增量读取 Codex rollout JSONL，并关联本地模型响应与 token_count。
/// </summary>
public sealed partial class RolloutLogReader
{
    public const int CurrentServiceTierTrackingVersion = 2;
    public const int CurrentResponseAssociationTrackingVersion = 1;
    private const int SessionHeaderPrefixBytes = 4096;
    private const int StreamingReadBufferBytes = 64 * 1024;
    private readonly PricingCatalog _pricingCatalog;
    private readonly Dictionary<string, ActivityIndex> _activityIndexes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 创建使用程序内置价格的日志读取器，供既有调用和独立业务测试使用。
    /// </summary>
    public RolloutLogReader()
        : this(PublicApiPricing.BuiltInCatalog)
    {
    }

    /// <summary>
    /// 创建绑定指定不可变价格配置的日志读取器，保证一次扫描不会混入设置变更后的价格。
    /// </summary>
    /// <param name="pricingCatalog">本轮实时扫描或历史重放使用的完整价格配置。</param>
    public RolloutLogReader(PricingCatalog pricingCatalog)
    {
        _pricingCatalog = pricingCatalog;
    }

    /// <summary>
    /// 首次启动时把已有日志设为基线；仅扫描近期文件内容以保留正在进行响应的模型上下文。
    /// </summary>
    /// <param name="sessionRoot">Codex sessions 根目录。</param>
    /// <param name="state">保存文件游标的监控状态。</param>
    /// <param name="contextLookbackHours">需要解析上下文的近期文件小时范围。</param>
    public void PrimeExistingFiles(string sessionRoot, MonitorState state, int contextLookbackHours)
    {
        if (contextLookbackHours <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextLookbackHours));
        }

        MigrateServiceTierTrackingState(state);
        MigrateResponseAssociationTrackingState(state);
        var configuredTier = ReadConfiguredServiceTierEvidence(sessionRoot);
        var malformedLines = 0;
        var contextCutoff = DateTime.UtcNow.AddHours(-contextLookbackHours);
        foreach (var path in EnumerateRolloutFiles(sessionRoot))
        {
            var info = new FileInfo(path);
            var cursor = new FileCursorState { CreationTimeUtcTicks = info.CreationTimeUtc.Ticks };
            ApplyConfiguredServiceTier(path, cursor, configuredTier);
            if (info.LastWriteTimeUtc >= contextCutoff)
            {
                cursor.Offset = ProcessCompleteLines(path, 0, line =>
                {
                    if (!ProcessLine(line, cursor, null, null))
                    {
                        malformedLines++;
                    }
                });
            }
            else
            {
                cursor.Offset = info.Length;
            }

            state.FileCursors[path] = cursor;
        }

        state.MalformedRolloutLines += malformedLines;
        state.RolloutFilesPrimed = true;
    }

    /// <summary>
    /// 读取所有已知和新发现 rollout 文件中尚未消费的完整 JSONL 行并计算 Standard API 等价成本。
    /// </summary>
    /// <param name="sessionRoot">Codex sessions 根目录。</param>
    /// <param name="state">包含并更新文件字节游标的监控状态。</param>
    /// <param name="includeSince">仅把不早于此时刻的 token_count 计入金额；更早行仍会推进游标。</param>
    /// <returns>本次新增模型响应、数据质量和文件轮转统计。</returns>
    public RolloutScanResult ScanNew(
        string sessionRoot,
        MonitorState state,
        DateTimeOffset? includeSince = null)
    {
        MigrateServiceTierTrackingState(state);
        MigrateResponseAssociationTrackingState(state);
        var configuredTier = ReadConfiguredServiceTierEvidence(sessionRoot);
        var accumulator = new ScanAccumulator(_pricingCatalog, includeSince);
        var paths = EnumerateRolloutFiles(sessionRoot).ToArray();
        var currentPaths = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var removedCursors = state.FileCursors.Keys
            .Where(path => !currentPaths.Contains(path) && !File.Exists(path))
            .ToArray();
        foreach (var removedCursor in removedCursors)
        {
            state.FileCursors.Remove(removedCursor);
        }
        accumulator.SetPrunedCursorCount(removedCursors.Length);

        foreach (var path in paths)
        {
            var info = new FileInfo(path);
            if (!state.FileCursors.TryGetValue(path, out var cursor))
            {
                cursor = new FileCursorState { CreationTimeUtcTicks = info.CreationTimeUtc.Ticks };
                state.FileCursors[path] = cursor;
            }

            if (cursor.CreationTimeUtcTicks == 0)
            {
                cursor.CreationTimeUtcTicks = info.CreationTimeUtc.Ticks;
            }

            if (info.CreationTimeUtc.Ticks != cursor.CreationTimeUtcTicks || info.Length < cursor.Offset)
            {
                ResetCursor(cursor, info.CreationTimeUtc.Ticks);
                accumulator.AddRotatedFile();
            }

            if (string.Equals(cursor.CurrentServiceTier, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                ApplyConfiguredServiceTier(path, cursor, configuredTier);
            }

            if (info.Length == cursor.Offset)
            {
                continue;
            }

            cursor.Offset = ProcessCompleteLines(path, cursor.Offset, line =>
            {
                if (!ProcessLine(line, cursor, accumulator, includeSince))
                {
                    accumulator.AddMalformedLine();
                }
            });
        }

        return accumulator.ToResult();
    }

    /// <summary>
    /// 根据日志内部事件时间选择窗口相关文件，重读完整上下文；修改时间不能用于排除跨重置会话。
    /// </summary>
    /// <param name="sessionRoot">Codex sessions 根目录。</param>
    /// <param name="windowStart">当前周窗口起点。</param>
    /// <param name="windowEnd">当前权威额度快照时间。</param>
    /// <param name="windowDurationMinutes">需要提取的周窗口分钟数。</param>
    /// <param name="cancellationToken">改价或退出时取消过时扫描。</param>
    /// <param name="progress">报告已完成和总文件数；回调不包含文件路径。</param>
    /// <returns>不修改增量游标的历史事实和完整性诊断。</returns>
    public HistoricalRolloutFacts ReadHistoricalFacts(
        string sessionRoot,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int windowDurationMinutes,
        CancellationToken cancellationToken = default,
        IProgress<(int Completed, int Total)>? progress = null)
        => ReadHistoricalFacts([sessionRoot], windowStart, windowEnd, windowDurationMinutes, cancellationToken, progress);

    /// <summary>
    /// 从多个 Codex 会话根目录一次性重读历史上下文，并按 rollout 文件名去重活动与归档副本。
    /// </summary>
    /// <param name="sessionRoots">需要合并扫描的 sessions 或 archived_sessions 根目录。</param>
    /// <param name="windowStart">历史重放最早时间。</param>
    /// <param name="windowEnd">历史重放最晚时间。</param>
    /// <param name="windowDurationMinutes">需要提取的额度窗口分钟数。</param>
    /// <param name="cancellationToken">每条完整日志处理前检查取消。</param>
    /// <param name="progress">报告文件扫描进度。</param>
    /// <returns>不修改增量游标的合并历史事实和完整性诊断。</returns>
    public HistoricalRolloutFacts ReadHistoricalFacts(
        IReadOnlyCollection<string> sessionRoots,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int windowDurationMinutes,
        CancellationToken cancellationToken = default,
        IProgress<(int Completed, int Total)>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sessionRoots.Count == 0)
        {
            throw new ArgumentException("历史重放至少需要一个 Codex 会话根目录。", nameof(sessionRoots));
        }

        if (windowEnd < windowStart)
        {
            throw new ArgumentOutOfRangeException(nameof(windowEnd), "历史重放结束时间不能早于窗口起点。");
        }

        if (windowDurationMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowDurationMinutes));
        }

        var accumulator = new ScanAccumulator(
            _pricingCatalog,
            windowStart,
            windowEnd,
            windowDurationMinutes);
        var normalizedRoots = sessionRoots
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var configuredTier = ReadConfiguredServiceTierEvidence(normalizedRoots[0]);
        var paths = normalizedRoots
            .SelectMany(EnumerateRolloutFiles)
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.MaxBy(path => new FileInfo(path).Length)!)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var completed = 0;
        var scanned = 0;
        var existingPaths = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var missing in _activityIndexes.Keys.Where(path => !existingPaths.Contains(path)).ToArray())
            _activityIndexes.Remove(missing);
        progress?.Report((0, paths.Length));
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MayContainEventsSince(path, windowStart, cancellationToken))
            {
                progress?.Report((++completed, paths.Length));
                continue;
            }
            scanned++;
            var tierProbe = new ScanAccumulator(_pricingCatalog, null, retainPricingUsages: false);
            var tierProbeCursor = new FileCursorState();
            ProcessCompleteLines(path, 0, line =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProcessLine(line, tierProbeCursor, tierProbe, null);
            });

            var explicitTier = tierProbe.GetConsistentExplicitServiceTier();
            var cursor = new FileCursorState { CurrentServiceTier = explicitTier ?? "unknown" };
            if (explicitTier is null && !tierProbe.HasExplicitServiceTierEvidence)
            {
                ApplyConfiguredServiceTier(path, cursor, configuredTier);
            }
            accumulator.SetHistoricalSourceFile(path);
            ProcessCompleteLines(path, 0, line =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ProcessLine(line, cursor, accumulator, windowStart))
                {
                    accumulator.AddMalformedLine();
                }
            });
            progress?.Report((++completed, paths.Length));
        }

        return accumulator.ToHistoricalFacts(windowStart, windowEnd, scanned);
    }

    /// <summary>
    /// 增量索引完整记录中的最大事件时间；追加写入即使不更新 mtime 也按长度发现。
    /// 索引只在读取成功后提交；截短、换文件或同长改写会重建。无法解析的时间保留给正常扫描报告，不能据此排除文件。
    /// </summary>
    private bool MayContainEventsSince(string path, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        _activityIndexes.TryGetValue(path, out var previous);
        var reusable = previous is not null && previous.CreationTicks == info.CreationTimeUtc.Ticks &&
            info.Length >= previous.Length &&
            (info.Length != previous.Length || info.LastWriteTimeUtc.Ticks == previous.WriteTicks);
        var offset = reusable ? previous!.Offset : 0;
        var latest = reusable ? previous!.Latest : DateTimeOffset.MinValue;
        var uncertain = reusable && previous!.Uncertain;
        var committed = ProcessCompleteLines(path, offset, line =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    TryReadTimestamp(document.RootElement, out var timestamp))
                    latest = timestamp > latest ? timestamp : latest;
                else
                    uncertain = true;
            }
            catch (JsonException)
            {
                // 正常扫描会计数并展示坏行；索引不得把无法分类的文件误判为已过期。
                uncertain = true;
            }
        });
        cancellationToken.ThrowIfCancellationRequested();
        _activityIndexes[path] = new(committed, info.Length, info.CreationTimeUtc.Ticks,
            info.LastWriteTimeUtc.Ticks, latest, uncertain);
        return uncertain || latest >= since;
    }

    /// <summary>仅缓存文件活动时间和增量位置，不保存对话正文；供同一读取器后续历史扫描复用。</summary>
    private sealed record ActivityIndex(long Offset, long Length, long CreationTicks, long WriteTicks,
        DateTimeOffset Latest, bool Uncertain);

    /// <summary>
    /// 根据配置的 sessions 路径自动发现同一 Codex 数据目录下可用的活动与归档会话根目录。
    /// </summary>
    /// <param name="sessionRoot">设置中配置的活动 sessions 根目录。</param>
    /// <returns>始终包含活动目录，并在存在时追加同级 archived_sessions 目录。</returns>
    public static IReadOnlyList<string> DiscoverHistoricalSessionRoots(string sessionRoot)
    {
        var absoluteSessionRoot = Path.GetFullPath(sessionRoot);
        var codexRoot = Path.GetDirectoryName(absoluteSessionRoot)
            ?? throw new InvalidDataException($"无法从 sessions 路径解析 Codex 数据目录：{absoluteSessionRoot}");
        var roots = new List<string> { absoluteSessionRoot };
        var archivedRoot = Path.Combine(codexRoot, "archived_sessions");
        if (Directory.Exists(archivedRoot) &&
            !string.Equals(archivedRoot, absoluteSessionRoot, StringComparison.OrdinalIgnoreCase))
        {
            roots.Add(archivedRoot);
        }

        return roots;
    }

    /// <summary>
    /// 记录仍含未定价响应的 rollout 文件长度，用于后续只检查这些文件是否补写了层级证据。
    /// </summary>
    /// <param name="paths">历史重放确认含未定价响应的源文件。</param>
    /// <returns>以不区分大小写路径为键的当前文件长度。</returns>
    public static Dictionary<string, long> CaptureFileLengths(IEnumerable<string> paths)
    {
        var lengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path))
            {
                lengths[path] = new FileInfo(path).Length;
            }
        }

        return lengths;
    }

    /// <summary>
    /// 检查未定价响应所在文件是否被补写、截断或删除，避免在所有会话日志上执行高频全量重放。
    /// </summary>
    /// <param name="trackedLengths">上次历史重放保存的未解析源文件及长度。</param>
    /// <returns>任一受跟踪文件状态改变时返回 true。</returns>
    public static bool HaveTrackedFilesChanged(IReadOnlyDictionary<string, long> trackedLengths)
    {
        foreach (var pair in trackedLengths)
        {
            if (!File.Exists(pair.Key) || new FileInfo(pair.Key).Length != pair.Value)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 把旧版隐式 Standard 层级游标迁移为 unknown，避免升级后继续按错误倍率计价。
    /// </summary>
    /// <param name="state">需要检查并原地迁移的监控状态。</param>
    public static void MigrateServiceTierTrackingState(MonitorState state)
    {
        if (state.ServiceTierTrackingVersion > CurrentServiceTierTrackingVersion)
        {
            throw new InvalidDataException(
                $"state.json 的服务层级追踪版本 {state.ServiceTierTrackingVersion} 高于当前支持版本 {CurrentServiceTierTrackingVersion}。");
        }

        if (state.ServiceTierTrackingVersion == CurrentServiceTierTrackingVersion)
        {
            return;
        }

        foreach (var cursor in state.FileCursors.Values)
        {
            cursor.CurrentServiceTier = "unknown";
            cursor.CurrentServiceTierFromConfig = false;
            ClearPendingResponse(cursor);
        }

        state.ServiceTierTrackingVersion = CurrentServiceTierTrackingVersion;
    }

    /// <summary>
    /// 升级响应关联状态；旧版 pending 未保存响应时模型和层级快照，必须清除后重新建立。
    /// </summary>
    /// <param name="state">需要检查并原地迁移的监控状态。</param>
    public static void MigrateResponseAssociationTrackingState(MonitorState state)
    {
        if (state.ResponseAssociationTrackingVersion > CurrentResponseAssociationTrackingVersion)
        {
            throw new InvalidDataException(
                $"state.json 的响应关联追踪版本 {state.ResponseAssociationTrackingVersion} 高于当前支持版本 {CurrentResponseAssociationTrackingVersion}。");
        }

        if (state.ResponseAssociationTrackingVersion == CurrentResponseAssociationTrackingVersion)
        {
            return;
        }

        foreach (var cursor in state.FileCursors.Values)
        {
            ClearPendingResponse(cursor);
        }

        state.ResponseAssociationTrackingVersion = CurrentResponseAssociationTrackingVersion;
    }

    /// <summary>
    /// 将被截短或替换文件的游标恢复到文件起点，并清除不可跨文件继承的模型上下文。
    /// </summary>
    /// <param name="cursor">需要原地重置的文件游标。</param>
    /// <param name="creationTimeUtcTicks">当前文件创建时间标识。</param>
    private static void ResetCursor(FileCursorState cursor, long creationTimeUtcTicks)
    {
        cursor.Offset = 0;
        cursor.CreationTimeUtcTicks = creationTimeUtcTicks;
        cursor.CurrentModel = string.Empty;
        cursor.CurrentServiceTier = "unknown";
        cursor.CurrentServiceTierFromConfig = false;
        ClearPendingResponse(cursor);
    }

    /// <summary>
    /// 枚举 sessions 根目录内全部 rollout JSONL 文件并使用稳定路径排序。
    /// </summary>
    /// <param name="sessionRoot">Codex sessions 根目录。</param>
    /// <returns>按路径排序的 JSONL 文件序列。</returns>
    private static IEnumerable<string> EnumerateRolloutFiles(string sessionRoot)
    {
        if (!Directory.Exists(sessionRoot))
        {
            throw new DirectoryNotFoundException($"Codex sessions 目录不存在：{sessionRoot}");
        }

        return Directory
            .EnumerateFiles(sessionRoot, "*.jsonl", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从指定字节位置流式读取完整 UTF-8 JSONL 行，避免为大型 rollout 同时分配整文件字节、字符串和行数组。
    /// </summary>
    /// <param name="path">需要读取的 rollout 文件。</param>
    /// <param name="offset">已持久化的起始字节位置。</param>
    /// <param name="processLine">逐行处理以换行结束的非空完整记录。</param>
    /// <returns>最后一条完整行之后的字节位置；末尾半行留待下次重新读取。</returns>
    internal static long ProcessCompleteLines(string path, long offset, Action<string> processLine)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length <= offset)
        {
            return offset;
        }

        stream.Position = offset;
        var buffer = new byte[StreamingReadBufferBytes];
        using var lineBuffer = new MemoryStream();
        var committedOffset = offset;
        while (true)
        {
            var bufferStart = stream.Position;
            var bytesRead = stream.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
            {
                break;
            }

            var segmentStart = 0;
            for (var index = 0; index < bytesRead; index++)
            {
                if (buffer[index] != (byte)'\n')
                {
                    continue;
                }

                lineBuffer.Write(buffer, segmentStart, index - segmentStart);
                EmitCompleteLine(lineBuffer, processLine);
                lineBuffer.SetLength(0);
                committedOffset = bufferStart + index + 1;
                segmentStart = index + 1;
            }

            if (segmentStart < bytesRead)
            {
                lineBuffer.Write(buffer, segmentStart, bytesRead - segmentStart);
            }
        }

        return committedOffset;
    }

    /// <summary>
    /// 解码一条已确认换行结束的 UTF-8 记录，移除可选回车并跳过空行。
    /// </summary>
    /// <param name="lineBuffer">不含换行符的单行 UTF-8 字节。</param>
    /// <param name="processLine">接收解码后完整 JSONL 行的处理器。</param>
    private static void EmitCompleteLine(MemoryStream lineBuffer, Action<string> processLine)
    {
        var length = checked((int)lineBuffer.Length);
        var bytes = lineBuffer.GetBuffer();
        if (length > 0 && bytes[length - 1] == (byte)'\r')
        {
            length--;
        }

        if (length > 0)
        {
            processLine(Encoding.UTF8.GetString(bytes, 0, length));
        }
    }

    /// <summary>
    /// 解析单行 rollout 事件；畸形完整行被隔离并返回 false，不阻断后续文件扫描。
    /// </summary>
    /// <param name="line">单条完整 JSONL 行。</param>
    /// <param name="cursor">当前文件的模型和关联状态。</param>
    /// <param name="accumulator">非基线扫描时使用的统计累加器。</param>
    /// <param name="includeSince">需要计入金额的最早 token_count 时间。</param>
    /// <returns>结构可识别时返回 true；畸形或缺少必要字段时返回 false。</returns>
    private static bool ProcessLine(
        string line,
        FileCursorState cursor,
        ScanAccumulator? accumulator,
        DateTimeOffset? includeSince)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return ProcessParsedLine(document.RootElement, cursor, accumulator, includeSince);
        }
        catch (JsonException)
        {
            ClearPendingResponse(cursor);
            return false;
        }
    }

    /// <summary>
    /// 对已成功解析的 rollout 对象执行字段验证、响应关联和 token 累积。
    /// </summary>
    /// <param name="root">已解析的 JSON 根对象。</param>
    /// <param name="cursor">当前文件游标状态。</param>
    /// <param name="accumulator">非基线扫描时使用的统计累加器。</param>
    /// <param name="includeSince">需要计入金额的最早 token_count 时间。</param>
    /// <returns>必要字段有效时返回 true。</returns>
    private static bool ProcessParsedLine(
        JsonElement root,
        FileCursorState cursor,
        ScanAccumulator? accumulator,
        DateTimeOffset? includeSince)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            ClearPendingResponse(cursor);
            return false;
        }

        var type = typeElement.GetString();
        if (type == "turn_context")
        {
            return ReadTurnContext(root, cursor, accumulator);
        }

        if (type == "response_item")
        {
            return ReadResponseItem(root, cursor);
        }

        if (type != "event_msg")
        {
            return true;
        }

        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            ClearPendingResponse(cursor);
            return false;
        }

        if (!payload.TryGetProperty("type", out var payloadType) || payloadType.ValueKind != JsonValueKind.String)
        {
            ClearPendingResponse(cursor);
            return false;
        }

        var eventType = payloadType.GetString();
        if (eventType == "thread_settings_applied")
        {
            return ReadThreadSettingsApplied(payload, cursor, accumulator);
        }

        if (eventType == "task_started")
        {
            ClearPendingResponse(cursor);
            return true;
        }

        if (eventType != "token_count")
        {
            return true;
        }

        if (accumulator is not null && !accumulator.TryCaptureRateLimitCheckpoint(root, payload))
        {
            ClearPendingResponse(cursor);
            return false;
        }

        if (!payload.TryGetProperty("info", out var info))
        {
            ClearPendingResponse(cursor);
            return false;
        }

        if (info.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (info.ValueKind != JsonValueKind.Object ||
            !info.TryGetProperty("last_token_usage", out var lastUsage) ||
            !TryParseLastUsage(lastUsage, out var usage))
        {
            ClearPendingResponse(cursor);
            return false;
        }

        if (!cursor.PendingModelResponse)
        {
            return true;
        }

        var responseModel = cursor.PendingResponseModel;
        var responseServiceTier = cursor.PendingResponseServiceTier;
        var responseServiceTierFromConfig = cursor.PendingResponseServiceTierFromConfig;
        ClearPendingResponse(cursor);
        if (accumulator is null)
        {
            return true;
        }

        if (!TryReadTimestamp(root, out var responseTimestamp))
        {
            return false;
        }

        if (includeSince is not null)
        {
            if (responseTimestamp < includeSince.Value)
            {
                return true;
            }
        }

        accumulator.Add(
            responseTimestamp,
            responseModel,
            responseServiceTier,
            responseServiceTierFromConfig,
            usage);
        return true;
    }

    /// <summary>
    /// 从新版 thread_settings_applied 更新实际服务层级和可选模型，并清除跨设置的待关联状态。
    /// </summary>
    /// <param name="payload">event_msg 的 payload 对象。</param>
    /// <param name="cursor">需要更新的文件游标。</param>
    /// <param name="accumulator">用于记录历史文件中明确层级证据的可选累加器。</param>
    /// <returns>thread_settings 中的服务层级以及可选模型有效时返回 true。</returns>
    private static bool ReadThreadSettingsApplied(
        JsonElement payload,
        FileCursorState cursor,
        ScanAccumulator? accumulator)
    {
        ClearPendingResponse(cursor);
        if (!payload.TryGetProperty("thread_settings", out var settings) ||
            settings.ValueKind != JsonValueKind.Object ||
            !settings.TryGetProperty("service_tier", out var tier) ||
            tier.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var serviceTier = tier.GetString();
        if (string.IsNullOrWhiteSpace(serviceTier))
        {
            return false;
        }

        string? modelName = null;
        if (settings.TryGetProperty("model", out var model))
        {
            if (model.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            modelName = model.GetString();
            if (string.IsNullOrWhiteSpace(modelName))
            {
                return false;
            }
        }

        cursor.CurrentServiceTier = serviceTier;
        cursor.CurrentServiceTierFromConfig = false;
        accumulator?.RecordExplicitServiceTier(serviceTier);
        if (modelName is not null)
        {
            cursor.CurrentModel = modelName;
        }

        return true;
    }

    /// <summary>
    /// 从 turn_context 更新实际模型；仅在旧版日志明确提供服务层级时才更新层级。
    /// </summary>
    /// <param name="root">turn_context 根对象。</param>
    /// <param name="cursor">需要更新的文件游标。</param>
    /// <param name="accumulator">用于记录旧版显式层级证据的可选累加器。</param>
    /// <returns>模型以及可选的显式服务层级字段有效时返回 true。</returns>
    private static bool ReadTurnContext(
        JsonElement root,
        FileCursorState cursor,
        ScanAccumulator? accumulator)
    {
        ClearPendingResponse(cursor);
        if (!root.TryGetProperty("payload", out var payload) ||
            payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("model", out var model) ||
            model.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var modelName = model.GetString();
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        string? explicitServiceTier = null;
        if (payload.TryGetProperty("service_tier", out var tier))
        {
            if (tier.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            explicitServiceTier = tier.GetString();
            if (string.IsNullOrWhiteSpace(explicitServiceTier))
            {
                return false;
            }
        }

        cursor.CurrentModel = modelName;
        if (explicitServiceTier is not null)
        {
            cursor.CurrentServiceTier = explicitServiceTier;
            cursor.CurrentServiceTierFromConfig = false;
            accumulator?.RecordExplicitServiceTier(explicitServiceTier);
        }

        return true;
    }

    /// <summary>
    /// 读取 response_item 并记录它是否是下一条 token_count 可归因的本地模型响应。
    /// </summary>
    /// <param name="root">response_item 根对象。</param>
    /// <param name="cursor">需要更新关联状态的文件游标。</param>
    /// <returns>payload 结构有效时返回 true。</returns>
    private static bool ReadResponseItem(JsonElement root, FileCursorState cursor)
    {
        if (!root.TryGetProperty("payload", out var payload) ||
            payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("type", out var itemType) ||
            itemType.ValueKind != JsonValueKind.String)
        {
            ClearPendingResponse(cursor);
            return false;
        }

        var itemTypeName = itemType.GetString() ?? string.Empty;
        if (IsLocalModelResponse(payload, itemTypeName))
        {
            SetPendingResponse(cursor);
        }
        else if (!itemTypeName.EndsWith("_output", StringComparison.Ordinal))
        {
            ClearPendingResponse(cursor);
        }

        return true;
    }

    /// <summary>
    /// 记录待关联响应，并快照响应产生时的模型与服务层级，避免后续设置变化造成错价。
    /// </summary>
    /// <param name="cursor">需要更新关联状态的文件游标。</param>
    private static void SetPendingResponse(FileCursorState cursor)
    {
        cursor.PendingModelResponse = true;
        cursor.PendingResponseModel = cursor.CurrentModel;
        cursor.PendingResponseServiceTier = cursor.CurrentServiceTier;
        cursor.PendingResponseServiceTierFromConfig = cursor.CurrentServiceTierFromConfig;
    }

    /// <summary>
    /// 清除一次待关联响应及其模型、服务层级快照。
    /// </summary>
    /// <param name="cursor">需要清除关联状态的文件游标。</param>
    private static void ClearPendingResponse(FileCursorState cursor)
    {
        cursor.PendingModelResponse = false;
        cursor.PendingResponseModel = string.Empty;
        cursor.PendingResponseServiceTier = string.Empty;
        cursor.PendingResponseServiceTierFromConfig = false;
    }

    /// <summary>
    /// 判断 response_item 是否由模型产生，而非用户、开发者或工具输出写入历史。
    /// </summary>
    /// <param name="payload">已验证为对象的 response_item payload。</param>
    /// <param name="itemType">已读取的 item 类型。</param>
    /// <returns>assistant 消息、reasoning 和工具调用等模型输出返回 true。</returns>
    private static bool IsLocalModelResponse(JsonElement payload, string itemType)
    {
        if (itemType == "message")
        {
            return payload.TryGetProperty("role", out var role) &&
                   role.ValueKind == JsonValueKind.String &&
                   role.GetString() == "assistant";
        }

        if (itemType.EndsWith("_output", StringComparison.Ordinal))
        {
            return false;
        }

        return itemType is "reasoning" or "function_call" or "custom_tool_call" or
            "local_shell_call" or "mcp_tool_call" or "web_search_call" or
            "computer_call" or "image_generation_call";
    }

    /// <summary>
    /// 从 rollout 根对象读取标准 ISO-8601 时间戳，用于重置窗口后的日志切分。
    /// </summary>
    /// <param name="root">包含 timestamp 的 rollout 对象。</param>
    /// <param name="timestamp">成功时返回解析后的绝对时间。</param>
    /// <returns>时间戳存在且可按往返格式解析时返回 true。</returns>
    private static bool TryReadTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return root.TryGetProperty("timestamp", out var timestampElement) &&
               timestampElement.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParse(
                   timestampElement.GetString(),
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind,
                   out timestamp);
    }

    /// <summary>
    /// 从 last_token_usage 读取并验证逐响应 token 字段；协议中的可选字段缺省时按零处理。
    /// </summary>
    /// <param name="usageElement">last_token_usage 对象。</param>
    /// <param name="usage">成功时返回逐响应 token 分类。</param>
    /// <returns>必要字段存在、为整数且各 token 数非负时返回 true。</returns>
    private static bool TryParseLastUsage(JsonElement usageElement, out TokenUsage usage)
    {
        usage = TokenUsage.Zero;
        if (usageElement.ValueKind != JsonValueKind.Object ||
            !TryReadInt64(usageElement, "input_tokens", out var input) ||
            !TryReadInt64(usageElement, "cached_input_tokens", out var cached) ||
            !TryReadInt64(usageElement, "output_tokens", out var output))
        {
            return false;
        }

        var cacheWrite = 0L;
        if (usageElement.TryGetProperty("cache_write_input_tokens", out var cacheWriteElement) &&
            !cacheWriteElement.TryGetInt64(out cacheWrite))
        {
            return false;
        }

        var reasoning = 0L;
        if (usageElement.TryGetProperty("reasoning_output_tokens", out var reasoningElement) &&
            !reasoningElement.TryGetInt64(out reasoning))
        {
            return false;
        }

        if (input < 0 || cached < 0 || cacheWrite < 0 || output < 0 || reasoning < 0)
        {
            return false;
        }

        usage = new(input, cached, cacheWrite, output, reasoning);
        return true;
    }

    /// <summary>
    /// 从 JSON 对象读取一个必需的 Int64 字段。
    /// </summary>
    /// <param name="element">包含字段的 JSON 对象。</param>
    /// <param name="propertyName">需要读取的字段名。</param>
    /// <param name="value">成功时返回字段值。</param>
    /// <returns>字段存在且可表示为 Int64 时返回 true。</returns>
    private static bool TryReadInt64(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out value);
    }

    /// <summary>
    /// 读取 sessions 同级 config.toml 中明确配置的默认服务层级及其最后生效时间。
    /// </summary>
    /// <param name="sessionRoot">Codex sessions 根目录。</param>
    /// <returns>存在且可按公开计价口径解释时返回配置证据；没有显式配置时返回 null。</returns>
    private static ConfiguredServiceTierEvidence? ReadConfiguredServiceTierEvidence(string sessionRoot)
    {
        var absoluteSessionRoot = Path.GetFullPath(sessionRoot);
        var codexRoot = Path.GetDirectoryName(absoluteSessionRoot)
            ?? throw new InvalidDataException($"无法从 sessions 路径解析 Codex 配置目录：{absoluteSessionRoot}");
        var configPath = Path.Combine(codexRoot, "config.toml");
        if (!File.Exists(configPath))
        {
            return null;
        }

        string? configuredTier = null;
        var atTopLevel = true;
        foreach (var line in File.ReadLines(configPath))
        {
            var trimmed = line.Trim().TrimStart('\uFEFF');
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.StartsWith('['))
            {
                atTopLevel = false;
                continue;
            }

            if (!atTopLevel)
            {
                continue;
            }

            var match = ConfigServiceTierLinePattern().Match(trimmed);
            if (!match.Success)
            {
                if (trimmed.StartsWith("service_tier", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"无法解析 config.toml 的 service_tier：{trimmed}");
                }

                continue;
            }

            if (configuredTier is not null)
            {
                throw new InvalidDataException("config.toml 顶层重复定义 service_tier。");
            }

            configuredTier = NormalizeConfiguredServiceTier(match.Groups["tier"].Value);
        }

        return configuredTier is null
            ? null
            : new(configuredTier, new DateTimeOffset(File.GetLastWriteTimeUtc(configPath)));
    }

    /// <summary>
    /// 在配置早于会话创建且当前没有显式层级时，把配置层级作为可审计证据写入文件游标。
    /// </summary>
    /// <param name="path">rollout JSONL 绝对路径。</param>
    /// <param name="cursor">需要补充初始层级的文件游标。</param>
    /// <param name="configuredTier">本轮只读取一次的 config.toml 层级证据。</param>
    /// <returns>成功应用配置证据时返回 true。</returns>
    private static bool ApplyConfiguredServiceTier(
        string path,
        FileCursorState cursor,
        ConfiguredServiceTierEvidence? configuredTier)
    {
        if (configuredTier is null)
        {
            return false;
        }

        var sessionStartedAt = ReadSessionStartedAt(path);
        if (sessionStartedAt is null || configuredTier.WrittenAt > sessionStartedAt.Value)
        {
            return false;
        }

        cursor.CurrentServiceTier = configuredTier.ServiceTier;
        cursor.CurrentServiceTierFromConfig = true;
        if (cursor.PendingModelResponse &&
            string.Equals(cursor.PendingResponseServiceTier, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            cursor.PendingResponseServiceTier = configuredTier.ServiceTier;
            cursor.PendingResponseServiceTierFromConfig = true;
        }

        return true;
    }

    /// <summary>
    /// 从 rollout 文件前缀读取 session_meta 的绝对创建时间，不解析或持久化用户对话内容。
    /// </summary>
    /// <param name="path">rollout JSONL 绝对路径。</param>
    /// <returns>前缀包含有效 session_meta 时间时返回该时间，否则返回 null。</returns>
    private static DateTimeOffset? ReadSessionStartedAt(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[Math.Min(SessionHeaderPrefixBytes, checked((int)Math.Min(stream.Length, int.MaxValue)))];
        var length = stream.Read(bytes, 0, bytes.Length);
        if (length == 0)
        {
            return null;
        }

        var prefix = Encoding.UTF8.GetString(bytes, 0, length);
        if (!SessionMetaTypePattern().IsMatch(prefix))
        {
            return null;
        }

        var timestamp = SessionTimestampPattern().Match(prefix);
        return timestamp.Success && DateTimeOffset.TryParse(
            timestamp.Groups["timestamp"].Value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// 把 Codex 配置层级归一化为公开计价器支持的 Standard 或 Fast 请求值。
    /// </summary>
    /// <param name="serviceTier">config.toml 中的原始层级。</param>
    /// <returns>default 或 priority。</returns>
    private static string NormalizeConfiguredServiceTier(string serviceTier) =>
        serviceTier.Trim().ToLowerInvariant() switch
        {
            "standard" or "default" or "auto" => "default",
            "fast" or "priority" => "priority",
            _ => throw new InvalidDataException($"config.toml 的 service_tier 无法按当前公开口径计价：{serviceTier}")
        };

    /// <summary>
    /// 为展示层保留配置回填来源，避免把推导层级误报为 rollout 原生字段。
    /// </summary>
    /// <param name="normalizedServiceTier">定价器返回的 standard 或 fast。</param>
    /// <param name="serviceTierFromConfig">是否由配置证据回填。</param>
    /// <returns>带可选 config 来源标记的层级标签。</returns>
    public static string FormatServiceTierEvidence(
        string normalizedServiceTier,
        bool serviceTierFromConfig) =>
        serviceTierFromConfig ? $"{normalizedServiceTier}(config)" : normalizedServiceTier;

    /// <summary>
    /// 匹配 config.toml 顶层双引号 service_tier 标量。
    /// </summary>
    [GeneratedRegex(@"^\s*service_tier\s*=\s*""(?<tier>[^""]+)""\s*(?:#.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ConfigServiceTierLinePattern();

    /// <summary>
    /// 在 rollout 文件前缀中确认首条记录是 session_meta。
    /// </summary>
    [GeneratedRegex(@"""type""\s*:\s*""session_meta""", RegexOptions.IgnoreCase)]
    private static partial Regex SessionMetaTypePattern();

    /// <summary>
    /// 从 rollout 文件前缀提取根级 timestamp 字符串。
    /// </summary>
    [GeneratedRegex(@"""timestamp""\s*:\s*""(?<timestamp>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex SessionTimestampPattern();

    /// <summary>
    /// 保存 config.toml 明确层级及配置文件最后写入时刻，用于与会话创建时间建立证据顺序。
    /// </summary>
    private sealed record ConfiguredServiceTierEvidence(string ServiceTier, DateTimeOffset WrittenAt);

    /// <summary>
    /// 在一次扫描中累积可定价响应、不可定价响应、模型口径和数据质量统计。
    /// </summary>
    private sealed class ScanAccumulator
    {
        private readonly PricingCatalog _pricingCatalog;
        private readonly List<ResponsePricingUsage> _pricingUsages = [];
        private readonly bool _retainPricingUsages;
        private readonly DateTimeOffset? _includedSince;
        private readonly DateTimeOffset? _historicalWindowEnd;
        private readonly int? _historicalWindowDurationMinutes;
        private TokenUsage _usage = TokenUsage.Zero;
        private decimal _costUsd;
        private decimal _officialLongContextCostUsd;
        private int _responseCount;
        private int _unpricedCount;
        private int _malformedLineCount;
        private int _rotatedFileCount;
        private int _prunedCursorCount;
        private readonly HashSet<string> _models = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unpricedModels = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _serviceTiers = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _creditMultipliers = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _explicitServiceTiers = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<HistoricalResponseFact> _historicalResponses = [];
        private readonly Dictionary<HistoricalCheckpointKey, HistoricalRateLimitCheckpoint> _historicalCheckpoints = [];
        private string _historicalSourceFile = string.Empty;

        /// <summary>
        /// 创建一次扫描累加器并保存本轮用于重置窗口切分的最早时间。
        /// </summary>
        /// <param name="includedSince">仅纳入计价的最早 token_count 时间。</param>
        /// <param name="retainPricingUsages">仅实时扫描保留计价明细，层级探测不重复保存响应。</param>
        public ScanAccumulator(PricingCatalog pricingCatalog, DateTimeOffset? includedSince, bool retainPricingUsages = true)
        {
            _pricingCatalog = pricingCatalog;
            _includedSince = includedSince;
            _retainPricingUsages = retainPricingUsages;
        }

        /// <summary>
        /// 创建历史事实累加器，并限定额度候选和响应事实的绝对时间窗口。
        /// </summary>
        /// <param name="windowStart">当前周窗口起点。</param>
        /// <param name="windowEnd">当前权威额度快照时间。</param>
        /// <param name="windowDurationMinutes">需要提取的周窗口分钟数。</param>
        public ScanAccumulator(
            PricingCatalog pricingCatalog,
            DateTimeOffset windowStart,
            DateTimeOffset windowEnd,
            int windowDurationMinutes)
        {
            _pricingCatalog = pricingCatalog;
            _includedSince = windowStart;
            _historicalWindowEnd = windowEnd;
            _historicalWindowDurationMinutes = windowDurationMinutes;
        }

        /// <summary>
        /// 设置接下来历史响应事实所属的 rollout 文件，供层级完整性诊断使用。
        /// </summary>
        /// <param name="path">当前正在重放的 rollout 绝对路径。</param>
        public void SetHistoricalSourceFile(string path)
        {
            _historicalSourceFile = path;
        }

        /// <summary>
        /// 记录文件中明确出现的服务层级证据，并按 Standard/Fast 语义归一化同义值。
        /// </summary>
        /// <param name="serviceTier">turn_context 或 thread_settings_applied 的原始层级。</param>
        public void RecordExplicitServiceTier(string serviceTier)
        {
            var normalized = serviceTier.Trim().ToLowerInvariant() switch
            {
                "standard" or "default" or "auto" => "default",
                "fast" or "priority" => "priority",
                _ => serviceTier.Trim().ToLowerInvariant()
            };
            _explicitServiceTiers.Add(normalized);
        }

        /// <summary>
        /// 仅当整个 rollout 文件的明确层级证据完全一致时返回可用于首个设置事件前的回填值。
        /// </summary>
        /// <returns>唯一明确层级；没有证据或存在层级切换时返回 null。</returns>
        public string? GetConsistentExplicitServiceTier() =>
            _explicitServiceTiers.Count == 1 ? _explicitServiceTiers.Single() : null;

        /// <summary>
        /// 判断当前文件是否出现过任何显式服务层级，避免层级切换文件被全局配置错误回填。
        /// </summary>
        public bool HasExplicitServiceTierEvidence => _explicitServiceTiers.Count > 0;

        /// <summary>
        /// 从 token_count.payload.rate_limits 提取与目标窗口时长相同的 primary 或 secondary 候选。
        /// </summary>
        /// <param name="root">包含事件时间戳的 rollout 根对象。</param>
        /// <param name="payload">token_count 的 payload 对象。</param>
        /// <returns>字段缺省或结构有效时返回 true；目标窗口字段畸形时返回 false。</returns>
        public bool TryCaptureRateLimitCheckpoint(JsonElement root, JsonElement payload)
        {
            if (_historicalWindowDurationMinutes is null)
            {
                return true;
            }

            if (!payload.TryGetProperty("rate_limits", out var rateLimits) ||
                rateLimits.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (rateLimits.ValueKind != JsonValueKind.Object || !TryReadTimestamp(root, out var timestamp))
            {
                return false;
            }

            if (timestamp < _includedSince!.Value || timestamp > _historicalWindowEnd!.Value)
            {
                return true;
            }

            return TryCaptureRateLimitWindow(rateLimits, "primary", timestamp) &&
                   TryCaptureRateLimitWindow(rateLimits, "secondary", timestamp);
        }

        /// <summary>
        /// 读取一个可选额度窗口；只有分钟数匹配历史目标时才要求百分比和重置时间完整。
        /// </summary>
        /// <param name="rateLimits">包含 primary 或 secondary 的 rate_limits 对象。</param>
        /// <param name="propertyName">需要读取的窗口属性名。</param>
        /// <param name="timestamp">额度候选的事件观察时间。</param>
        /// <returns>窗口缺省、不匹配或结构完整时返回 true。</returns>
        private bool TryCaptureRateLimitWindow(
            JsonElement rateLimits,
            string propertyName,
            DateTimeOffset timestamp)
        {
            if (!rateLimits.TryGetProperty(propertyName, out var window) ||
                window.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (window.ValueKind != JsonValueKind.Object ||
                !window.TryGetProperty("window_minutes", out var durationElement) ||
                !durationElement.TryGetInt32(out var duration))
            {
                return false;
            }

            if (duration != _historicalWindowDurationMinutes!.Value)
            {
                return true;
            }

            if (!window.TryGetProperty("used_percent", out var usedPercentElement) ||
                !usedPercentElement.TryGetDecimal(out var usedPercent) ||
                !window.TryGetProperty("resets_at", out var resetsAtElement) ||
                !resetsAtElement.TryGetInt64(out var resetsAtUnix) ||
                usedPercent < 0 ||
                usedPercent > 100)
            {
                return false;
            }

            var checkpoint = new HistoricalRateLimitCheckpoint(
                timestamp,
                usedPercent,
                duration,
                DateTimeOffset.FromUnixTimeSeconds(resetsAtUnix));
            var key = new HistoricalCheckpointKey(usedPercent, duration, resetsAtUnix);
            if (!_historicalCheckpoints.TryGetValue(key, out var existing) || timestamp < existing.Timestamp)
            {
                _historicalCheckpoints[key] = checkpoint;
            }

            return true;
        }

        /// <summary>
        /// 将一个已关联模型响应加入累计；无法公开定价时只计入不可定价统计。
        /// </summary>
        /// <param name="timestamp">逐响应用量事件时间。</param>
        /// <param name="model">响应使用的实际模型。</param>
        /// <param name="serviceTier">响应记录的实际服务层级。</param>
        /// <param name="serviceTierFromConfig">层级是否由会话创建前已生效的 config.toml 证据回填。</param>
        /// <param name="usage">逐响应 token 分类。</param>
        public void Add(
            DateTimeOffset timestamp,
            string model,
            string serviceTier,
            bool serviceTierFromConfig,
            TokenUsage usage)
        {
            if (_historicalWindowEnd is DateTimeOffset historicalWindowEnd && timestamp > historicalWindowEnd)
            {
                return;
            }

            var pricing = _pricingCatalog.Calculate(model, serviceTier, usage);
            if (_historicalWindowDurationMinutes is not null)
            {
                _historicalResponses.Add(new(
                    timestamp,
                    _historicalSourceFile,
                    model,
                    serviceTier,
                    usage,
                    pricing.Success,
                    pricing.CostUsd,
                    pricing.OfficialLongContextCostUsd,
                    pricing.NormalizedServiceTier,
                    pricing.CreditMultiplier)
                {
                    ServiceTierRecoveredFromConfig = serviceTierFromConfig
                });
            }

            if (!pricing.Success)
            {
                _unpricedCount++;
                var modelLabel = string.IsNullOrWhiteSpace(model) ? "未知模型" : model;
                _unpricedModels.Add($"{modelLabel}@{serviceTier}");
                return;
            }

            if (_retainPricingUsages) _pricingUsages.Add(new(model, serviceTier, usage));
            _usage = _usage.Add(usage);
            _costUsd += pricing.CostUsd;
            _officialLongContextCostUsd += pricing.OfficialLongContextCostUsd;
            _responseCount++;
            _models.Add(model);
            _serviceTiers.Add(FormatServiceTierEvidence(pricing.NormalizedServiceTier, serviceTierFromConfig));
            _creditMultipliers.Add($"{pricing.CreditMultiplier.ToString("0.###", CultureInfo.InvariantCulture)}x");
        }

        /// <summary>
        /// 记录一条已隔离的畸形完整 JSONL 行。
        /// </summary>
        public void AddMalformedLine() => _malformedLineCount++;

        /// <summary>
        /// 记录一个被截短或替换后从头重新读取的 rollout 文件。
        /// </summary>
        public void AddRotatedFile() => _rotatedFileCount++;

        /// <summary>
        /// 保存本轮从持久化状态清理的已删除文件游标数。
        /// </summary>
        /// <param name="count">被清理的游标数量。</param>
        public void SetPrunedCursorCount(int count) => _prunedCursorCount = count;

        /// <summary>
        /// 将内部累计转换为不可变的扫描结果。
        /// </summary>
        /// <returns>包含金额口径、来源层级和数据质量统计的扫描结果。</returns>
        public RolloutScanResult ToResult() => new(
            _usage,
            _costUsd,
            _responseCount,
            _models.OrderBy(model => model, StringComparer.OrdinalIgnoreCase).ToArray(),
            _unpricedCount,
            _unpricedModels.OrderBy(model => model, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            PricingUsages = _pricingUsages.ToArray(),
            OfficialLongContextApiEquivalentUsd = _officialLongContextCostUsd,
            ServiceTiers = _serviceTiers.OrderBy(tier => tier, StringComparer.OrdinalIgnoreCase).ToArray(),
            CreditMultipliers = _creditMultipliers.OrderBy(multiplier => multiplier, StringComparer.OrdinalIgnoreCase).ToArray(),
            PricingVersion = _pricingCatalog.PricingVersion,
            IncludedSince = _includedSince,
            MalformedLineCount = _malformedLineCount,
            RotatedFileCount = _rotatedFileCount,
            PrunedCursorCount = _prunedCursorCount
        };

        /// <summary>
        /// 将历史累加器转换为不依赖增量游标的事实集合。
        /// </summary>
        /// <param name="windowStart">当前周窗口起点。</param>
        /// <param name="windowEnd">当前权威额度快照时间。</param>
        /// <param name="filesScanned">为重放读取的 rollout 文件数。</param>
        /// <returns>可交给历史时间线计算器的事实集合。</returns>
        public HistoricalRolloutFacts ToHistoricalFacts(
            DateTimeOffset windowStart,
            DateTimeOffset windowEnd,
            int filesScanned) => new(
                windowStart,
                windowEnd,
                _historicalResponses.OrderBy(response => response.Timestamp).ToArray(),
                _historicalCheckpoints.Values.OrderBy(checkpoint => checkpoint.Timestamp).ToArray(),
                filesScanned,
                _malformedLineCount)
            {
                PricingVersion = _pricingCatalog.PricingVersion
            };
    }

    /// <summary>
    /// 标识同一额度百分比和重置承诺的重复 rollout 快照，只保留其首次观察时刻。
    /// </summary>
    private readonly record struct HistoricalCheckpointKey(
        decimal UsedPercent,
        int WindowDurationMinutes,
        long ResetsAtUnix);
}
