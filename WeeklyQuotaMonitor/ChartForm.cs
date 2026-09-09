using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor;

/// <summary>
/// 标识主窗口当前选中的一级业务页。
/// </summary>
public enum DashboardSection
{
    Dashboard,
    Settings,
    Diagnostics,
    Speed
}

/// <summary>
/// 提供合并的额度趋势工作台、设置和诊断三个一级页面的常驻主窗口。
/// </summary>
public sealed class ChartForm : Form
{
    private const string LiveSampleSource = "live";
    private readonly ApplicationSidebar _sidebar = new();
    private readonly Panel _contentHost = new() { Dock = DockStyle.Fill };
    private readonly Panel _dashboardPage = new() { Dock = DockStyle.Fill };
    private readonly Panel _settingsPage = new() { Dock = DockStyle.Fill, Padding = new Padding(12) };
    private readonly Panel _diagnosticsPage = new() { Dock = DockStyle.Fill, Padding = new Padding(12) };
    private readonly Panel _speedPage = new() { Dock = DockStyle.Fill };
    private SpeedPanel? _speedPanel;
    private readonly MetricCard _baseEstimateCard = new(MetricCardTone.Primary, MetricCardStyle.Hero);
    private readonly MetricCard _usedCard = new(MetricCardTone.Warning);
    private readonly MetricCard _remainingCard = new(MetricCardTone.Positive);
    private readonly MetricCard _resetCard = new(MetricCardTone.Purple);
    private readonly MetricCard _sampleCard = new(MetricCardTone.Neutral);
    private readonly DashboardCardGrid _dashboardGrid = new();
    private readonly ConnectionBadge _connectionBadge = new();
    private readonly Label _dashboardEyebrow = new();
    private readonly Label _dashboardTitle = new();
    private readonly Label _dashboardSubtitle = new();
    private readonly Label _regressionEstimate = new() { Name = "RegressionEstimateLabel" };
    private readonly ToolStripStatusLabel _runtimeStatus = new()
    {
        Name = "RepricingStatusLabel", Spring = true, TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly StatusStrip _statusStrip = new() { SizingGrip = false, Dock = DockStyle.Bottom };
    private readonly ToolStripProgressBar _replayProgress = new()
    {
        Name = "RepricingProgress", Minimum = 0, Maximum = 100, Width = 180, Visible = false
    };
    private readonly Label _summary = new() { Name = "HistorySummaryLabel" };
    private readonly Label _timeRangeLabel = new();
    private readonly ComboBox _timeRange = new() { Name = "HistoryRangeComboBox" };
    private readonly CustomTimeWindowControl _customWindow = new() { Visible = false, Name = "QuotaCustomWindow" };
    public event Action<DateTimeOffset>? SpeedHistoryRequested;
    private readonly Panel _trendFrame = new() { Dock = DockStyle.Fill, Padding = new Padding(1) };
    private readonly TableLayoutPanel _trendContent = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
    private readonly QuotaChartControl _chart = new();
    private readonly DataGridView _grid = new();
    private readonly CheckBox _showBaseSamples = SeriesCheckBox(Color.FromArgb(14, 165, 233));
    private readonly CheckBox _showBaseRegression = SeriesCheckBox(Color.FromArgb(15, 135, 210));
    private readonly CheckBox _showOfficialSamples = SeriesCheckBox(Color.FromArgb(121, 67, 171));
    private readonly CheckBox _showOfficialRegression = SeriesCheckBox(Color.FromArgb(139, 92, 246));
    private readonly SettingsPanel _settingsPanel;
    private readonly DiagnosticsPanel _diagnosticsPanel = new();
    private readonly Dictionary<DataGridViewColumn, string> _gridColumnKeys = [];
    private MonitorViewSnapshot? _view;
    private AppSettings _settings;
    private DashboardSection _selectedSection = DashboardSection.Dashboard;

    /// <summary>
    /// 构造三页主窗口，载入设置面板并绑定保存回调。
    /// </summary>
    /// <param name="settings">当前已生效设置。</param>
    /// <param name="applySettings">保存设置后立即应用的回调。</param>
    public ChartForm(AppSettings settings, Action<AppSettings> applySettings)
    {
        _settings = settings;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(1040, 680);
        Size = new Size(1280, 820);
        Font = SystemFonts.MessageBoxFont!;

        _settingsPanel = new SettingsPanel(settings);
        _settingsPanel.SettingsSaved += applySettings;
        ConfigureGrid();
        ConfigureSeriesOptions();
        ConfigureTimeRange();

        _dashboardPage.Controls.Add(BuildDashboardPage());
        _settingsPage.Controls.Add(_settingsPanel);
        _diagnosticsPage.Controls.Add(_diagnosticsPanel);
        _contentHost.Controls.AddRange([_dashboardPage, _settingsPage, _diagnosticsPage, _speedPage]);
        _sidebar.SectionRequested += SelectSection;

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        shell.ColumnStyles.Add(new(SizeType.Absolute, 200));
        shell.ColumnStyles.Add(new(SizeType.Percent, 100));
        shell.RowStyles.Add(new(SizeType.Percent, 100));
        shell.Controls.Add(_sidebar, 0, 0);
        shell.Controls.Add(_contentHost, 1, 0);
        Controls.Add(shell);
        _statusStrip.Items.Add(_runtimeStatus);
        _statusStrip.Items.Add(_replayProgress);
        Controls.Add(_statusStrip);

        ApplyPreferences(settings);
        SelectSection(DashboardSection.Dashboard);
        FormClosing += HideOnUserClose;
    }

    public DashboardSection SelectedSection => _selectedSection;

    /// <summary>
    /// 构造标题、紧凑指标带、趋势图和逐点明细合并而成的额度工作台。
    /// </summary>
    /// <returns>可停靠到额度趋势入口的根控件。</returns>
    private Control BuildDashboardPage()
    {
        _dashboardEyebrow.AutoSize = true;
        _dashboardEyebrow.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 7.5F, FontStyle.Bold);
        _dashboardEyebrow.ForeColor = Color.FromArgb(8, 145, 178);
        _dashboardEyebrow.Margin = new Padding(0, 0, 0, 2);
        _dashboardTitle.AutoSize = true;
        _dashboardTitle.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 19F, FontStyle.Bold);
        _dashboardTitle.Margin = new Padding(0, 0, 0, 3);
        _dashboardSubtitle.AutoSize = true;
        _dashboardSubtitle.MaximumSize = new Size(820, 0);
        _dashboardSubtitle.ForeColor = AppTheme.Current.MutedText;

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 3,
            Margin = new Padding(4, 0, 4, 8)
        };
        header.ColumnStyles.Add(new(SizeType.Percent, 100));
        header.ColumnStyles.Add(new(SizeType.AutoSize));
        header.RowStyles.Add(new(SizeType.AutoSize));
        header.RowStyles.Add(new(SizeType.AutoSize));
        header.RowStyles.Add(new(SizeType.AutoSize));
        _connectionBadge.Margin = new Padding(20, 2, 0, 0);
        header.Controls.Add(_dashboardEyebrow, 0, 0);
        header.Controls.Add(_dashboardTitle, 0, 1);
        header.Controls.Add(_dashboardSubtitle, 0, 2);
        header.Controls.Add(_connectionBadge, 1, 1);
        header.SetRowSpan(_connectionBadge, 2);

        _dashboardGrid.Margin = new Padding(0, 0, 0, 8);
        _dashboardGrid.SetCards(
            _baseEstimateCard,
            _usedCard,
            _remainingCard,
            _resetCard,
            _sampleCard);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(20, 16, 20, 18)
        };
        content.ColumnStyles.Add(new(SizeType.Percent, 100));
        content.RowStyles.Add(new(SizeType.AutoSize));
        content.RowStyles.Add(new(SizeType.AutoSize));
        content.RowStyles.Add(new(SizeType.Percent, 100));
        content.Controls.Add(header, 0, 0);
        content.Controls.Add(_dashboardGrid, 0, 1);
        content.Controls.Add(BuildTrendWorkspace(), 0, 2);

        return content;
    }

    /// <summary>
    /// 构造额度横幅、时间范围、系列开关、绘图区和采样明细表组成的趋势工作区。
    /// </summary>
    /// <returns>带轻量边框和留白的趋势工作区。</returns>
    private Control BuildTrendWorkspace()
    {
        _regressionEstimate.Dock = DockStyle.Fill;
        _regressionEstimate.AutoSize = true;
        _regressionEstimate.Padding = new Padding(12, 9, 8, 3);
        _regressionEstimate.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11F, FontStyle.Bold);

        _summary.Dock = DockStyle.Fill;
        _summary.AutoSize = true;
        _summary.Padding = new Padding(12, 0, 8, 4);
        _summary.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8.5F);

        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(8, 0, 8, 5),
            WrapContents = true
        };
        _timeRangeLabel.AutoSize = true;
        _timeRangeLabel.Margin = new Padding(4, 8, 4, 3);
        _timeRange.Width = 112;
        _timeRange.DropDownStyle = ComboBoxStyle.DropDownList;
        _timeRange.FlatStyle = FlatStyle.Flat;
        options.Controls.AddRange([
            _timeRangeLabel,
            _timeRange,
            _showBaseSamples,
            _showBaseRegression,
            _showOfficialSamples,
            _showOfficialRegression]);

        _chart.Dock = DockStyle.Fill;
        var dataStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(8, 0, 8, 8)
        };
        dataStack.ColumnStyles.Add(new(SizeType.Percent, 100));
        dataStack.RowStyles.Add(new(SizeType.Percent, 68));
        dataStack.RowStyles.Add(new(SizeType.Percent, 32));
        dataStack.Controls.Add(_chart, 0, 0);
        dataStack.Controls.Add(_grid, 0, 1);

        _trendContent.ColumnStyles.Add(new(SizeType.Percent, 100));
        _trendContent.RowStyles.Add(new(SizeType.AutoSize));
        _trendContent.RowStyles.Add(new(SizeType.AutoSize));
        _trendContent.RowStyles.Add(new(SizeType.AutoSize));
        _trendContent.RowStyles.Add(new(SizeType.AutoSize));
        _trendContent.RowStyles.Add(new(SizeType.Percent, 100));
        _trendContent.Controls.Add(_regressionEstimate, 0, 0);
        _trendContent.Controls.Add(_summary, 0, 1);
        _trendContent.Controls.Add(options, 0, 2);
        _trendContent.Controls.Add(_customWindow, 0, 3);
        _trendContent.Controls.Add(dataStack, 0, 4);
        _trendFrame.Margin = new Padding(4, 0, 4, 0);
        _trendFrame.Controls.Add(_trendContent);
        return _trendFrame;
    }

    /// <summary>
    /// 绑定基础与可选官方口径的四个系列开关，并按设置同步实际可见状态。
    /// </summary>
    private void ConfigureSeriesOptions()
    {
        foreach (var option in new[]
                 {
                     _showBaseSamples,
                     _showBaseRegression,
                     _showOfficialSamples,
                     _showOfficialRegression
                 })
        {
            option.CheckedChanged += (_, _) => ApplySeriesVisibility();
        }

        ApplySeriesVisibility();
    }

    /// <summary>
    /// 绑定历史时间范围选择事件，首次打开时默认显示最近 30 天。
    /// </summary>
    private void ConfigureTimeRange()
    {
        _timeRange.SelectedIndexChanged += (_, _) =>
        {
            _customWindow.Visible = SelectedHistoryRange() == HistoryRange.Custom;
            RefreshHistoryPage();
        };
        _customWindow.WindowApplied += RefreshHistoryPage;
        BindTimeRanges(HistoryRange.Days30);
    }

    /// <summary>
    /// 将图表选择框转换为绘图控件的系列显隐配置并立即重绘。
    /// </summary>
    private void ApplySeriesVisibility()
    {
        var showOfficial = _settings.EnableOfficialLongContextEstimate;
        _chart.SetSeriesVisibility(new(
            _showBaseSamples.Checked,
            _showBaseRegression.Checked,
            showOfficial && _showOfficialSamples.Checked,
            showOfficial && _showOfficialRegression.Checked));
    }

    /// <summary>
    /// 根据显式设置同步官方 >272K 系列开关和表格列，默认不让实验口径占用主界面空间。
    /// </summary>
    private void ApplyOptionalOfficialContextVisibility()
    {
        var enabled = _settings.EnableOfficialLongContextEstimate;
        _showOfficialSamples.Visible = enabled;
        _showOfficialRegression.Visible = enabled;
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            if (column.DataPropertyName is nameof(SampleGridRow.OfficialLongContextIntervalApiEquivalentUsd) or
                nameof(SampleGridRow.OfficialLongContextEstimatedWeeklyQuotaUsd))
            {
                column.Visible = enabled;
            }
        }

        ApplySeriesVisibility();
    }

    /// <summary>
    /// 应用新的语言与主题，刷新所有已创建页面并保留当前业务数据和选择。
    /// </summary>
    /// <param name="settings">已保存并生效的完整设置。</param>
    public void ApplyPreferences(AppSettings settings)
    {
        _settings = settings;
        _settingsPanel.LoadSettings(settings);
        ApplyLocalization();
        AppTheme.Apply(this);
        _statusStrip.BackColor = Color.FromArgb(8, 25, 52);
        _runtimeStatus.ForeColor = Color.FromArgb(220, 234, 252);
        foreach (var option in new[]
                 {
                     _showBaseSamples,
                     _showBaseRegression,
                     _showOfficialSamples,
                     _showOfficialRegression
                 })
        {
            SizeSeriesOptionToText(option);
            ApplySeriesOptionAppearance(option);
        }
        _sidebar.ApplyAppearance();
        _dashboardEyebrow.ForeColor = Color.FromArgb(8, 145, 178);
        _dashboardSubtitle.ForeColor = AppTheme.Current.MutedText;
        _connectionBadge.ApplyAppearance();
        _trendFrame.BackColor = AppTheme.Current.Border;
        _trendContent.BackColor = AppTheme.Current.Surface;
        _chart.ApplyAppearance();
        ApplyOptionalOfficialContextVisibility();
        RefreshAllPages();
    }

    /// <summary>
    /// 刷新主窗口、三个一级页面、系列、时间范围和表格标题的中英文文本。
    /// </summary>
    private void ApplyLocalization()
    {
        Text = UiText.Get("ProductName");
        _sidebar.ApplyLocalization();
        _dashboardEyebrow.Text = UiText.Get("DashboardEyebrow");
        _dashboardTitle.Text = UiText.Get("DashboardTitle");
        _dashboardSubtitle.Text = UiText.Get("DashboardSubtitle");
        _timeRangeLabel.Text = UiText.Get("TimeRangeLabel");
        _showBaseSamples.Text = UiText.Get("SeriesBaseSamples");
        _showBaseRegression.Text = UiText.Get("SeriesBaseRegression");
        _showOfficialSamples.Text = UiText.Get("SeriesOfficialSamples");
        _showOfficialRegression.Text = UiText.Get("SeriesOfficialRegression");
        foreach (var pair in _gridColumnKeys)
        {
            pair.Key.HeaderText = UiText.Get(pair.Value);
        }

        var selectedRange = SelectedHistoryRange();
        BindTimeRanges(selectedRange);
        _settingsPanel.ApplyLocalization();
        _diagnosticsPanel.ApplyLocalization();
    }

    /// <summary>
    /// 选择总览页并显示或激活主窗口。
    /// </summary>
    public void ShowDashboardTab()
    {
        ShowWindow();
        SelectSection(DashboardSection.Dashboard);
    }

    /// <summary>
    /// 兼容原图表入口并打开已经合并趋势图的总览工作台。
    /// </summary>
    public void ShowChartTab()
    {
        ShowDashboardTab();
    }

    /// <summary>
    /// 重新载入当前设置，选择设置页并显示或激活主窗口。
    /// </summary>
    /// <param name="settings">协调器当前已生效设置。</param>
    public void ShowSettingsTab(AppSettings settings)
    {
        _settingsPanel.LoadSettings(settings);
        ShowWindow();
        SelectSection(DashboardSection.Settings);
    }

    /// <summary>
    /// 选择诊断页并显示或激活主窗口。
    /// </summary>
    public void ShowDiagnosticsTab()
    {
        ShowWindow();
        SelectSection(DashboardSection.Diagnostics);
    }

    /// <summary>
    /// 切换无页签内容工作区中唯一可见的业务页，并同步侧栏选中状态。
    /// </summary>
    /// <param name="section">需要显示的总览、历史、设置或诊断页面。</param>
    private void SelectSection(DashboardSection section)
    {
        if (section == DashboardSection.Speed && _speedPanel is null)
        {
            _speedPanel = new SpeedPanel();
            _speedPanel.HistoryRequested += start => SpeedHistoryRequested?.Invoke(start);
            _speedPage.Controls.Add(_speedPanel);
            if (_view is not null) _speedPanel.UpdateView(_view, _settings.Speed);
        }
        var target = section switch
        {
            DashboardSection.Dashboard => _dashboardPage,
            DashboardSection.Settings => _settingsPage,
            DashboardSection.Diagnostics => _diagnosticsPage,
            DashboardSection.Speed => _speedPage,
            _ => throw new InvalidOperationException($"不支持的主窗口页面：{section}。")
        };
        foreach (var page in new[] { _dashboardPage, _settingsPage, _diagnosticsPage, _speedPage })
        {
            page.Visible = ReferenceEquals(page, target);
        }

        target.BringToFront();
        _selectedSection = section;
        _sidebar.SetSelected(section);
    }

    /// <summary>
    /// 显示已隐藏窗口、恢复普通状态并置于当前工作区前方。
    /// </summary>
    private void ShowWindow()
    {
        if (!Visible)
        {
            Show();
        }

        WindowState = FormWindowState.Normal;
        Activate();
    }

    /// <summary>
    /// 保存最新展示快照并同步刷新总览、历史图表、表格和诊断页。
    /// </summary>
    /// <param name="view">协调器发布的展示快照。</param>
    /// <param name="options">当前回归模式、窗口和样本过滤参数。</param>
    public void UpdateView(MonitorViewSnapshot view, RegressionOptions options)
    {
        _runtimeStatus.Text = view.Status;
        _runtimeStatus.ToolTipText = view.Status;
        _replayProgress.Visible = view.RepricingProgressPercent.HasValue;
        _replayProgress.Value = view.RepricingProgressPercent ?? 0;
        _view = view;
        _settings.Regression = options;
        RefreshAllPages();
    }

    /// <summary>
    /// 在存在快照时刷新全部数据页；设置页的未保存输入不受影响。
    /// </summary>
    private void RefreshAllPages()
    {
        if (_view is null)
        {
            return;
        }

        RefreshDashboardPage();
        RefreshHistoryPage();
        _diagnosticsPanel.UpdateView(_view, _settings);
        _speedPanel?.UpdateView(_view, _settings.Speed);
    }

    /// <summary>打开速度页，供托盘和独立窗口调用；数据读取由协调器负责。</summary>
    public void ShowSpeedTab()
    {
        SelectSection(DashboardSection.Speed);
        ShowWindow();
    }

    /// <summary>
    /// 根据当前快照填充总览指标卡，不触发额度查询或日志扫描。
    /// </summary>
    private void RefreshDashboardPage()
    {
        var view = _view ?? throw new InvalidOperationException("刷新总览前必须提供展示快照。");
        var used = view.RateLimit?.UsedPercent;
        decimal? remaining = used is decimal usedValue ? 100m - usedValue : null;
        var pendingCount = view.HistoricalReplayUnattributedUsedPercents.Count;
        _usedCard.SetContent(
            UiText.Get("CardUsed"),
            PercentValue(used),
            UiText.Get("DashboardCurrentWindow"));
        _usedCard.SetProgress(used);
        _remainingCard.SetContent(
            UiText.Get("CardRemaining"),
            PercentValue(remaining),
            UiText.Get("DashboardCurrentWindow"));
        _remainingCard.SetProgress(remaining);
        _resetCard.SetContent(
            UiText.Get("CardReset"),
            view.RateLimit?.ResetsAt.LocalDateTime.ToString("M/d HH:mm", UiText.Culture) ?? UiText.Get("Unknown"),
            UiText.Get("DashboardResetCaption"));
        _sampleCard.SetContent(
            UiText.Get("CardSamples"),
            view.SampleCount.ToString("N0", UiText.Culture),
            UiText.Format("DashboardSamplesCaption", view.ArchivedSampleCount, pendingCount));
        _connectionBadge.SetState(view.AppServerConnected, view.UpdatedAt);
    }

    /// <summary>
    /// 按所选时间范围过滤快照，并刷新额度横幅、绘图区和逐点表格。
    /// </summary>
    private void RefreshHistoryPage()
    {
        if (_view is null || _timeRange.SelectedItem is not RangeChoice selected)
        {
            return;
        }

        var cutoff = selected.Range switch
        {
            HistoryRange.All => DateTimeOffset.MinValue,
            HistoryRange.Hours24 => DateTimeOffset.Now.AddHours(-24),
            HistoryRange.Days7 => DateTimeOffset.Now.AddDays(-7),
            HistoryRange.Days30 => DateTimeOffset.Now.AddDays(-30),
            HistoryRange.Custom => _customWindow.Start,
            _ => throw new InvalidOperationException($"不支持的历史时间范围：{selected.Range}。")
        };
        var end = selected.Range == HistoryRange.Custom ? _customWindow.End : DateTimeOffset.Now;
        var samples = _view.Samples.Where(sample => sample.Timestamp >= cutoff && sample.Timestamp <= end).ToArray();
        var baseAnalysis = RegressionCalculator.Analyze(
            samples,
            _settings.Regression,
            _view.PricingVersion);
        var officialAnalysis = _settings.EnableOfficialLongContextEstimate
            ? RegressionCalculator.AnalyzeOfficialLongContext(
                samples,
                _settings.Regression,
                _view.PricingVersion)
            : new RegressionAnalysis([], []);
        _baseEstimateCard.SetContent(
            UiText.Get(_view.ShowingPreviousPrices ? "RepricingPreviousLabel" : "EstimateBase"),
            EstimateValue(baseAnalysis.CurrentEstimate),
            UiText.Format("DashboardEstimateCaption", baseAnalysis.CurrentContributions.Count));

        var baseEstimate = RegressionEstimateText(
            baseAnalysis.CurrentEstimate,
            baseAnalysis.CurrentContributions.Count,
            samples.Length);
        _regressionEstimate.Text = _settings.EnableOfficialLongContextEstimate
            ? UiText.Format(
                "CurrentEstimateDualFormat",
                ModeName(_settings.Regression.Mode),
                baseEstimate,
                RegressionEstimateText(
                    officialAnalysis.CurrentEstimate,
                    officialAnalysis.CurrentContributions.Count,
                    samples.Length))
            : UiText.Format(
                "CurrentEstimateBaseFormat",
                ModeName(_settings.Regression.Mode),
                baseEstimate);
        _regressionEstimate.ForeColor = AppTheme.Current.Accent;

        var filterHint = samples.Length > 0 &&
                          (baseAnalysis.Curve.Count == 0 ||
                           (_settings.EnableOfficialLongContextEstimate && officialAnalysis.Curve.Count == 0))
            ? UiText.Get("FilterHint")
            : string.Empty;
        var authoritative = _view.RateLimit is null
            ? "--"
            : PercentValue(_view.RateLimit.UsedPercent);
        var latestValid = samples.OrderBy(sample => sample.Timestamp).LastOrDefault()?.UsedPercent;
        _summary.Text = UiText.Format(
            "TrendSummaryFormat",
            authoritative,
            latestValid is decimal value ? PercentValue(value) : UiText.Get("None"),
            samples.Length,
            _view.ArchivedSampleCount,
            _view.HistoricalReplayUnattributedUsedPercents.Count,
            filterHint);
        _summary.ForeColor = AppTheme.Current.MutedText;

        _chart.SetData(
            samples,
            baseAnalysis.Curve,
            officialAnalysis.Curve,
            baseAnalysis.CurrentContributions);
        _grid.DataSource = samples
            .OrderByDescending(sample => sample.Timestamp)
            .Select(sample => new SampleGridRow(
                sample.Timestamp.LocalDateTime,
                sample.UsedPercent,
                sample.DeltaPercent,
                sample.IntervalApiEquivalentUsd,
                sample.EstimatedWeeklyQuotaUsd,
                sample.OfficialLongContextIntervalApiEquivalentUsd,
                sample.OfficialLongContextEstimatedWeeklyQuotaUsd,
                sample.Usage.InputTokens,
                sample.Usage.CachedInputTokens,
                sample.Usage.OutputTokens,
                sample.Models,
                sample.ServiceTiers,
                sample.CreditMultipliers,
                SampleSourceName(sample.SampleSource),
                sample.PricingVersion))
            .ToArray();
    }

    /// <summary>
    /// 创建只读采样点表格及其金额、百分比和 token 列。
    /// </summary>
    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AutoGenerateColumns = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.BorderStyle = BorderStyle.None;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
        _grid.MultiSelect = false;
        _grid.DefaultCellStyle.Padding = new Padding(4, 4, 4, 4);
        _grid.TabStop = false;
        _grid.DataBindingComplete += (_, _) =>
        {
            _grid.ClearSelection();
            _grid.CurrentCell = null;
        };
        AddGridColumn("GridTime", nameof(SampleGridRow.Timestamp), 145, "yyyy-MM-dd HH:mm:ss");
        AddGridColumn("GridUsed", nameof(SampleGridRow.UsedPercent), 70, "F3");
        AddGridColumn("GridDelta", nameof(SampleGridRow.DeltaPercent), 65, "F3");
        AddGridColumn("GridBaseInterval", nameof(SampleGridRow.IntervalApiEquivalentUsd), 90, "F5");
        AddGridColumn("GridBaseWeekly", nameof(SampleGridRow.EstimatedWeeklyQuotaUsd), 90, "F2");
        AddGridColumn("GridOfficialInterval", nameof(SampleGridRow.OfficialLongContextIntervalApiEquivalentUsd), 100, "F5");
        AddGridColumn("GridOfficialWeekly", nameof(SampleGridRow.OfficialLongContextEstimatedWeeklyQuotaUsd), 100, "F2");
        AddGridColumn("GridInput", nameof(SampleGridRow.InputTokens), 90, "N0");
        AddGridColumn("GridCached", nameof(SampleGridRow.CachedInputTokens), 90, "N0");
        AddGridColumn("GridOutput", nameof(SampleGridRow.OutputTokens), 80, "N0");
        AddGridColumn("GridModels", nameof(SampleGridRow.Models), 210, null);
        AddGridColumn("GridTier", nameof(SampleGridRow.ServiceTiers), 90, null);
        AddGridColumn("GridMultiplier", nameof(SampleGridRow.CreditMultipliers), 90, null);
        AddGridColumn("GridSource", nameof(SampleGridRow.SampleSource), 105, null);
        AddGridColumn("GridPricing", nameof(SampleGridRow.PricingVersion), 180, null);
    }

    /// <summary>
    /// 创建并登记一个可区域化的数据表文本列。
    /// </summary>
    /// <param name="titleKey">表头资源键。</param>
    /// <param name="property">数据绑定属性。</param>
    /// <param name="width">默认列宽。</param>
    /// <param name="format">可选显示格式。</param>
    private void AddGridColumn(string titleKey, string property, int width, string? format)
    {
        var column = new DataGridViewTextBoxColumn
        {
            HeaderText = UiText.Get(titleKey),
            DataPropertyName = property,
            Width = width,
            MinimumWidth = Math.Max(48, width),
            DefaultCellStyle = new DataGridViewCellStyle { Format = format }
        };
        _grid.Columns.Add(column);
        _gridColumnKeys[column] = titleKey;
    }

    /// <summary>
    /// 重新绑定区域化的时间范围选项并保留当前枚举选择。
    /// </summary>
    /// <param name="selected">需要继续选中的时间范围。</param>
    private void BindTimeRanges(HistoryRange selected)
    {
        _customWindow.ApplyLanguage();
        _timeRange.DataSource = new[]
        {
            new RangeChoice(HistoryRange.All, UiText.Get("TimeRangeAll")),
            new RangeChoice(HistoryRange.Hours24, UiText.Get("TimeRange24Hours")),
            new RangeChoice(HistoryRange.Days7, UiText.Get("TimeRange7Days")),
            new RangeChoice(HistoryRange.Days30, UiText.Get("TimeRange30Days")),
            new RangeChoice(HistoryRange.Custom, UiText.Get("TimeRangeCustom"))
        };
        _timeRange.SelectedItem = ((RangeChoice[])_timeRange.DataSource)
            .Single(choice => choice.Range == selected);
    }

    /// <summary>
    /// 返回当前时间范围；控件尚未绑定时按默认的最近 30 天处理。
    /// </summary>
    private HistoryRange SelectedHistoryRange() =>
        _timeRange.SelectedItem is RangeChoice selected ? selected.Range : HistoryRange.Days30;

    /// <summary>
    /// 创建默认选中的曲线显隐选项，并使用对应系列颜色强化辨识。
    /// </summary>
    /// <param name="color">曲线绘制颜色。</param>
    /// <returns>可加入横向选项区的复选框。</returns>
    private static CheckBox SeriesCheckBox(Color color)
    {
        var option = new CheckBox
        {
            Checked = true,
            AutoSize = false,
            AutoEllipsis = false,
            Appearance = Appearance.Button,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(7, 2, 7, 2),
            MinimumSize = new Size(112, 28),
            Margin = new Padding(8, 3, 4, 3),
            Tag = color,
            UseVisualStyleBackColor = false
        };
        option.TextChanged += (_, _) => SizeSeriesOptionToText(option);
        option.FontChanged += (_, _) => SizeSeriesOptionToText(option);
        option.CheckedChanged += (_, _) => ApplySeriesOptionAppearance(option);
        SizeSeriesOptionToText(option);
        ApplySeriesOptionAppearance(option);
        return option;
    }

    /// <summary>
    /// 使用当前字体和 DPI 测量系列按钮文字，并为扁平按钮边框预留额外空间，防止末字裁切。
    /// </summary>
    /// <param name="option">需要按可见文字调整尺寸的系列按钮。</param>
    private static void SizeSeriesOptionToText(CheckBox option)
    {
        var textSize = TextRenderer.MeasureText(
            option.Text,
            option.Font,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        var horizontalChrome = Math.Max(20, (int)Math.Ceiling(24 * option.DeviceDpi / 96F));
        var verticalChrome = Math.Max(6, (int)Math.Ceiling(8 * option.DeviceDpi / 96F));
        option.Size = new Size(
            Math.Max(option.MinimumSize.Width, textSize.Width + option.Padding.Horizontal + horizontalChrome),
            Math.Max(option.MinimumSize.Height, textSize.Height + option.Padding.Vertical + verticalChrome));
    }

    /// <summary>
    /// 以实色系列色和白字突出选中项，以中性底色和弱化文字表示未选中项。
    /// </summary>
    /// <param name="option">需要同步勾选视觉状态的系列按钮。</param>
    private static void ApplySeriesOptionAppearance(CheckBox option)
    {
        var accent = option.Tag is Color color
            ? color
            : throw new InvalidOperationException("系列按钮缺少对应的曲线颜色。");
        var selectedBackground = ControlPaint.Dark(accent, 0.24F);
        if (option.Checked)
        {
            option.BackColor = selectedBackground;
            option.ForeColor = Color.White;
            option.FlatAppearance.BorderColor = selectedBackground;
            option.FlatAppearance.BorderSize = 2;
            option.FlatAppearance.CheckedBackColor = selectedBackground;
            option.FlatAppearance.MouseOverBackColor = ControlPaint.Light(selectedBackground, 0.08F);
            option.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(selectedBackground, 0.08F);
        }
        else
        {
            option.BackColor = AppTheme.Current.SurfaceAlternate;
            option.ForeColor = AppTheme.Current.MutedText;
            option.FlatAppearance.BorderColor = AppTheme.Current.Border;
            option.FlatAppearance.BorderSize = 1;
            option.FlatAppearance.CheckedBackColor = selectedBackground;
            option.FlatAppearance.MouseOverBackColor = ControlPaint.Light(AppTheme.Current.SurfaceAlternate, 0.04F);
            option.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(AppTheme.Current.SurfaceAlternate, 0.04F);
        }

        option.Invalidate();
    }

    /// <summary>
    /// 把可空额度转换为固定 USD 符号的区域化文本。
    /// </summary>
    private static string EstimateValue(decimal? estimate) =>
        estimate is decimal value ? UiText.Format("EstimateValue", value) : UiText.Get("WaitingSamples");

    /// <summary>
    /// 把可空百分比转换为区域化数值和百分号。
    /// </summary>
    private static string PercentValue(decimal? percent) =>
        percent is decimal value ? $"{value.ToString("N2", UiText.Culture)}%" : "--";

    /// <summary>
    /// 将回归值和当前估值贡献样本数转换为窗口顶部的明确额度文本。
    /// </summary>
    /// <param name="estimate">所选时间范围重新计算后的当前估值。</param>
    /// <param name="contributionCount">当前估值实际使用的有效样本数。</param>
    /// <param name="totalPoints">所选时间范围内尚未经过金额上限过滤的样本总数。</param>
    /// <returns>包含金额和贡献样本数，或等待原因计数的区域化文本。</returns>
    private static string RegressionEstimateText(decimal? estimate, int contributionCount, int totalPoints) =>
        estimate is decimal value
            ? UiText.Format("EstimateWithPoints", value, contributionCount)
            : $"{UiText.Get("WaitingSamples")} ({contributionCount}/{totalPoints})";

    /// <summary>
    /// 将回归枚举转换为当前界面语言的名称。
    /// </summary>
    private static string ModeName(RegressionMode mode) => mode switch
    {
        RegressionMode.Linear => UiText.Get("RegressionLinear"),
        RegressionMode.TimeWindowSegmented => UiText.Get("RegressionSegmented"),
        RegressionMode.GaussianAggregation => UiText.Get("RegressionGaussian"),
        _ => throw new InvalidOperationException($"不支持的回归模式：{mode}。")
    };

    /// <summary>
    /// 将持久化样本来源转换为当前界面语言的标签。
    /// </summary>
    private static string SampleSourceName(string source)
    {
        if (string.Equals(source, HistoricalReplayCalculator.HistoricalSampleSource, StringComparison.Ordinal))
        {
            return UiText.Get("SourceReplay");
        }

        if (string.Equals(source, LiveSampleSource, StringComparison.Ordinal))
        {
            return UiText.Get("SourceLive");
        }

        throw new InvalidDataException($"不支持的样本来源：{source}。");
    }

    /// <summary>
    /// 用户关闭主窗口时仅隐藏，保留托盘上下文持有的实例。
    /// </summary>
    private void HideOnUserClose(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>
    /// 定义合并趋势工作台支持的四种时间范围。
    /// </summary>
    private enum HistoryRange
    {
        All,
        Hours24,
        Days7,
        Days30,
        Custom
    }

    /// <summary>
    /// 将时间范围枚举与当前语言的下拉显示文本绑定。
    /// </summary>
    private sealed record RangeChoice(HistoryRange Range, string Text)
    {
        /// <summary>
        /// 返回下拉框应显示的当前语言文本。
        /// </summary>
        public override string ToString() => Text;
    }

    /// <summary>
    /// 将嵌套 TokenUsage 展平为 DataGridView 可直接绑定的本地时间只读行。
    /// </summary>
    private sealed record SampleGridRow(
        DateTime Timestamp,
        decimal UsedPercent,
        decimal DeltaPercent,
        decimal IntervalApiEquivalentUsd,
        decimal EstimatedWeeklyQuotaUsd,
        decimal OfficialLongContextIntervalApiEquivalentUsd,
        decimal OfficialLongContextEstimatedWeeklyQuotaUsd,
        long InputTokens,
        long CachedInputTokens,
        long OutputTokens,
        string Models,
        string ServiceTiers,
        string CreditMultipliers,
        string SampleSource,
        string PricingVersion);
}
