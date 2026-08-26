using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WeeklyQuotaMonitor;

/// <summary>
/// 通过 stdio JSONL 管理独立的 Codex App Server 进程和 account/rateLimits 请求。
/// </summary>
public sealed class CodexAppServerClient : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Process? _process;
    private Task? _stdoutLoop;
    private Task? _stderrLoop;
    private bool _processStarted;
    private int _nextRequestId;
    private int _connectionEndedSignaled;
    private int _malformedProtocolMessages;

    public event Action<JsonElement>? RateLimitsUpdated;
    public event Action<string>? ConnectionEnded;

    public int MalformedProtocolMessages => Volatile.Read(ref _malformedProtocolMessages);

    /// <summary>
    /// 判断子进程是否仍在运行且客户端尚未进入关闭流程。
    /// </summary>
    public bool IsConnected
    {
        get
        {
            var process = _process;
            return _processStarted &&
                   process is not null &&
                   !process.HasExited &&
                   !_shutdown.IsCancellationRequested;
        }
    }

    /// <summary>
    /// 启动 app-server、开始读写循环、完成 initialize/initialized 握手。
    /// </summary>
    /// <param name="executable">Codex 可执行文件名或绝对路径。</param>
    /// <param name="arguments">启动 app-server 的命令行参数。</param>
    public async Task StartAsync(string executable, string arguments)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("App Server 已启动。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            StandardInputEncoding = Utf8WithoutBom,
            StandardOutputEncoding = Utf8WithoutBom,
            StandardErrorEncoding = Utf8WithoutBom
        };

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.Exited += ProcessExited;
        if (!_process.Start())
        {
            throw new InvalidOperationException("Codex App Server 进程未能启动。");
        }
        _processStarted = true;

        _stdoutLoop = ReadStdoutAsync(_shutdown.Token);
        _stderrLoop = ReadStderrAsync(_shutdown.Token);

        await RequestAsync("initialize", new
        {
            clientInfo = new
            {
                name = "codex_weekly_quota_monitor",
                title = "Codex 周限额监控",
                version = "1.1.0"
            },
            capabilities = new
            {
                optOutNotificationMethods = new[]
                {
                    "thread/started",
                    "item/started",
                    "item/completed",
                    "item/agentMessage/delta"
                }
            }
        });
        await NotifyAsync("initialized", new { });
        RuntimeLog.Write("Codex App Server 初始化完成。");
    }

    /// <summary>
    /// 查询当前 ChatGPT 登录账户的全部额度桶和窗口。
    /// </summary>
    /// <returns>account/rateLimits/read 响应中的 result 对象。</returns>
    public Task<JsonElement> ReadRateLimitsAsync() =>
        RequestAsync("account/rateLimits/read", new { });

    /// <summary>
    /// 发送带编号 JSON-RPC 请求并等待对应 result，并在完成、失败或超时时清理等待表。
    /// </summary>
    /// <param name="method">App Server 方法名。</param>
    /// <param name="parameters">需要序列化为 params 的参数对象。</param>
    /// <returns>对应响应的 result 对象。</returns>
    private async Task<JsonElement> RequestAsync(string method, object parameters)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException($"重复的 App Server 请求编号：{id}");
        }

        try
        {
            await WriteLineAsync(JsonSerializer.Serialize(new { method, id, @params = parameters }));
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), _shutdown.Token);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// 发送不需要响应的 JSON-RPC 通知。
    /// </summary>
    /// <param name="method">App Server 方法名。</param>
    /// <param name="parameters">需要序列化为 params 的参数对象。</param>
    private Task NotifyAsync(string method, object parameters) =>
        WriteLineAsync(JsonSerializer.Serialize(new { method, @params = parameters }));

    /// <summary>
    /// 串行写入一条 JSONL 消息并立即刷新标准输入。
    /// </summary>
    /// <param name="json">不含换行的单条 JSON 消息。</param>
    private async Task WriteLineAsync(string json)
    {
        var process = _process ?? throw new InvalidOperationException("App Server 尚未启动。");
        if (!IsConnected)
        {
            throw new InvalidOperationException("App Server 连接已结束。");
        }

        await _writeGate.WaitAsync(_shutdown.Token);
        try
        {
            await process.StandardInput.WriteLineAsync(json.AsMemory(), _shutdown.Token);
            await process.StandardInput.FlushAsync(_shutdown.Token);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// 持续解析标准输出中的响应和额度更新通知；单条畸形消息被隔离并记录。
    /// </summary>
    /// <param name="cancellationToken">客户端关闭时触发的取消令牌。</param>
    private async Task ReadStdoutAsync(CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("App Server 尚未启动。");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    SignalConnectionEnded("Codex App Server 标准输出已关闭。");
                    return;
                }

                ProcessStdoutLine(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (IOException exception)
        {
            SignalConnectionEnded($"Codex App Server 标准输出读取失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 解析单条 App Server 标准输出；无效 JSON 或缺少必要字段时只计诊断，不终止读取循环。
    /// </summary>
    /// <param name="line">App Server 输出的一条完整 JSONL 消息。</param>
    private void ProcessStdoutLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                RecordMalformedProtocolMessage();
                return;
            }

            if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
            {
                CompleteRequest(root, id);
                return;
            }

            if (root.TryGetProperty("method", out var method) &&
                method.ValueKind == JsonValueKind.String &&
                method.GetString() == "account/rateLimits/updated")
            {
                if (!root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
                {
                    RecordMalformedProtocolMessage();
                    return;
                }

                RateLimitsUpdated?.Invoke(parameters.Clone());
            }
        }
        catch (JsonException)
        {
            RecordMalformedProtocolMessage();
        }
    }

    /// <summary>
    /// 增加协议畸形消息计数并写入不含原文的安全诊断。
    /// </summary>
    private void RecordMalformedProtocolMessage()
    {
        Interlocked.Increment(ref _malformedProtocolMessages);
        RuntimeLog.Write("Codex App Server 输出一条无法解析的协议消息，原文未持久化；读取循环继续运行。");
    }

    /// <summary>
    /// 消费 app-server 标准错误但不保存原文，避免把潜在敏感诊断写入监控日志。
    /// </summary>
    /// <param name="cancellationToken">客户端关闭时触发的取消令牌。</param>
    private async Task ReadStderrAsync(CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("App Server 尚未启动。");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    return;
                }

                RuntimeLog.Write("Codex App Server 写入一条 stderr 诊断，原文未持久化。");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (IOException exception)
        {
            SignalConnectionEnded($"Codex App Server 标准错误读取失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 将单个响应的 result 或 error 完成到对应等待任务。
    /// </summary>
    /// <param name="root">响应根对象。</param>
    /// <param name="id">响应请求编号。</param>
    private void CompleteRequest(JsonElement root, int id)
    {
        if (!_pending.TryRemove(id, out var completion))
        {
            return;
        }

        if (root.TryGetProperty("error", out var error))
        {
            completion.SetException(new InvalidOperationException($"App Server 请求失败：{error}"));
            return;
        }

        if (!root.TryGetProperty("result", out var result))
        {
            completion.SetException(new InvalidDataException("App Server 响应缺少 result 字段。"));
            return;
        }

        completion.SetResult(result.Clone());
    }

    /// <summary>
    /// 处理子进程退出事件，使全部未完成请求明确失败并通知上层进入断线状态。
    /// </summary>
    private void ProcessExited(object? sender, EventArgs e)
    {
        var error = new InvalidOperationException("Codex App Server 进程已退出。");
        foreach (var entry in _pending.ToArray())
        {
            if (_pending.TryRemove(entry.Key, out var completion))
            {
                completion.SetException(error);
            }
        }

        SignalConnectionEnded("Codex App Server 进程已退出。");
    }

    /// <summary>
    /// 只发送一次连接结束事件，避免 stdout 关闭和进程退出产生重复状态。
    /// </summary>
    /// <param name="message">可安全展示和持久化的连接状态。</param>
    private void SignalConnectionEnded(string message)
    {
        if (Interlocked.Exchange(ref _connectionEndedSignaled, 1) == 0)
        {
            ConnectionEnded?.Invoke(message);
        }
    }

    /// <summary>
    /// 停止读写、终止本程序创建的子进程并释放协议资源。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_processStarted && _process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        var loops = new[] { _stdoutLoop, _stderrLoop }.Where(task => task is not null).Cast<Task>().ToArray();
        if (loops.Length > 0)
        {
            await Task.WhenAll(loops);
        }

        if (_process is not null)
        {
            _process.Exited -= ProcessExited;
            _process.Dispose();
        }
        _writeGate.Dispose();
        _shutdown.Dispose();
    }
}
