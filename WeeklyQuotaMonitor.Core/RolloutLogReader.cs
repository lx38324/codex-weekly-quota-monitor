using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WeeklyQuotaMonitor.Core;

/// <summary>
/// 按持久化字节游标增量读取 Codex rollout JSONL，并关联本地模型响应与 token_count。
/// </summary>
public sealed class RolloutLogReader
{
    public const int CurrentServiceTierTrackingVersion = 1;
    public const int CurrentResponseAssociationTrackingVersion = 1;

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
        var malformedLines = 0;
        var contextCutoff = DateTime.UtcNow.AddHours(-contextLookbackHours);
        foreach (var path in EnumerateRolloutFiles(sessionRoot))
        {
            var info = new FileInfo(path);
            var cursor = new FileCursorState { CreationTimeUtcTicks = info.CreationTimeUtc.Ticks };
            if (info.LastWriteTimeUtc >= contextCutoff)
            {
                var chunk = ReadCompleteLines(path, 0);
                foreach (var line in chunk.Lines)
                {
                    if (!ProcessLine(line, cursor, null, null))
                    {
                        malformedLines++;
                    }
                }

                cursor.Offset = chunk.NextOffset;
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
        var accumulator = new ScanAccumulator(includeSince);
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

            if (info.Length == cursor.Offset)
            {
                continue;
            }

            var chunk = ReadCompleteLines(path, cursor.Offset);
            foreach (var line in chunk.Lines)
            {
                if (!ProcessLine(line, cursor, accumulator, includeSince))
                {
                    accumulator.AddMalformedLine();
                }
            }

            cursor.Offset = chunk.NextOffset;
        }

        return accumulator.ToResult();
    }

    /// <summary>
    /// 从当前周窗口内仍有写入的 rollout 文件重读完整上下文，提取可全局排序的响应事实和额度候选。
    /// </summary>
    /// <param name="sessionRoot">Codex sessions 根目录。</param>
    /// <param name="windowStart">当前周窗口起点。</param>
    /// <param name="windowEnd">当前权威额度快照时间。</param>
    /// <param name="windowDurationMinutes">需要提取的周窗口分钟数。</param>
    /// <returns>不修改增量游标的历史事实和完整性诊断。</returns>
    public HistoricalRolloutFacts ReadHistoricalFacts(
        string sessionRoot,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int windowDurationMinutes)
    {
        if (windowEnd < windowStart)
        {
            throw new ArgumentOutOfRangeException(nameof(windowEnd), "历史重放结束时间不能早于窗口起点。");
        }

        if (windowDurationMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowDurationMinutes));
        }

        var accumulator = new ScanAccumulator(
            windowStart,
            windowEnd,
            windowDurationMinutes);
        var paths = EnumerateRolloutFiles(sessionRoot)
            .Where(path => new FileInfo(path).LastWriteTimeUtc >= windowStart.UtcDateTime)
            .ToArray();
        foreach (var path in paths)
        {
            var chunk = ReadCompleteLines(path, 0);
            var tierProbe = new ScanAccumulator(null);
            var tierProbeCursor = new FileCursorState();
            foreach (var line in chunk.Lines)
            {
                ProcessLine(line, tierProbeCursor, tierProbe, null);
            }

            var cursor = new FileCursorState
            {
                CurrentServiceTier = tierProbe.GetConsistentExplicitServiceTier() ?? "unknown"
            };
            accumulator.SetHistoricalSourceFile(path);
            foreach (var line in chunk.Lines)
            {
                if (!ProcessLine(line, cursor, accumulator, windowStart))
                {
                    accumulator.AddMalformedLine();
                }
            }
        }

        return accumulator.ToHistoricalFacts(windowStart, windowEnd, paths.Length);
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
    /// 从指定字节位置读取到当前文件末尾，但只返回以换行结束的完整 UTF-8 JSONL 行。
    /// </summary>
    /// <param name="path">需要读取的 rollout 文件。</param>
    /// <param name="offset">已持久化的起始字节位置。</param>
    /// <returns>完整行集合和下次读取应使用的字节位置。</returns>
    private static FileChunk ReadCompleteLines(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var available = stream.Length - offset;
        if (available <= 0)
        {
            return new([], offset);
        }

        if (available > int.MaxValue)
        {
            throw new InvalidDataException($"单次新增日志超过 2 GiB：{path}");
        }

        stream.Position = offset;
        var bytes = new byte[(int)available];
        stream.ReadExactly(bytes);
        var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
        if (lastNewline < 0)
        {
            return new([], offset);
        }

        var text = Encoding.UTF8.GetString(bytes, 0, lastNewline + 1);
        var lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .ToArray();
        return new(lines, offset + lastNewline + 1);
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

        accumulator.Add(responseTimestamp, responseModel, responseServiceTier, usage);
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
    /// 保存一次文件读取形成的完整行和下一字节位置。
    /// </summary>
    private sealed record FileChunk(IReadOnlyList<string> Lines, long NextOffset);

    /// <summary>
    /// 在一次扫描中累积可定价响应、不可定价响应、模型口径和数据质量统计。
    /// </summary>
    private sealed class ScanAccumulator
    {
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
        private readonly List<HistoricalRateLimitCheckpoint> _historicalCheckpoints = [];
        private string _historicalSourceFile = string.Empty;

        /// <summary>
        /// 创建一次扫描累加器并保存本轮用于重置窗口切分的最早时间。
        /// </summary>
        /// <param name="includedSince">仅纳入计价的最早 token_count 时间。</param>
        public ScanAccumulator(DateTimeOffset? includedSince)
        {
            _includedSince = includedSince;
        }

        /// <summary>
        /// 创建历史事实累加器，并限定额度候选和响应事实的绝对时间窗口。
        /// </summary>
        /// <param name="windowStart">当前周窗口起点。</param>
        /// <param name="windowEnd">当前权威额度快照时间。</param>
        /// <param name="windowDurationMinutes">需要提取的周窗口分钟数。</param>
        public ScanAccumulator(
            DateTimeOffset windowStart,
            DateTimeOffset windowEnd,
            int windowDurationMinutes)
        {
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

            _historicalCheckpoints.Add(new(
                timestamp,
                usedPercent,
                duration,
                DateTimeOffset.FromUnixTimeSeconds(resetsAtUnix)));
            return true;
        }

        /// <summary>
        /// 将一个已关联模型响应加入累计；无法公开定价时只计入不可定价统计。
        /// </summary>
        /// <param name="timestamp">逐响应用量事件时间。</param>
        /// <param name="model">响应使用的实际模型。</param>
        /// <param name="serviceTier">响应记录的实际服务层级。</param>
        /// <param name="usage">逐响应 token 分类。</param>
        public void Add(
            DateTimeOffset timestamp,
            string model,
            string serviceTier,
            TokenUsage usage)
        {
            if (_historicalWindowEnd is DateTimeOffset historicalWindowEnd && timestamp > historicalWindowEnd)
            {
                return;
            }

            var pricing = PublicApiPricing.Calculate(model, serviceTier, usage);
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
                    pricing.CreditMultiplier));
            }

            if (!pricing.Success)
            {
                _unpricedCount++;
                var modelLabel = string.IsNullOrWhiteSpace(model) ? "未知模型" : model;
                _unpricedModels.Add($"{modelLabel}@{serviceTier}");
                return;
            }

            _usage = _usage.Add(usage);
            _costUsd += pricing.CostUsd;
            _officialLongContextCostUsd += pricing.OfficialLongContextCostUsd;
            _responseCount++;
            _models.Add(model);
            _serviceTiers.Add(pricing.NormalizedServiceTier);
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
            OfficialLongContextApiEquivalentUsd = _officialLongContextCostUsd,
            ServiceTiers = _serviceTiers.OrderBy(tier => tier, StringComparer.OrdinalIgnoreCase).ToArray(),
            CreditMultipliers = _creditMultipliers.OrderBy(multiplier => multiplier, StringComparer.OrdinalIgnoreCase).ToArray(),
            PricingVersion = PublicApiPricing.PricingVersion,
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
                _historicalCheckpoints.OrderBy(checkpoint => checkpoint.Timestamp).ToArray(),
                filesScanned,
                _malformedLineCount);
    }
}
