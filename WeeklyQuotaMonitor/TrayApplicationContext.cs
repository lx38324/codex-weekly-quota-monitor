using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor;

/// <summary>
/// 管理托盘图标、悬停摘要、双击入口、右键菜单及各个辅助窗口。
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly MonitorCoordinator _coordinator;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly DetailForm _detailForm;
    private readonly ChartForm _chartForm;
    private readonly ToolStripMenuItem _refreshItem = new();
    private readonly ToolStripMenuItem _detailsItem = new();
    private readonly ToolStripMenuItem _dashboardItem = new();
    private readonly ToolStripMenuItem _settingsItem = new();
    private readonly ToolStripMenuItem _diagnosticsItem = new();
    private readonly ToolStripMenuItem _dataItem = new();
    private readonly ToolStripMenuItem _exitItem = new();
    private Icon _currentIcon;

    /// <summary>
    /// 构造托盘交互组件并订阅协调器的展示快照更新事件。
    /// </summary>
    /// <param name="uiContext">WinForms 同步上下文。</param>
    public TrayApplicationContext(SynchronizationContext uiContext)
    {
        _coordinator = new MonitorCoordinator(uiContext);
        UiText.SetLanguage(_coordinator.Settings.Language);
        AppTheme.Set(_coordinator.Settings.Theme);
        _detailForm = new DetailForm();
        _chartForm = new ChartForm(_coordinator.Settings, ApplySettings);
        _menu = BuildMenu();
        _currentIcon = IconFactory.Create(null);
        _notifyIcon = new NotifyIcon
        {
            Icon = _currentIcon,
            Text = UiText.Get("TrayStarting"),
            Visible = true,
            ContextMenuStrip = _menu
        };

        _notifyIcon.MouseDoubleClick += NotifyIconMouseDoubleClick;
        _coordinator.ViewUpdated += UpdateView;
        ApplyLocalization();
        UpdateView(_coordinator.CurrentView);
    }

    /// <summary>
    /// 启动后台协调器并完成第一次额度查询。
    /// </summary>
    public Task StartAsync() => _coordinator.StartAsync();

    /// <summary>
    /// 判断托盘鼠标动作是否应打开完整图表窗；仅左键双击或更多连续点击满足条件。
    /// </summary>
    /// <param name="button">触发托盘动作的鼠标按键。</param>
    /// <param name="clickCount">Windows 报告的连续点击次数。</param>
    /// <returns>应打开完整图表窗时返回 true；单击和非左键动作返回 false。</returns>
    public static bool ShouldOpenChartFromTray(MouseButtons button, int clickCount) =>
        button == MouseButtons.Left && clickCount >= 2;

    /// <summary>
    /// 在 WinForms 消息循环首次空闲时启动异步协议连接，避免启动阶段阻塞托盘消息泵。
    /// </summary>
    public void BeginStart()
    {
        Application.Idle += StartOnFirstIdle;
    }

    /// <summary>
    /// 只执行一次后台启动并在开始后移除 Idle 订阅。
    /// </summary>
    private async void StartOnFirstIdle(object? sender, EventArgs e)
    {
        Application.Idle -= StartOnFirstIdle;
        await StartAsync();
    }

    /// <summary>
    /// 创建右键菜单中的立即查询、详情、合并工作台、设置、日志目录和退出动作。
    /// </summary>
    /// <returns>已配置的托盘右键菜单。</returns>
    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        _refreshItem.Click += async (_, _) => await _coordinator.RefreshNowAsync();
        _detailsItem.Click += (_, _) => ShowDetails();
        _dashboardItem.Click += (_, _) => ShowDashboard();
        _settingsItem.Click += (_, _) => ShowSettings();
        _diagnosticsItem.Click += (_, _) => ShowDiagnostics();
        _dataItem.Click += (_, _) => OpenDataDirectory();
        _exitItem.Click += async (_, _) => await ExitAsync();
        menu.Items.AddRange([
            _refreshItem,
            _detailsItem,
            new ToolStripSeparator(),
            _dashboardItem,
            _settingsItem,
            _diagnosticsItem,
            new ToolStripSeparator(),
            _dataItem,
            new ToolStripSeparator(),
            _exitItem]);
        return menu;
    }

    /// <summary>
    /// 刷新托盘菜单、悬停起始文本和窗口标题的当前语言与主题。
    /// </summary>
    private void ApplyLocalization()
    {
        _refreshItem.Text = UiText.Get("TrayRefresh");
        _detailsItem.Text = UiText.Get("TrayDetails");
        _dashboardItem.Text = UiText.Get("TrayDashboard");
        _settingsItem.Text = UiText.Get("TraySettings");
        _diagnosticsItem.Text = UiText.Get("TabDiagnostics");
        _dataItem.Text = UiText.Get("TrayData");
        _exitItem.Text = UiText.Get("TrayExit");
        _menu.BackColor = AppTheme.Current.Surface;
        _menu.ForeColor = AppTheme.Current.Text;
        foreach (ToolStripItem item in _menu.Items)
        {
            item.BackColor = AppTheme.Current.Surface;
            item.ForeColor = AppTheme.Current.Text;
        }

        _detailForm.ApplyAppearance();
    }

    /// <summary>
    /// 保存协调器设置，立即同步语言和主题，并主动查询一次额度以应用新价格和触发历史重算。
    /// </summary>
    /// <param name="settings">设置页提交的完整配置。</param>
    private async void ApplySettings(AppSettings settings)
    {
        UiText.SetLanguage(settings.Language);
        AppTheme.Set(settings.Theme);
        _coordinator.ApplySettings(settings);
        _chartForm.ApplyPreferences(settings);
        ApplyLocalization();
        UpdateView(_coordinator.CurrentView);
        await _coordinator.RefreshNowAsync();
    }

    /// <summary>
    /// 左键双击托盘图标时打开合并后的额度趋势工作台；单击不执行任何窗口动作。
    /// </summary>
    private void NotifyIconMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (ShouldOpenChartFromTray(e.Button, e.Clicks))
        {
            ShowDashboard();
        }
    }

    /// <summary>
    /// 用最新快照刷新悬停文字、图标进度环和所有已创建窗口。
    /// </summary>
    private void UpdateView(MonitorViewSnapshot view)
    {
        _detailForm.UpdateView(view, _coordinator.Settings.EnableOfficialLongContextEstimate);
        _chartForm.UpdateView(view, _coordinator.Settings.Regression);
        _notifyIcon.Text = BuildTooltip(view, _coordinator.Settings.EnableOfficialLongContextEstimate);

        var nextIcon = IconFactory.Create(view.RateLimit?.UsedPercent);
        _notifyIcon.Icon = nextIcon;
        _currentIcon.Dispose();
        _currentIcon = nextIcon;
    }

    /// <summary>
    /// 在鼠标附近显示单击详情窗。
    /// </summary>
    private void ShowDetails()
    {
        _detailForm.UpdateView(
            _coordinator.CurrentView,
            _coordinator.Settings.EnableOfficialLongContextEstimate);
        _detailForm.ShowNearCursor();
    }

    /// <summary>
    /// 在统一主窗口中直达总览标签页。
    /// </summary>
    private void ShowDashboard()
    {
        _chartForm.UpdateView(_coordinator.CurrentView, _coordinator.Settings.Regression);
        _chartForm.ShowDashboardTab();
    }

    /// <summary>
    /// 在统一主窗口中直达设置标签页，并重新载入协调器当前设置。
    /// </summary>
    private void ShowSettings()
    {
        _chartForm.ShowSettingsTab(_coordinator.Settings);
    }

    /// <summary>
    /// 在统一主窗口中直达诊断标签页。
    /// </summary>
    private void ShowDiagnostics()
    {
        _chartForm.UpdateView(_coordinator.CurrentView, _coordinator.Settings.Regression);
        _chartForm.ShowDiagnosticsTab();
    }

    /// <summary>
    /// 使用资源管理器打开本程序的数据目录，便于查看设置、状态和运行日志。
    /// </summary>
    private static void OpenDataDirectory()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            ArgumentList = { AppPaths.DataDirectory },
            UseShellExecute = true
        });
    }

    /// <summary>
    /// 构造不超过 Windows NotifyIcon 长度限制的悬停摘要。
    /// </summary>
    /// <returns>最多 63 个字符的 tooltip。</returns>
    /// <param name="view">协调器发布的最新展示快照。</param>
    /// <param name="showOfficialLongContext">是否显示可选的官方 >272K 对比口径。</param>
    private static string BuildTooltip(MonitorViewSnapshot view, bool showOfficialLongContext)
    {
        var baseEstimate = view.EstimatedWeeklyQuotaUsd is decimal baseValue
            ? $"${baseValue.ToString("N0", UiText.Culture)}"
            : "--";
        var officialLongEstimate = view.OfficialLongContextEstimatedWeeklyQuotaUsd is decimal officialLongValue
            ? $"${officialLongValue.ToString("N0", UiText.Culture)}"
            : "--";
        var used = view.RateLimit is null
            ? "--"
            : $"{view.RateLimit.UsedPercent.ToString("N1", UiText.Culture)}%";
        var text = UiText.Format(
            "TooltipFormat",
            showOfficialLongContext ? $"{baseEstimate}/{officialLongEstimate}" : baseEstimate,
            used,
            view.SampleCount);
        return text.Length <= 63 ? text : text[..63];
    }

    /// <summary>
    /// 隐藏托盘图标、停止后台进程、释放窗口资源并结束消息循环。
    /// </summary>
    private async Task ExitAsync()
    {
        _notifyIcon.Visible = false;
        await _coordinator.DisposeAsync();
        _detailForm.Dispose();
        _chartForm.Dispose();
        _menu.Dispose();
        _notifyIcon.Dispose();
        _currentIcon.Dispose();
        ExitThread();
    }
}
