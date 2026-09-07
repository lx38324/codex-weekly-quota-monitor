using System.Text.Json;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor;

/// <summary>
/// 协调额度查询、变化触发日志扫描、样本生成、状态持久化和界面快照更新。
/// </summary>
public sealed class MonitorCoordinator : IAsyncDisposable
{
    private readonly JsonStorage _storage;
    private RolloutLogReader _rolloutReader;
    private PricingCatalog _pricingCatalog;
    private readonly SynchronizationContext _uiContext;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private CodexAppServerClient? _appServer;
    private FileSystemWatcher? _rolloutWatcher;
    private volatile bool _rolloutFilesChanged;
    private bool _polling;
    private bool _refreshQueued;
    private CancellationTokenSource? _replayCancellation;
    private MonitorViewSnapshot? _retainedPricingView;
    private bool _priceRebuildPending;
    private string _repricingNote = string.Empty;
    private string? _repriceSourceVersion;
    private bool _notificationProcessing;
    private bool _disposed;
    private bool _restartConnectionRequested;
    private int _consecutiveConnectionFailures;
    private int _malformedAppServerMessages;
    private DateTimeOffset _nextReconnectAttempt = DateTimeOffset.MinValue;
    private MonitorState _state;
    private RateLimitSnapshot? _lastRateLimit;

    public AppSettings Settings { get; private set; }
    public MonitorViewSnapshot CurrentView { get; private set; }

    public event Action<MonitorViewSnapshot>? ViewUpdated;

    /// <summary>
    /// 加载本地设置和状态，并建立负责低频额度轮询的 WinForms 定时器。
    /// </summary>
    /// <param name="uiContext">托盘消息循环使用的 UI 同步上下文。</param>
    public MonitorCoordinator(SynchronizationContext uiContext)
    {
        _uiContext = uiContext;
        _storage = new JsonStorage();
        Settings = _storage.LoadSettings();
        _pricingCatalog = PublicApiPricing.CreateCatalog(Settings.ModelPrices);
        _rolloutReader = new RolloutLogReader(_pricingCatalog);
        _state = _storage.LoadState(_pricingCatalog.PricingVersion);
        _pollTimer = new System.Windows.Forms.Timer();
        _pollTimer.Tick += PollTimerTick;
        CurrentView = BuildView("正在启动监控。");
    }

    /// <summary>
    /// 建立 rollout 基线、应用开机启动、启动轮询并尝试完成首次额度读取。
    /// </summary>
    public async Task StartAsync()
    {
        var errors = Settings.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }

        AutostartManager.Apply(Settings.StartWithWindows);
        _pollTimer.Interval = checked(Settings.PollIntervalSeconds * 1000);
        PrimeRolloutFilesIfNeeded();
        ConfigureRolloutWatcher();
        _pollTimer.Start();
        await RefreshNowAsync();
    }

    /// <summary>
    /// 首次启动时建立 rollout 文件基线；可预期的路径或读取错误会转为可见状态并等待后续轮询重试。
    /// </summary>
    private void PrimeRolloutFilesIfNeeded()
    {
        if (_state.RolloutFilesPrimed)
        {
            return;
        }

        try
        {
            _rolloutReader.PrimeExistingFiles(Settings.SessionRoot, _state, Settings.InitialContextLookbackHours);
            _storage.SaveState(_state);
            RuntimeLog.Write("已建立 rollout 增量游标基线；当前周历史将由版本化重放单独重建。");
        }
        catch (Exception exception) when (
            exception is DirectoryNotFoundException or
            UnauthorizedAccessException or
            IOException or
            InvalidDataException)
        {
            PublishView($"rollout 基线建立失败：{exception.Message}；后续轮询将继续重试。");
        }
    }

    /// <summary>
    /// 立即查询额度；连接失败会解除轮询锁、进入退避并由后续轮询自动重连。
    /// </summary>
    public async Task RefreshNowAsync()
    {
        if (_disposed)
        {
            return;
        }
        if (_polling || _notificationProcessing)
        {
            _refreshQueued = true;
            return;
        }

        _polling = true;
        try
        {
            PrimeRolloutFilesIfNeeded();
            JsonElement result;
            try
            {
                var appServer = await EnsureAppServerConnectedAsync();
                if (appServer is null)
                {
                    return;
                }

                result = await appServer.ReadRateLimitsAsync();
            }
            catch (Exception exception) when (IsConnectionFailure(exception))
            {
                await EnterDisconnectedStateAsync(exception.Message);
                return;
            }

            try
            {
                await HandleRateLimitContainerAsync(result, "轮询");
            }
            catch (Exception exception) when (IsLocalDataFailure(exception))
            {
                PublishView($"本地日志或状态处理失败：{exception.Message}；App Server 连接保持，下一轮将重试。");
            }
        }
        finally
        {
            _polling = false;
            ScheduleQueuedRefresh();
        }
    }

    /// <summary>
    /// 当前工作释放互斥标志后立即处理最后一次查询请求，避免改价期间的请求被静默丢弃。
    /// </summary>
    private void ScheduleQueuedRefresh()
    {
        if (!_refreshQueued || _disposed || _polling || _notificationProcessing) return;
        _refreshQueued = false;
        _uiContext.Post(async _ => await RefreshNowAsync(), null);
    }

    /// <summary>
    /// 返回已连接客户端；断线、配置变化或 Codex Desktop 更新后创建一个新的 App Server 客户端。
    /// </summary>
    /// <returns>可查询额度的客户端；退避期或无法解析 Codex 路径时返回 null。</returns>
    private async Task<CodexAppServerClient?> EnsureAppServerConnectedAsync()
    {
        if (_appServer?.IsConnected == true && !_restartConnectionRequested)
        {
            return _appServer;
        }

        if (DateTimeOffset.Now < _nextReconnectAttempt)
        {
            return null;
        }

        await DisposeAppServerAsync();
        var executableResolution = CodexExecutableResolver.Resolve(Settings.CodexExecutable);
        if (!executableResolution.Success)
        {
            ScheduleReconnect(executableResolution.Status);
            return null;
        }

        if (!string.Equals(
                Settings.CodexExecutable,
                executableResolution.PersistedSetting,
                StringComparison.Ordinal))
        {
            Settings.CodexExecutable = executableResolution.PersistedSetting;
            _storage.SaveSettings(Settings);
            RuntimeLog.Write($"Codex 路径设置已迁移为稳定定位符：{executableResolution.PersistedSetting}");
        }

        RuntimeLog.Write(executableResolution.Status);
        var candidate = new CodexAppServerClient();
        candidate.RateLimitsUpdated += OnRateLimitsUpdatedFromBackground;
        candidate.ConnectionEnded += OnConnectionEndedFromBackground;
        _appServer = candidate;
        await candidate.StartAsync(executableResolution.ExecutablePath, Settings.CodexArguments);
        _consecutiveConnectionFailures = 0;
        _nextReconnectAttempt = DateTimeOffset.MinValue;
        _restartConnectionRequested = false;
        PublishView("App Server 已连接。");
        return candidate;
    }

    /// <summary>
    /// 判断异常是否属于启动、协议请求或连接生命周期中的可恢复失败。
    /// </summary>
    /// <param name="exception">需要分类的异常。</param>
    /// <returns>后续轮询可通过重建 App Server 恢复时返回 true。</returns>
    private static bool IsConnectionFailure(Exception exception) =>
        exception is System.ComponentModel.Win32Exception or
        TimeoutException or
        InvalidOperationException or
        InvalidDataException or
        IOException or
        OperationCanceledException;

    /// <summary>
    /// 判断异常是否来自本地 rollout、额度 JSON 或状态文件处理。
    /// </summary>
    /// <param name="exception">需要分类的异常。</param>
    /// <returns>保持 App Server 连接并等待下一轮重试更合适时返回 true。</returns>
    private static bool IsLocalDataFailure(Exception exception) =>
        exception is DirectoryNotFoundException or
        UnauthorizedAccessException or
        IOException or
        InvalidDataException or
        JsonException or
        FormatException or
        OverflowException;

    /// <summary>
    /// 释放失败连接、计算退避时间并发布下一次自动重连状态。
    /// </summary>
    /// <param name="reason">不包含凭据或协议原文的失败原因。</param>
    private async Task EnterDisconnectedStateAsync(string reason)
    {
        await DisposeAppServerAsync();
        ScheduleReconnect(reason);
    }

    /// <summary>
    /// 根据连续失败次数设置有上限的指数退避，并向托盘发布下次重连时间。
    /// </summary>
    /// <param name="reason">导致本次退避的可见原因。</param>
    private void ScheduleReconnect(string reason)
    {
        _consecutiveConnectionFailures++;
        var exponent = Math.Min(_consecutiveConnectionFailures - 1, 4);
        var baseSeconds = Math.Max(Settings.PollIntervalSeconds, 15);
        var delaySeconds = Math.Min(baseSeconds * (1 << exponent), 300);
        _nextReconnectAttempt = DateTimeOffset.Now.AddSeconds(delaySeconds);
        PublishView($"App Server 未连接：{reason}；将在 {_nextReconnectAttempt.LocalDateTime:HH:mm:ss} 后自动重试。");
    }

    /// <summary>
    /// 取消事件订阅并释放当前 App Server 客户端；重复调用是安全的。
    /// </summary>
    private async Task DisposeAppServerAsync()
    {
        var appServer = _appServer;
        _appServer = null;
        if (appServer is null)
        {
            return;
        }

        appServer.RateLimitsUpdated -= OnRateLimitsUpdatedFromBackground;
        appServer.ConnectionEnded -= OnConnectionEndedFromBackground;
        _malformedAppServerMessages += appServer.MalformedProtocolMessages;
        await appServer.DisposeAsync();
    }

    /// <summary>
    /// 保存设置并立即应用轮询、回归和开机启动参数；协议参数变化会在下一轮自动重建连接。
    /// </summary>
    /// <param name="settings">设置窗口提交的新设置。</param>
    public void ApplySettings(AppSettings settings)
    {
        var pricingCatalog = PublicApiPricing.CreateCatalog(settings.ModelPrices);
        var pricingChanged = !string.Equals(
            _pricingCatalog.PricingVersion,
            pricingCatalog.PricingVersion,
            StringComparison.Ordinal);
        _storage.SaveSettings(settings);
        AutostartManager.Apply(settings.StartWithWindows);
        var sessionRootChanged =
            !string.Equals(Settings.SessionRoot, settings.SessionRoot, StringComparison.OrdinalIgnoreCase);
        var protocolChanged =
            !string.Equals(Settings.CodexExecutable, settings.CodexExecutable, StringComparison.Ordinal) ||
            !string.Equals(Settings.CodexArguments, settings.CodexArguments, StringComparison.Ordinal) ||
            sessionRootChanged;
        Settings = settings;
        if (pricingChanged)
        {
            _replayCancellation?.Cancel();
            if (CurrentView.SampleCount > 0) _retainedPricingView = CurrentView;
            _repriceSourceVersion = CurrentView.PricingVersion;
            _priceRebuildPending = true;
            _pricingCatalog = pricingCatalog;
            _rolloutReader = new RolloutLogReader(_pricingCatalog);
            QuotaEstimator.ResetIncompatiblePendingInterval(_state, _pricingCatalog.PricingVersion);
            var repriced = SampleRepricer.Apply(_state, _pricingCatalog, _repriceSourceVersion);
            _repricingNote = FormatRepricingResult(repriced);
            if (repriced.MissingUsageIntervals == 0 && repriced.Errors.Count == 0)
            {
                // 只有已完成的同算法历史才允许复用覆盖范围；首次升级仍会扫描补齐明细。
                _state.HistoricalReplayPricingVersion = _pricingCatalog.PricingVersion;
                _state.HistoricalArchiveReplayPricingVersion = _pricingCatalog.PricingVersion;
                _priceRebuildPending = false;
                _retainedPricingView = null;
            }
            _storage.SaveState(_state);
        }

        _pollTimer.Interval = checked(Settings.PollIntervalSeconds * 1000);
        if (sessionRootChanged)
        {
            ConfigureRolloutWatcher();
        }

        if (protocolChanged)
        {
            _replayCancellation?.Cancel();
            _restartConnectionRequested = true;
            _nextReconnectAttempt = DateTimeOffset.MinValue;
        }

        PublishView((protocolChanged, pricingChanged) switch
        {
            (true, true) => "设置已保存；连接参数与模型价格将在下一轮轮询重建并重算历史。",
            (true, false) => "设置已保存；协议或路径变化将在下一轮轮询自动重建连接。",
            (false, true) => "设置已保存；已尝试离线重算，缺失明细将在额度查询成功后补录。",
            _ => "设置已保存并应用。"
        });
    }

    /// <summary>
    /// 处理定时器触发的低频额度查询；RefreshNowAsync 会吸收所有已分类的可恢复失败。
    /// </summary>
    private async void PollTimerTick(object? sender, EventArgs e)
    {
        await RefreshNowAsync();
    }

    /// <summary>
    /// 把后台读取线程收到的额度更新通知投递到 WinForms UI 上下文。
    /// </summary>
    /// <param name="container">通知 params 的克隆 JSON。</param>
    private void OnRateLimitsUpdatedFromBackground(JsonElement container)
    {
        _uiContext.Post(_ => HandleNotificationOnUi(container), null);
    }

    /// <summary>
    /// 在 UI 线程处理额度通知，并把本地数据错误转换为可见状态。
    /// </summary>
    /// <param name="container">通知 params 的克隆 JSON。</param>
    private async void HandleNotificationOnUi(JsonElement container)
    {
        if (_polling || _notificationProcessing)
        {
            return;
        }

        _notificationProcessing = true;
        try
        {
            await HandleRateLimitContainerAsync(container, "服务端通知");
        }
        catch (Exception exception) when (IsLocalDataFailure(exception))
        {
            PublishView($"服务端通知的本地处理失败：{exception.Message}；下一轮轮询将重试。");
        }
        finally
        {
            _notificationProcessing = false;
            ScheduleQueuedRefresh();
        }
    }

    /// <summary>
    /// 把 App Server 连接结束状态投递到 UI，并允许下一轮轮询立即重建连接。
    /// </summary>
    /// <param name="message">连接结束原因。</param>
    private void OnConnectionEndedFromBackground(string message)
    {
        _uiContext.Post(_ =>
        {
            _nextReconnectAttempt = DateTimeOffset.MinValue;
            PublishView($"{message} 下一轮轮询将自动重连。");
        }, null);
    }

    /// <summary>
    /// 选择周额度窗口、按变化决定是否扫描日志、生成样本并持久化状态。
    /// </summary>
    /// <param name="container">account/rateLimits 的 result 或通知 params。</param>
    /// <param name="source">用于状态栏区分轮询和服务端通知的来源。</param>
    private async Task HandleRateLimitContainerAsync(JsonElement container, string source)
    {
        var snapshot = RateLimitParser.Parse(
            container,
            Settings.PreferredLimitId,
            Settings.MinimumWindowMinutes,
            DateTimeOffset.Now);
        if (snapshot is null)
        {
            PublishView($"{source}未返回满足条件的额度窗口；请检查额度桶和最短窗口设置。");
            return;
        }

        var authoritativeTimelineChanged =
            HistoricalReplayCalculator.RecordAuthoritativeCheckpoint(_state, snapshot);
        var scanStart = QuotaEstimator.GetRolloutScanStart(_state, snapshot);
        var scan = QuotaEstimator.RequiresRolloutScan(_state, snapshot)
            ? _rolloutReader.ScanNew(Settings.SessionRoot, _state, scanStart)
            : RolloutScanResult.Empty;
        var update = QuotaEstimator.Process(
            _state,
            snapshot,
            scan,
            Settings.MinimumPercentDelta,
            _pricingCatalog.PricingVersion);
        var replayStatus = await ReplayHistoryIfRequiredAsync(snapshot, authoritativeTimelineChanged);
        _lastRateLimit = snapshot;
        PruneExpiredSamples();
        _storage.SaveState(_state);
        PublishView($"{source}：{replayStatus}{update.Status}");
    }

    /// <summary>
    /// 在重放算法或价格版本升级后，从当前周 rollout 自动重建历史样本并记录幂等迁移版本。
    /// </summary>
    /// <param name="snapshot">当前 App Server 权威周额度快照。</param>
    /// <returns>本轮完成重放时返回可见状态前缀，否则返回空字符串。</returns>
    private async Task<string> ReplayHistoryIfRequiredAsync(
        RateLimitSnapshot snapshot,
        bool authoritativeTimelineChanged)
    {
        var roots = RolloutLogReader.DiscoverHistoricalSessionRoots(Settings.SessionRoot);
        var catalog = _pricingCatalog;
        var historyDays = Settings.ChartHistoryDays;
        var sessionRoot = Settings.SessionRoot;
        var archiveRequired = HistoricalArchiveReplayCalculator.IsReplayRequired(
            _state, historyDays, roots, catalog.PricingVersion);
        var currentRequired = HistoricalReplayCalculator.IsReplayRequired(_state, catalog.PricingVersion) ||
            authoritativeTimelineChanged ||
            RolloutLogReader.HaveTrackedFilesChanged(_state.HistoricalReplayUnresolvedFileLengths) ||
            (_rolloutFilesChanged && _state.HistoricalReplayAwaitingLogIntervals > 0);
        _rolloutFilesChanged = false;
        if (!archiveRequired && !currentRequired)
        {
            _priceRebuildPending = false;
            return string.Empty;
        }

        using var cancellation = new CancellationTokenSource();
        _replayCancellation = cancellation;
        var token = cancellation.Token;
        var statuses = new List<string>();
        var windowStart = QuotaEstimator.GetEffectiveWindowStart(snapshot);
        try
        {
            if (currentRequired)
            {
                PublishView(UiText.Get("RepricingCurrent"));
                var progress = CreateReplayProgress("RepricingCurrent", cancellation);
                var facts = await Task.Run(() => new RolloutLogReader(catalog).ReadHistoricalFacts(
                    sessionRoot, windowStart, snapshot.SampledAt, snapshot.WindowDurationMinutes, token, progress), token);
                token.ThrowIfCancellationRequested();
                var replay = HistoricalReplayCalculator.Build(snapshot, facts, _state.AuthoritativeRateLimitCheckpoints);
                HistoricalReplayCalculator.ApplyToState(_state, replay);
                _state.HistoricalReplayUnresolvedFileLengths = RolloutLogReader.CaptureFileLengths(replay.UnresolvedSourceFiles);
                _storage.SaveState(_state);
                statuses.Add($"当前周重放生成 {replay.Samples.Count} 个样本，未归因区间 {replay.UnattributedIntervalCount}；");
            }

            if (archiveRequired)
            {
                PublishView(UiText.Get("RepricingArchive"));
                var historyStart = DateTimeOffset.Now.AddDays(-historyDays);
                var progress = CreateReplayProgress("RepricingArchive", cancellation);
                var archive = historyStart < windowStart
                    ? await Task.Run(() =>
                    {
                        var facts = new RolloutLogReader(catalog).ReadHistoricalFacts(
                            roots, historyStart, windowStart, snapshot.WindowDurationMinutes, token, progress);
                        token.ThrowIfCancellationRequested();
                        return HistoricalArchiveReplayCalculator.Build(snapshot.LimitId, snapshot.WindowDurationMinutes, facts, windowStart);
                    }, token)
                    : new HistoricalArchiveReplayResult(historyStart, windowStart, snapshot.LimitId, [], 0, 0)
                    { PricingVersion = catalog.PricingVersion };
                token.ThrowIfCancellationRequested();
                HistoricalArchiveReplayCalculator.ApplyToState(_state, archive, historyDays, roots);
                _storage.SaveState(_state);
                statuses.Add($"旧窗口重建 {archive.Windows.Count} 周、{archive.Windows.Sum(window => window.Samples.Count)} 个样本；");
            }

            if (_priceRebuildPending)
            {
                var repriced = SampleRepricer.Apply(_state, catalog, _repriceSourceVersion);
                _repricingNote = FormatRepricingResult(repriced);
                _priceRebuildPending = false;
                if (_state.Samples.Any(catalog.IsCurrentSample)) _retainedPricingView = null;
            }
            return string.Concat(statuses);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 只有用户换价或退出触发的取消才在此处理；实际扫描失败仍沿原异常路径报告。
            if (!_disposed) _refreshQueued = true;
            return UiText.Get("RepricingCancelled");
        }
        finally
        {
            if (ReferenceEquals(_replayCancellation, cancellation)) _replayCancellation = null;
        }
    }

    /// <summary>
    /// 创建最多每秒一次的文件扫描进度；过时任务的已排队通知不得覆盖最新价格界面。
    /// </summary>
    private IProgress<(int Completed, int Total)> CreateReplayProgress(string labelKey, CancellationTokenSource owner)
    {
        var lastUpdate = DateTimeOffset.MinValue;
        return new Progress<(int Completed, int Total)>(value =>
        {
            if (_disposed || !ReferenceEquals(_replayCancellation, owner) || owner.IsCancellationRequested) return;
            var now = DateTimeOffset.Now;
            if (value.Completed != value.Total && now - lastUpdate < TimeSpan.FromSeconds(1)) return;
            lastUpdate = now;
            PublishView($"{UiText.Get(labelKey)} {value.Completed}/{value.Total}");
        });
    }

    /// <summary>汇总离线重算的成功、缺失明细和失败原因，明确区分扫描结束与全部覆盖。</summary>
    private static string FormatRepricingResult(RepricingResult result) =>
        UiText.Format("RepricingCoverage", result.PricedIntervals, result.MissingUsageIntervals, result.Errors.Count) +
        (result.Errors.Count > 0 ? " " + string.Join(" ", result.Errors.Take(3)) : string.Empty);

    /// <summary>
    /// 监听 sessions 目录中新建和追加的 JSONL，供待日志或待层级区间在下一次额度查询时按需重放。
    /// </summary>
    private void ConfigureRolloutWatcher()
    {
        _rolloutWatcher?.Dispose();
        _rolloutWatcher = new FileSystemWatcher(Settings.SessionRoot, "*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _rolloutWatcher.Changed += MarkRolloutFilesChanged;
        _rolloutWatcher.Created += MarkRolloutFilesChanged;
        _rolloutWatcher.Deleted += MarkRolloutFilesChanged;
        _rolloutWatcher.Renamed += MarkRolloutFilesChanged;
        _rolloutWatcher.Error += MarkRolloutWatcherError;
        _rolloutFilesChanged = true;
    }

    /// <summary>
    /// 标记至少一个 rollout 文件发生变化；实际重放仍由 UI 线程上的额度轮询串行执行。
    /// </summary>
    /// <param name="sender">触发文件事件的监视器。</param>
    /// <param name="e">发生变化的文件事件。</param>
    private void MarkRolloutFilesChanged(object sender, FileSystemEventArgs e) =>
        _rolloutFilesChanged = true;

    /// <summary>
    /// 文件监视器报告缓冲区错误时请求下一轮重新检查历史事实，不在后台线程读取状态。
    /// </summary>
    /// <param name="sender">报告错误的文件监视器。</param>
    /// <param name="e">监视器错误事件。</param>
    private void MarkRolloutWatcherError(object sender, ErrorEventArgs e) =>
        _rolloutFilesChanged = true;

    /// <summary>
    /// 根据图表保留天数删除过期样本，限制长期常驻状态文件大小。
    /// </summary>
    private void PruneExpiredSamples()
    {
        var cutoff = DateTimeOffset.Now.AddDays(-Settings.ChartHistoryDays);
        _state.Samples.RemoveAll(sample => sample.Timestamp < cutoff);
    }

    /// <summary>
    /// 重建回归曲线和托盘展示快照，并通知所有已打开窗口刷新。
    /// </summary>
    /// <param name="status">本轮可见运行状态。</param>
    private void PublishView(string status)
    {
        CurrentView = BuildView(status);
        RuntimeLog.Write(status);
        ViewUpdated?.Invoke(CurrentView);
    }

    /// <summary>
    /// 从最新额度、当前价格版本有效样本和运行诊断构造只读展示模型。
    /// </summary>
    /// <param name="status">本轮可见运行状态。</param>
    /// <returns>托盘和窗口共用的展示快照。</returns>
    private MonitorViewSnapshot BuildView(string status)
    {
        var allSamples = _state.Samples.OrderBy(sample => sample.Timestamp).ToArray();
        var samples = allSamples.Where(_pricingCatalog.IsCurrentSample).ToArray();
        var displayedVersion = _pricingCatalog.PricingVersion;
        if (_retainedPricingView is not null && (_priceRebuildPending || samples.Length == 0))
        {
            samples = _retainedPricingView.Samples.ToArray();
            displayedVersion = _retainedPricingView.PricingVersion;
        }
        else if (samples.Length == 0 && allSamples.Length > 0)
        {
            // 重启发生在换价与扫描完成之间时，也保留持久化的旧价格结果供用户查看。
            var previous = allSamples.Where(sample => !string.IsNullOrEmpty(sample.PricingVersion))
                .GroupBy(sample => sample.PricingVersion)
                .OrderByDescending(group => group.Key == _state.HistoricalReplayPricingVersion)
                .ThenByDescending(group => group.Max(sample => sample.Timestamp)).FirstOrDefault();
            if (previous is not null)
            {
                samples = previous.ToArray();
                displayedVersion = previous.Key;
            }
        }
        if (displayedVersion != _pricingCatalog.PricingVersion)
            status = UiText.Get("RepricingShowingPrevious") + " " + status;
        if (!string.IsNullOrEmpty(_repricingNote)) status += " " + _repricingNote;
        var curve = RegressionCalculator.BuildCurve(
            samples,
            Settings.Regression,
            displayedVersion);
        var officialLongContextCurve =
            RegressionCalculator.BuildOfficialLongContextCurve(
                samples,
                Settings.Regression,
                displayedVersion);
        return new(
            DateTimeOffset.Now,
            status,
            _lastRateLimit,
            RegressionCalculator.CurrentEstimate(curve),
            samples.Length,
            _state.UnattributedPercentChanges,
            _state.UnpricedModelResponses,
            samples,
            curve)
        {
            PricingVersion = displayedVersion,
            ShowingPreviousPrices = displayedVersion != _pricingCatalog.PricingVersion,
            OfficialLongContextEstimatedWeeklyQuotaUsd =
                RegressionCalculator.CurrentEstimate(officialLongContextCurve),
            OfficialLongContextRegressionCurve = officialLongContextCurve,
            ArchivedSampleCount = allSamples.Length - samples.Length,
            MalformedRolloutLines = _state.MalformedRolloutLines,
            RotatedRolloutFiles = _state.RotatedRolloutFiles,
            PrunedRolloutCursors = _state.PrunedRolloutCursors,
            MalformedAppServerMessages = _malformedAppServerMessages + (_appServer?.MalformedProtocolMessages ?? 0),
            AppServerConnected = _appServer?.IsConnected == true,
            HistoricalReplayCompletedAt = _state.HistoricalReplayCompletedAt,
            HistoricalReplayAcceptedCheckpoints = _state.HistoricalReplayAcceptedCheckpoints,
            HistoricalReplayRejectedCheckpoints = _state.HistoricalReplayRejectedCheckpoints,
            HistoricalReplayUnpricedResponses = _state.HistoricalReplayUnpricedResponses,
            HistoricalReplayUnattributedIntervals = _state.HistoricalReplayUnattributedIntervals,
            HistoricalReplayAwaitingLogIntervals = _state.HistoricalReplayAwaitingLogIntervals,
            HistoricalReplayUnattributedUsedPercents =
                _state.HistoricalReplayUnattributedUsedPercents.ToArray(),
            HistoricalReplayMalformedLines = _state.HistoricalReplayMalformedLines,
            HistoricalArchiveReplayCompletedAt = _state.HistoricalArchiveReplayCompletedAt,
            HistoricalArchiveReplayWindowCount = _state.HistoricalArchiveReplayWindowCount,
            HistoricalArchiveReplaySampleCount = _state.HistoricalArchiveReplaySampleCount,
            HistoricalArchiveReplayFilesScanned = _state.HistoricalArchiveReplayFilesScanned,
            HistoricalArchiveReplayUnpricedResponses = _state.HistoricalArchiveReplayUnpricedResponses,
            HistoricalArchiveReplayUnattributedIntervals = _state.HistoricalArchiveReplayUnattributedIntervals,
            HistoricalArchiveReplayMalformedLines = _state.HistoricalArchiveReplayMalformedLines
        };
    }

    /// <summary>
    /// 停止轮询并释放本程序创建的 App Server 子进程。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _replayCancellation?.Cancel();
        _pollTimer.Stop();
        _pollTimer.Dispose();
        _rolloutWatcher?.Dispose();
        await DisposeAppServerAsync();
    }
}
