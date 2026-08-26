using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor;

/// <summary>
/// 标识主窗口当前选中的一级业务页。
/// </summary>
public enum DashboardSection
{
    Dashboard,
    History,
    Settings,
    Diagnostics
}

/// <summary>
/// 提供总览、历史与图表、设置、诊断四个一级页面的常驻主窗口。
/// </summary>
public sealed class ChartForm : Form
{
    private const string LiveSampleSource = "live";
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _dashboardTab = new();
    private readonly TabPage _historyTab = new();
    private readonly TabPage _settingsTab = new();
    private readonly TabPage _diagnosticsTab = new();
    private readonly MetricCard _baseEstimateCard = new();
    private readonly MetricCard _officialEstimateCard = new();
    private readonly MetricCard _usedCard = new();
    private readonly MetricCard _remainingCard = new();
    private readonly MetricCard _resetCard = new();
    private readonly MetricCard _sampleCard = new();
    private readonly MetricCard _connectionCard = new();
    private readonly Label _dashboardTitle = new();
    private readonly Label _dashboardSubtitle = new();
    private readonly Label _dashboardStatus = new();
    private readonly Label _regressionEstimate = new() { Name = "RegressionEstimateLabel" };
    private readonly Label _summary = new() { Name = "HistorySummaryLabel" };
    private readonly Label _timeRangeLabel = new();
    private readonly ComboBox _timeRange = new();
    private readonly QuotaChartControl _chart = new();
    private readonly DataGridView _grid = new();
    private readonly CheckBox _showBaseSamples = SeriesCheckBox(Color.FromArgb(210, 105, 28));
    private readonly CheckBox _showBaseRegression = SeriesCheckBox(Color.FromArgb(24, 119, 196));
    private readonly CheckBox _showOfficialSamples = SeriesCheckBox(Color.FromArgb(121, 67, 171));
    private readonly CheckBox _showOfficialRegression = SeriesCheckBox(Color.FromArgb(26, 145, 91));
    private readonly SettingsPanel _settingsPanel;
    private readonly DiagnosticsPanel _diagnosticsPanel = new();
    private readonly Dictionary<DataGridViewColumn, string> _gridColumnKeys = [];
    private MonitorViewSnapshot? _view;
    private AppSettings _settings;

    /// <summary>
    /// 构造四页主窗口，载入设置面板并绑定保存回调。
    /// </summary>
    /// <param name="settings">当前已生效设置。</param>
    /// <param name="applySettings">保存设置后立即应用的回调。</param>
    public ChartForm(AppSettings settings, Action<AppSettings> applySettings)
    {
        _settings = settings;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(860, 600);
        Size = new Size(1180, 780);
        Font = SystemFonts.MessageBoxFont!;

        _settingsPanel = new SettingsPanel(settings);
        _settingsPanel.SettingsSaved += applySettings;
        ConfigureGrid();
        ConfigureSeriesOptions();
        ConfigureTimeRange();

        _dashboardTab.Padding = new Padding(4);
        _historyTab.Padding = new Padding(4);
        _settingsTab.Padding = new Padding(4);
        _diagnosticsTab.Padding = new Padding(4);
        _dashboardTab.Controls.Add(BuildDashboardPage());
        _historyTab.Controls.Add(BuildHistoryPage());
        _settingsTab.Controls.Add(_settingsPanel);
        _diagnosticsTab.Controls.Add(_diagnosticsPanel);
        _tabs.TabPages.AddRange([_dashboardTab, _historyTab, _settingsTab, _diagnosticsTab]);
        Controls.Add(_tabs);

        ApplyPreferences(settings);
        FormClosing += HideOnUserClose;
    }

    public DashboardSection SelectedSection => _tabs.SelectedTab switch
    {
        var selected when selected == _dashboardTab => DashboardSection.Dashboard,
        var selected when selected == _historyTab => DashboardSection.History,
        var selected when selected == _settingsTab => DashboardSection.Settings,
        var selected when selected == _diagnosticsTab => DashboardSection.Diagnostics,
        _ => throw new InvalidOperationException("主窗口存在未注册的一级标签页。")
    };

    /// <summary>
    /// 构造标题、说明、响应式指标卡和运行状态组成的总览页。
    /// </summary>
    /// <returns>可停靠到总览标签页的根控件。</returns>
    private Control BuildDashboardPage()
    {
        _dashboardTitle.AutoSize = true;
        _dashboardTitle.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 20F, FontStyle.Bold);
        _dashboardSubtitle.AutoSize = true;
        _dashboardSubtitle.MaximumSize = new Size(940, 0);
        _dashboardStatus.AutoSize = true;
        _dashboardStatus.Padding = new Padding(8);

        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            Margin = new Padding(0, 16, 0, 16)
        };
        for (var column = 0; column < 3; column++)
        {
            cards.ColumnStyles.Add(new(SizeType.Percent, 33.333F));
        }

        cards.RowStyles.Add(new(SizeType.Absolute, 142));
        cards.RowStyles.Add(new(SizeType.Absolute, 142));
        cards.RowStyles.Add(new(SizeType.Absolute, 142));
        cards.Controls.Add(_baseEstimateCard, 0, 0);
        cards.Controls.Add(_officialEstimateCard, 1, 0);
        cards.Controls.Add(_usedCard, 2, 0);
        cards.Controls.Add(_remainingCard, 0, 1);
        cards.Controls.Add(_resetCard, 1, 1);
        cards.Controls.Add(_sampleCard, 2, 1);
        cards.Controls.Add(_connectionCard, 0, 2);
        cards.SetColumnSpan(_connectionCard, 3);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(24)
        };
        content.ColumnStyles.Add(new(SizeType.Percent, 100));
        content.RowStyles.Add(new(SizeType.AutoSize));
        content.RowStyles.Add(new(SizeType.AutoSize));
        content.RowStyles.Add(new(SizeType.Absolute, 426));
        content.RowStyles.Add(new(SizeType.AutoSize));
        content.Controls.Add(_dashboardTitle);
        content.Controls.Add(_dashboardSubtitle);
        content.Controls.Add(cards);
        content.Controls.Add(_dashboardStatus);

        return content;
    }

    /// <summary>
    /// 构造额度横幅、时间范围、系列开关、绘图区和采样明细表组成的历史页。
    /// </summary>
    /// <returns>可停靠到历史标签页的根控件。</returns>
    private Control BuildHistoryPage()
    {
        _regressionEstimate.Dock = DockStyle.Fill;
        _regressionEstimate.AutoSize = true;
        _regressionEstimate.Padding = new Padding(12, 10, 8, 6);
        _regressionEstimate.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12F, FontStyle.Bold);

        _summary.Dock = DockStyle.Fill;
        _summary.AutoSize = true;
        _summary.Padding = new Padding(12, 2, 8, 6);

        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(10, 2, 8, 5),
            WrapContents = true
        };
        _timeRangeLabel.AutoSize = true;
        _timeRangeLabel.Margin = new Padding(4, 7, 4, 3);
        _timeRange.Width = 125;
        _timeRange.DropDownStyle = ComboBoxStyle.DropDownList;
        options.Controls.AddRange([
            _timeRangeLabel,
            _timeRange,
            _showBaseSamples,
            _showBaseRegression,
            _showOfficialSamples,
            _showOfficialRegression]);

        _chart.Dock = DockStyle.Fill;
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 395,
            Panel1MinSize = 220,
            Panel2MinSize = 150
        };
        split.Panel1.Controls.Add(_chart);
        split.Panel2.Controls.Add(_grid);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.Percent, 100));
        root.Controls.Add(_regressionEstimate, 0, 0);
        root.Controls.Add(_summary, 0, 1);
        root.Controls.Add(options, 0, 2);
        root.Controls.Add(split, 0, 3);
        return root;
    }

    /// <summary>
    /// 绑定四个系列开关，并把默认全选状态同步到绘图控件。
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
    /// 绑定历史时间范围选择事件，默认显示全部保留数据。
    /// </summary>
    private void ConfigureTimeRange()
    {
        _timeRange.SelectedIndexChanged += (_, _) => RefreshHistoryPage();
        BindTimeRanges(HistoryRange.All);
    }

    /// <summary>
    /// 将图表选择框转换为绘图控件的系列显隐配置并立即重绘。
    /// </summary>
    private void ApplySeriesVisibility()
    {
        _chart.SetSeriesVisibility(new(
            _showBaseSamples.Checked,
            _showBaseRegression.Checked,
            _showOfficialSamples.Checked,
            _showOfficialRegression.Checked));
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
        _chart.ApplyAppearance();
        RefreshAllPages();
    }

    /// <summary>
    /// 刷新主窗口、四个页签、系列、时间范围和表格标题的中英文文本。
    /// </summary>
    private void ApplyLocalization()
    {
        Text = UiText.Get("ProductName");
        _dashboardTab.Text = UiText.Get("TabDashboard");
        _historyTab.Text = UiText.Get("TabHistory");
        _settingsTab.Text = UiText.Get("TabSettings");
        _diagnosticsTab.Text = UiText.Get("TabDiagnostics");
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
        _tabs.SelectedTab = _dashboardTab;
    }

    /// <summary>
    /// 选择历史与图表页并显示或激活主窗口。
    /// </summary>
    public void ShowChartTab()
    {
        ShowWindow();
        _tabs.SelectedTab = _historyTab;
    }

    /// <summary>
    /// 重新载入当前设置，选择设置页并显示或激活主窗口。
    /// </summary>
    /// <param name="settings">协调器当前已生效设置。</param>
    public void ShowSettingsTab(AppSettings settings)
    {
        _settingsPanel.LoadSettings(settings);
        ShowWindow();
        _tabs.SelectedTab = _settingsTab;
    }

    /// <summary>
    /// 选择诊断页并显示或激活主窗口。
    /// </summary>
    public void ShowDiagnosticsTab()
    {
        ShowWindow();
        _tabs.SelectedTab = _diagnosticsTab;
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
        _baseEstimateCard.SetContent(
            UiText.Get("EstimateBase"),
            EstimateValue(view.EstimatedWeeklyQuotaUsd),
            UiText.Format("DashboardEstimateCaption", view.RegressionCurve.Count));
        _officialEstimateCard.SetContent(
            UiText.Get("EstimateOfficial"),
            EstimateValue(view.OfficialLongContextEstimatedWeeklyQuotaUsd),
            UiText.Format("DashboardEstimateCaption", view.OfficialLongContextRegressionCurve.Count));
        _usedCard.SetContent(
            UiText.Get("CardUsed"),
            PercentValue(used),
            UiText.Get("DashboardCurrentWindow"));
        _remainingCard.SetContent(
            UiText.Get("CardRemaining"),
            PercentValue(remaining),
            UiText.Get("DashboardCurrentWindow"));
        _resetCard.SetContent(
            UiText.Get("CardReset"),
            view.RateLimit?.ResetsAt.LocalDateTime.ToString("g", UiText.Culture) ?? UiText.Get("Unknown"),
            UiText.Get("DashboardResetCaption"));
        _sampleCard.SetContent(
            UiText.Get("CardSamples"),
            view.SampleCount.ToString("N0", UiText.Culture),
            UiText.Format("DashboardSamplesCaption", view.ArchivedSampleCount, pendingCount));
        _connectionCard.SetContent(
            UiText.Get("CardConnection"),
            view.AppServerConnected ? UiText.Get("Connected") : UiText.Get("Disconnected"),
            UiText.Format("DashboardConnectionCaption", view.UpdatedAt.LocalDateTime));
        _dashboardStatus.Text = $"{UiText.Get("DashboardStatus")}: " +
                                (view.AppServerConnected ? UiText.Get("Connected") : UiText.Get("Disconnected"));
        _dashboardStatus.ForeColor = AppTheme.Current.MutedText;
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
            _ => throw new InvalidOperationException($"不支持的历史时间范围：{selected.Range}。")
        };
        var samples = _view.Samples.Where(sample => sample.Timestamp >= cutoff).ToArray();
        var baseCurve = _view.RegressionCurve.Where(point => point.Timestamp >= cutoff).ToArray();
        var officialCurve = _view.OfficialLongContextRegressionCurve
            .Where(point => point.Timestamp >= cutoff)
            .ToArray();

        var baseEstimate = RegressionEstimateText(
            baseCurve.LastOrDefault()?.Value,
            baseCurve.Length,
            samples.Length);
        var officialEstimate = RegressionEstimateText(
            officialCurve.LastOrDefault()?.Value,
            officialCurve.Length,
            samples.Length);
        _regressionEstimate.Text = UiText.Format(
            "CurrentEstimateFormat",
            ModeName(_settings.Regression.Mode),
            baseEstimate,
            officialEstimate);
        _regressionEstimate.ForeColor = AppTheme.Current.Accent;

        var filterHint = samples.Length > 0 && (baseCurve.Length == 0 || officialCurve.Length == 0)
            ? UiText.Get("FilterHint")
            : string.Empty;
        var authoritative = _view.RateLimit is null
            ? "--"
            : PercentValue(_view.RateLimit.UsedPercent);
        var latestValid = samples.OrderBy(sample => sample.Timestamp).LastOrDefault()?.UsedPercent;
        var pending = _view.HistoricalReplayUnattributedUsedPercents.Count == 0
            ? UiText.Get("None")
            : string.Join(", ", _view.HistoricalReplayUnattributedUsedPercents.Select(percent => PercentValue(percent)));
        _summary.Text = UiText.Format(
            "SummaryFormat",
            authoritative,
            latestValid is decimal value ? PercentValue(value) : UiText.Get("None"),
            pending,
            samples.Length,
            _view.ArchivedSampleCount,
            _settings.Regression.MaximumSampleUsd,
            filterHint);
        _summary.ForeColor = AppTheme.Current.MutedText;

        _chart.SetData(samples, baseCurve, officialCurve);
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
        _timeRange.DataSource = new[]
        {
            new RangeChoice(HistoryRange.All, UiText.Get("TimeRangeAll")),
            new RangeChoice(HistoryRange.Hours24, UiText.Get("TimeRange24Hours")),
            new RangeChoice(HistoryRange.Days7, UiText.Get("TimeRange7Days")),
            new RangeChoice(HistoryRange.Days30, UiText.Get("TimeRange30Days"))
        };
        _timeRange.SelectedItem = ((RangeChoice[])_timeRange.DataSource)
            .Single(choice => choice.Range == selected);
    }

    /// <summary>
    /// 返回当前时间范围；控件尚未绑定时按全部处理。
    /// </summary>
    private HistoryRange SelectedHistoryRange() =>
        _timeRange.SelectedItem is RangeChoice selected ? selected.Range : HistoryRange.All;

    /// <summary>
    /// 创建默认选中的曲线显隐选项，并使用对应系列颜色强化辨识。
    /// </summary>
    /// <param name="color">曲线绘制颜色。</param>
    /// <returns>可加入横向选项区的复选框。</returns>
    private static CheckBox SeriesCheckBox(Color color) => new()
    {
        Checked = true,
        AutoSize = true,
        ForeColor = color,
        Margin = new Padding(12, 6, 16, 3)
    };

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
    /// 将回归值和参与点数转换为窗口顶部的明确额度文本。
    /// </summary>
    private static string RegressionEstimateText(decimal? estimate, int acceptedPoints, int totalPoints) =>
        estimate is decimal value
            ? UiText.Format("EstimateWithPoints", value, acceptedPoints)
            : $"{UiText.Get("WaitingSamples")} ({acceptedPoints}/{totalPoints})";

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
    /// 定义历史页支持的四种时间范围。
    /// </summary>
    private enum HistoryRange
    {
        All,
        Hours24,
        Days7,
        Days30
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
