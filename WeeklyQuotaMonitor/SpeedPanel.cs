using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor;

/// <summary>在独立速度页展示同模型/层级的时间桶或响应数桶，不将端到端 TPS 冒充解码速度或 TTFT。</summary>
public sealed class SpeedPanel : UserControl
{
    private readonly Label _title = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
    private readonly MetricCard _total = new(MetricCardTone.Primary, MetricCardStyle.Hero);
    private readonly MetricCard _visible = new(MetricCardTone.Positive);
    private readonly MetricCard _count = new(MetricCardTone.Purple);
    private readonly ToolTip _tips = new();
    private readonly TableLayoutPanel _layout = new() { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 8, MinimumSize = new Size(0, 610) };
    private readonly Label _note = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
    private readonly DataGridView _grid = new()
    {
        Name = "SpeedGrid", Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, AutoGenerateColumns = false, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        Margin = new Padding(0, 8, 0, 0)
    };
    private readonly Dictionary<DataGridViewColumn, string> _keys = [];
    private readonly SpeedChartControl _chart = new();
    private readonly ComboBox _model = new() { Name = "SpeedModelFilter", DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly ComboBox _tier = new() { Name = "SpeedTierFilter", DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _metric = new() { Name = "SpeedMetric", DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
    private readonly ComboBox _range = new() { Name = "SpeedRange", DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, FlatStyle = FlatStyle.Flat };
    private readonly Label _rangeLabel = new() { AutoSize = true, Margin = new Padding(0, 7, 6, 0) };
    private ChartTimeWindow? _interactiveWindow;
    private readonly CustomTimeWindowControl _window = new() { Name = "SpeedCustomWindow", Visible = false };
    private IReadOnlyList<SpeedSample> _samples = [];
    private SpeedOptions _options = new();
    private DateTimeOffset _coverageStart;
    private string _dataStatus = string.Empty;
    public event Action<DateTimeOffset>? HistoryRequested;
    private string[] _models = [];
    private bool _binding;
    private int _dayStartMinutes;
    private string? _presentationLanguage;
    private ThemePalette? _presentationTheme;
    private Row[] _tableRows = [];
    private enum RangePreset { Retained, Hours4, Today, Hours24, Days7, Days30, Custom }
    private string? SelectedTier => _tier.SelectedIndex switch { 1 => "standard", 2 => "fast", 3 => "unknown", _ => null };

    /// <summary>使用自适应说明区与可滚动表格，避免高 DPI 下固定文本高度裁切。</summary>
    public SpeedPanel()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(20, 16, 20, 16); AutoScroll = true;
        _title.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 18, FontStyle.Bold);
        var layout = _layout;
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++) layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 64));
        layout.RowStyles.Add(new(SizeType.Percent, 36));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        var filters = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
        filters.Controls.AddRange([_rangeLabel, _range, _model, _tier, _metric]);
        _model.FlatStyle = FlatStyle.Flat; _metric.FlatStyle = FlatStyle.Flat;
        _model.SelectedIndexChanged += (_, _) => RefreshFilteredData();
        _tier.SelectedIndexChanged += (_, _) => RefreshFilteredData();
        _metric.SelectedIndexChanged += (_, _) => RefreshFilteredData();
        _range.SelectedIndexChanged += (_, _) => ChangeRange();
        _window.WindowApplied += ChangeRange;
        _chart.Navigation.WindowChanged += window =>
        {
            _interactiveWindow = window; HistoryRequested?.Invoke(window.Start); RefreshFilteredData();
        };
        _chart.Navigation.ResetRequested += ChangeRange;
        var cards = new DashboardCardGrid { WrapWhenNarrow = true, Margin = new Padding(0, 8, 0, 8) };
        cards.SetCards(_total, _visible, _count);
        layout.Controls.Add(_title, 0, 0); layout.Controls.Add(filters, 0, 1); layout.Controls.Add(_window, 0, 2);
        layout.Controls.Add(cards, 0, 3); layout.Controls.Add(_note, 0, 4);
        layout.Controls.Add(_chart, 0, 5); layout.Controls.Add(_grid, 0, 6); layout.Controls.Add(_status, 0, 7);
        Controls.Add(layout);
        AddColumn("SpeedStart", nameof(Row.Start), "MM-dd HH:mm:ss");
        AddColumn("SpeedEnd", nameof(Row.End), "MM-dd HH:mm:ss");
        AddColumn("SpeedModel", nameof(Row.Model));
        AddColumn("SpeedTier", nameof(Row.Tier));
        AddColumn("SpeedSamples", nameof(Row.Count), "N0");
        AddColumn("SpeedTotalTps", nameof(Row.TotalTps), "N2");
        AddColumn("SpeedVisibleTps", nameof(Row.VisibleTps), "N2");
        AddColumn("SpeedSeconds", nameof(Row.Seconds), "N2");
        AddColumn("SpeedStreamingTps", nameof(Row.StreamingTps), "N2");
        AddColumn("SpeedStreamingCount", nameof(Row.StreamingCount), "N0");
        AddColumn("SpeedStreamingExclusions", nameof(Row.StreamingExclusions));
        AddColumn("SpeedBucketStatus", nameof(Row.Status));
        _grid.DefaultCellStyle.Padding = new Padding(4);
    }

    /// <summary>按换行筛选器、说明和指标卡的实际高度预留绘图空间，高 DPI 不挤掉图表。</summary>
    protected override void OnLayout(LayoutEventArgs e)
    {
        var width = Math.Max(1, ClientSize.Width - Padding.Horizontal - 8);
        foreach (var label in new[] { _title, _note, _status }) label.MaximumSize = new Size(width, 0);
        var required = 420 * DeviceDpi / 96;
        foreach (var row in new[] { 0, 1, 2, 3, 4, 7 })
        {
            var control = _layout.GetControlFromPosition(0, row);
            if (control is not null && control.Visible)
                required += control.GetPreferredSize(new Size(width, 0)).Height + control.Margin.Vertical;
        }
        _layout.Height = Math.Max(required, ClientSize.Height - Padding.Vertical);
        AutoScrollMinSize = new Size(0, _layout.Height + Padding.Vertical);
        base.OnLayout(e);
    }

    /// <summary>刷新速度结果与监控日起点，免费自动审批只在展示层过滤；仅模型或语言变化时重建筛选器。</summary>
    public void UpdateView(MonitorViewSnapshot view, SpeedOptions options, int dayStartMinutes = 0)
    {
        _title.Text = UiText.Get("SpeedTitle");
        _rangeLabel.Text = UiText.Get("ChartInitialRange");
        _samples = SpeedDisplayFilter.VisibleSamples(view.SpeedSamples); _options = options;
        _dayStartMinutes = dayStartMinutes;
        var models = _samples.Select(sample => sample.Model).Distinct().OrderBy(model => model).ToArray();
        var rebuildFilters = _presentationLanguage != UiText.Culture.Name || !_models.SequenceEqual(models);
        _models = models;
        _binding = true;
        var selectedModel = _model.SelectedIndex > 0 ? _model.SelectedItem as string : null;
        var selectedMetric = Math.Max(0, _metric.SelectedIndex);
        var selectedTier = Math.Max(0, _tier.SelectedIndex);
        var firstView = _range.SelectedIndex < 0;
        var selectedRange = firstView ? (int)RangePreset.Hours4 : _range.SelectedIndex;
        if (firstView || rebuildFilters)
        {
            _model.Items.Clear(); _model.Items.Add(UiText.Get("SpeedAllModels")); _model.Items.AddRange(_models);
            _model.SelectedIndex = selectedModel is not null && _models.Contains(selectedModel) ? Array.IndexOf(_models, selectedModel) + 1 : 0;
            _metric.Items.Clear(); _metric.Items.AddRange([UiText.Get("SpeedTotalTps"), UiText.Get("SpeedVisibleTps"), UiText.Get("SpeedStreamingTps")]);
            _metric.SelectedIndex = selectedMetric;
            _tier.Items.Clear(); _tier.Items.AddRange(new string?[] { null, "standard", "fast", "unknown" }.Select(UiText.SpeedTierName).ToArray());
            _tier.SelectedIndex = selectedTier; _tier.AccessibleName = UiText.Get("SpeedTier");
            _range.Items.Clear(); _range.Items.AddRange(new[] { "SpeedRetainedRange", "TimeRange4Hours", "TimeRangeToday", "TimeRange24Hours", "TimeRange7Days", "TimeRange30Days", "TimeRangeCustom" }.Select(UiText.Get).ToArray());
            _range.SelectedIndex = selectedRange; _window.ApplyLanguage();
        }
        _binding = false;
        _note.Text = UiText.Get("SpeedShortNote");
        _tips.SetToolTip(_note, UiText.Get("SpeedMeasurementNote") + "\n" + UiText.Get("SpeedTtftUnavailable"));
        var from = _samples.Count == 0 ? "—" : _samples.Min(sample => sample.EndedAt).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        var to = _samples.Count == 0 ? "—" : _samples.Max(sample => sample.EndedAt).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        _coverageStart = DateTimeOffset.Now.AddHours(-view.SpeedHistoryHours);
        _dataStatus = UiText.Format("SpeedCoverage", from, to, _samples.Count) + "\n" + view.SpeedStatus;
        foreach (var pair in _keys) pair.Key.HeaderText = UiText.Get(pair.Value);
        if (_presentationTheme != AppTheme.Current) AppTheme.Apply(this);
        _presentationTheme = AppTheme.Current; _presentationLanguage = UiText.Culture.Name;
        if (firstView) ChangeRange(); else RefreshFilteredData();
        _note.ForeColor = AppTheme.Current.MutedText;
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = AppTheme.Current.SurfaceAlternate;
        _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = AppTheme.Current.Text;
    }

    /// <summary>用户改变范围时请求更早的日志，界面刷新不重复触发补读。</summary>
    private void ChangeRange()
    {
        if (_binding) return;
        _interactiveWindow = null;
        _window.Visible = _range.SelectedIndex == (int)RangePreset.Custom;
        var (start, _) = WindowBounds(DateTimeOffset.Now);
        if (_range.SelectedIndex != 0) HistoryRequested?.Invoke(start);
        RefreshFilteredData();
    }

    /// <summary>快捷范围随当前时间滚动，自定义起止固定；全部缓存不暗示所有历史日志均已采集。</summary>
    private (DateTimeOffset Start, DateTimeOffset End) WindowBounds(DateTimeOffset now, bool includeInteractive = true) => includeInteractive && _interactiveWindow is { } window
        ? (window.Start, window.End) : (RangePreset)_range.SelectedIndex switch
    {
        RangePreset.Retained => (DateTimeOffset.MinValue, now),
        RangePreset.Hours4 => (now.AddHours(-4), now),
        RangePreset.Today => (ChartTimeWindow.CurrentDay(now, _dayStartMinutes).Start, now),
        RangePreset.Hours24 => (now.AddHours(-24), now),
        RangePreset.Days7 => (now.AddDays(-7), now), RangePreset.Days30 => (now.AddDays(-30), now),
        RangePreset.Custom => (_window.Start, _window.End),
        _ => throw new InvalidOperationException("未选择有效的速度时间范围。")
    };

    /// <summary>聚合视野与独立表格范围；流式估值及对照仅用同批有效响应，显示覆盖率和排除原因。</summary>
    private void RefreshFilteredData()
    {
        if (_binding) return;
        var now = DateTimeOffset.Now;
        var (start, end) = WindowBounds(now);
        var (tableStart, tableEnd) = WindowBounds(now, includeInteractive: false);
        var requestedStart = start;
        var modelSamples = _samples.Where(sample => sample.EndedAt <= now && sample.OutputTokens > 0 &&
            sample.DurationSeconds >= _options.MinimumDurationSeconds &&
            (SelectedTier is null || sample.ServiceTier == SelectedTier) &&
            (_model.SelectedIndex <= 0 || sample.Model == _model.SelectedItem as string)).ToArray();
        var inPreset = modelSamples.Where(sample => sample.EndedAt >= tableStart && sample.EndedAt <= tableEnd).ToArray();
        var emptyPreset = !_interactiveWindow.HasValue && inPreset.Length == 0 && _range.SelectedIndex != (int)RangePreset.Custom;
        _chart.Navigation.SetDataBounds(!emptyPreset && modelSamples.Length > 0 ? modelSamples.Min(sample => sample.EndedAt) : null,
            !emptyPreset && modelSamples.Length > 0 ? modelSamples.Max(sample => sample.EndedAt) : null);
        if (!_interactiveWindow.HasValue && inPreset.Length > 0) end = inPreset.Max(sample => sample.EndedAt);
        var visibleStart = start == DateTimeOffset.MinValue ? (modelSamples.Length > 0 ? modelSamples.Min(sample => sample.EndedAt) : end.AddDays(-1)) : start;
        var window = _chart.Navigation.SetWindow(visibleStart, end);
        start = visibleStart = window.Start; end = window.End;
        if (_interactiveWindow.HasValue) _interactiveWindow = window;
        _note.Text = UiText.Format("ChartInteractionHint", visibleStart.LocalDateTime, end.LocalDateTime) + "\n" + UiText.Get("SpeedShortNote") + " " + UiText.Get("ChartTableScopeHint");
        _status.Text = _dataStatus + ((_range.SelectedIndex != 0 || _interactiveWindow.HasValue) && requestedStart < _coverageStart ? "\n" + UiText.Get("SpeedWindowLoading") : string.Empty);
        var buckets = SpeedMonitoring.Aggregate(modelSamples, _options, now, start, end);
        var filtered = buckets.Where(bucket => _model.SelectedIndex <= 0 || bucket.Model == _model.SelectedItem as string)
            .OrderByDescending(bucket => bucket.End).ThenBy(bucket => bucket.Model).ToArray();
        var seconds = filtered.Sum(bucket => bucket.DurationSeconds);
        var scope = UiText.SpeedTierName(SelectedTier);
        var streaming = _metric.SelectedIndex == 2;
        var streamCount = filtered.Sum(b => b.StreamingCount); var allCount = filtered.Sum(b => b.Count);
        var streamSeconds = filtered.Sum(b => b.StreamingSeconds);
        var matchedSeconds = filtered.Sum(b => b.MatchedEffectiveSeconds);
        _total.SetContent(UiText.Get(streaming ? "SpeedStreamingTps" : "SpeedTotalTps"),
            streaming ? streamSeconds > 0 ? $"{filtered.Sum(b => b.StreamingOutputTokens) / streamSeconds:N2}" : "—" :
                seconds > 0 ? $"{filtered.Sum(bucket => bucket.OutputTokens) / seconds:N2}" : "—", scope + " · " + UiText.Get("SpeedWindowWeighted"));
        _visible.SetContent(UiText.Get(streaming ? "SpeedMatchedEffectiveTps" : "SpeedVisibleTps"),
            streaming ? matchedSeconds > 0 ? $"{filtered.Sum(b => b.StreamingOutputTokens) / matchedSeconds:N2}" : "—" :
                seconds > 0 ? $"{filtered.Sum(bucket => bucket.VisibleTokens) / seconds:N2}" : "—",
            UiText.Get(streaming ? "SpeedSameSamplesNote" : "SpeedVisibleCaption"));
        _count.SetContent(UiText.Get(streaming ? "SpeedStreamingCount" : "SpeedSamples"),
            streaming ? $"{streamCount:N0} / {allCount:N0}" : allCount.ToString("N0"),
            streaming ? UiText.Format("SpeedStreamingCoverage", streamCount, allCount, allCount > 0 ? 100d * streamCount / allCount : 0) :
                UiText.Format("SpeedGroupCaption", filtered.Length, filtered.Select(bucket => bucket.Model).Distinct().Count()));
        _status.Text += "\n" + UiText.Format("SpeedTierScope", scope,
            filtered.Where(b => b.ServiceTier == "standard").Sum(b => b.Count),
            filtered.Where(b => b.ServiceTier == "fast").Sum(b => b.Count),
            filtered.Where(b => b.ServiceTier == "unknown").Sum(b => b.Count));
        if (SelectedTier == "unknown" || filtered.Any(b => b.ServiceTier == "unknown"))
            _status.Text += "\n" + UiText.Get("SpeedUnknownNote");
        _status.Text += "\n" + UiText.Format("SpeedStreamingCoverage", streamCount, allCount, allCount > 0 ? 100d * streamCount / allCount : 0);
        if (streaming)
        {
            _note.Text += "\n" + UiText.Get("SpeedStreamingNote");
            var exclusions = filtered.SelectMany(b => b.StreamingExclusions).GroupBy(p => p.Key)
                .ToDictionary(g => g.Key, g => g.Sum(p => p.Value));
            _status.Text += "\n" + UiText.Get("SpeedStreamingExclusions") + ": " + ExclusionText(exclusions);
        }
        _chart.SetData(filtered, _models, _metric.SelectedIndex == 1, streaming);
        var tableBuckets = SpeedMonitoring.Aggregate(modelSamples, _options, now, tableStart, tableEnd)
            .OrderByDescending(bucket => bucket.End).ThenBy(bucket => bucket.Model).ToArray();
        var rows = tableBuckets.Select(bucket => new Row(bucket.Start.LocalDateTime,
            bucket.End.LocalDateTime, bucket.Model, UiText.SpeedTierName(bucket.ServiceTier), bucket.Count,
            bucket.TotalTps, bucket.VisibleTps, bucket.DurationSeconds,
            bucket.StreamingTps, bucket.StreamingCount, ExclusionText(bucket.StreamingExclusions),
            UiText.Get(bucket.Complete ? "SpeedComplete" : "SpeedPartial"))).ToArray();
        if (_grid.DataSource is null || !_tableRows.SequenceEqual(rows))
        { _grid.DataSource = rows; _tableRows = rows; _grid.ClearSelection(); }
    }

    /// <summary>把固定缺失原因及数量本地化，零缺失明确显示无；不隐去没有流式观测的桶。</summary>
    private static string ExclusionText(IReadOnlyDictionary<string, int> exclusions) => exclusions.Count == 0
        ? UiText.Get("None") : string.Join(" · ", exclusions.OrderByDescending(p => p.Value)
            .Select(p => $"{UiText.Get("SpeedStreamReason_" + p.Key)} {p.Value:N0}"));

    /// <summary>创建可本地化的只读列，保留实际数值供排序而不是预先格式化字符串。</summary>
    private void AddColumn(string key, string property, string? format = null)
    {
        var width = property == nameof(Row.StreamingExclusions) ? 220 :
            property is nameof(Row.Start) or nameof(Row.End) ? 135 : property is nameof(Row.Model) or nameof(Row.Tier) ? 130 : 80;
        var column = new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = UiText.Get(key), MinimumWidth = width, FillWeight = width };
        column.DefaultCellStyle.Format = format;
        if (property is nameof(Row.Tier) or nameof(Row.StreamingExclusions)) column.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.Columns.Add(column); _keys[column] = key;
    }

    /// <summary>释放测量口径说明提示。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }

    private sealed record Row(DateTime Start, DateTime End, string Model, string Tier, int Count,
        double TotalTps, double VisibleTps, double Seconds, double? StreamingTps, int StreamingCount, string StreamingExclusions, string Status);
}
