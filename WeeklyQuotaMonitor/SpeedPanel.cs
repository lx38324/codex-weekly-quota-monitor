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
    private readonly ComboBox _metric = new() { Name = "SpeedMetric", DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
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
        filters.Controls.AddRange([_rangeLabel, _range, _model, _metric]);
        _model.FlatStyle = FlatStyle.Flat; _metric.FlatStyle = FlatStyle.Flat;
        _model.SelectedIndexChanged += (_, _) => RefreshFilteredData();
        _metric.SelectedIndexChanged += (_, _) => RefreshFilteredData();
        _range.SelectedIndexChanged += (_, _) => ChangeRange();
        _window.WindowApplied += ChangeRange;
        _chart.Navigation.WindowChanged += window =>
        {
            _interactiveWindow = window; HistoryRequested?.Invoke(window.Start); RefreshFilteredData();
        };
        _chart.Navigation.ResetRequested += ChangeRange;
        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 8, 0, 8) };
        for (var i = 0; i < 3; i++) cards.ColumnStyles.Add(new(SizeType.Percent, i == 0 ? 40 : 30));
        cards.RowStyles.Add(new(SizeType.AutoSize));
        cards.Controls.Add(_total, 0, 0); cards.Controls.Add(_visible, 1, 0); cards.Controls.Add(_count, 2, 0);
        foreach (var card in new[] { _total, _visible, _count }) card.Dock = DockStyle.Fill;
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
        AddColumn("SpeedBucketStatus", nameof(Row.Status));
        _grid.DefaultCellStyle.Padding = new Padding(4);
    }

    /// <summary>约束说明文字的实际宽度，让区域随翻译和 DPI 自动增高。</summary>
    protected override void OnLayout(LayoutEventArgs e)
    {
        var width = Math.Max(1, ClientSize.Width - Padding.Horizontal - 8);
        foreach (var label in new[] { _title, _note, _status }) label.MaximumSize = new Size(width, 0);
        _layout.Height = Math.Max(_layout.MinimumSize.Height, ClientSize.Height - Padding.Vertical);
        AutoScrollMinSize = new Size(0, _layout.Height + Padding.Vertical);
        base.OnLayout(e);
    }

    /// <summary>刷新已聚合的速度结果与测量边界；空数据不显示伪零 TPS。</summary>
    public void UpdateView(MonitorViewSnapshot view, SpeedOptions options)
    {
        _title.Text = UiText.Get("SpeedTitle");
        _rangeLabel.Text = UiText.Get("ChartInitialRange");
        _samples = view.SpeedSamples; _options = options;
        _models = _samples.Select(sample => sample.Model).Distinct().OrderBy(model => model).ToArray();
        _binding = true;
        var selectedModel = _model.SelectedIndex > 0 ? _model.SelectedItem as string : null;
        var selectedMetric = Math.Max(0, _metric.SelectedIndex);
        var firstView = _range.SelectedIndex < 0;
        var selectedRange = firstView ? 1 : _range.SelectedIndex;
        _model.Items.Clear(); _model.Items.Add(UiText.Get("SpeedAllModels")); _model.Items.AddRange(_models);
        _model.SelectedIndex = selectedModel is not null && _models.Contains(selectedModel) ? Array.IndexOf(_models, selectedModel) + 1 : 0;
        _metric.Items.Clear(); _metric.Items.AddRange([UiText.Get("SpeedTotalTps"), UiText.Get("SpeedVisibleTps")]);
        _metric.SelectedIndex = selectedMetric;
        _range.Items.Clear(); _range.Items.AddRange(new[] { "SpeedRetainedRange", "TimeRange24Hours", "TimeRange7Days", "TimeRange30Days", "TimeRangeCustom" }.Select(UiText.Get).ToArray());
        _range.SelectedIndex = selectedRange; _window.ApplyLanguage();
        _binding = false;
        _note.Text = UiText.Get("SpeedShortNote");
        _tips.SetToolTip(_note, UiText.Get("SpeedMeasurementNote") + "\n" + UiText.Get("SpeedTtftUnavailable"));
        var from = _samples.Count == 0 ? "—" : _samples.Min(sample => sample.EndedAt).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        var to = _samples.Count == 0 ? "—" : _samples.Max(sample => sample.EndedAt).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        _coverageStart = DateTimeOffset.Now.AddHours(-view.SpeedHistoryHours);
        _dataStatus = UiText.Format("SpeedCoverage", from, to, view.SpeedSampleCount) + "\n" + view.SpeedStatus;
        foreach (var pair in _keys) pair.Key.HeaderText = UiText.Get(pair.Value);
        AppTheme.Apply(this);
        RefreshFilteredData();
        _note.ForeColor = AppTheme.Current.MutedText;
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = AppTheme.Current.SurfaceAlternate;
        _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = AppTheme.Current.Text;
        if (firstView) ChangeRange();
    }

    /// <summary>用户改变范围时请求更早的日志，界面刷新不重复触发补读。</summary>
    private void ChangeRange()
    {
        if (_binding) return;
        _interactiveWindow = null;
        _window.Visible = _range.SelectedIndex == 4;
        var (start, _) = WindowBounds(DateTimeOffset.Now);
        if (_range.SelectedIndex != 0) HistoryRequested?.Invoke(start);
        RefreshFilteredData();
    }

    /// <summary>快捷范围随当前时间滚动，自定义起止固定；全部缓存不暗示所有历史日志均已采集。</summary>
    private (DateTimeOffset Start, DateTimeOffset End) WindowBounds(DateTimeOffset now) => _interactiveWindow is { } window
        ? (window.Start, window.End) : _range.SelectedIndex switch
    {
        0 => (DateTimeOffset.MinValue, now), 2 => (now.AddDays(-7), now), 3 => (now.AddDays(-30), now),
        4 => (_window.Start, _window.End), _ => (now.AddHours(-24), now)
    };

    /// <summary>先在响应摘要上筛选时间再聚合，指标、图表和明细共享结果；均值为总 token/累计秒数。</summary>
    private void RefreshFilteredData()
    {
        if (_binding) return;
        var now = DateTimeOffset.Now;
        var (start, end) = WindowBounds(now);
        var visibleStart = start == DateTimeOffset.MinValue ? (_samples.Count > 0 ? _samples.Min(sample => sample.EndedAt) : end.AddDays(-1)) : start;
        _chart.Navigation.SetWindow(visibleStart, end);
        _note.Text = UiText.Format("ChartInteractionHint", visibleStart.LocalDateTime, end.LocalDateTime) + "\n" + UiText.Get("SpeedShortNote");
        _status.Text = _dataStatus + ((_range.SelectedIndex != 0 || _interactiveWindow.HasValue) && start < _coverageStart ? "\n" + UiText.Get("SpeedWindowLoading") : string.Empty);
        var buckets = SpeedMonitoring.Aggregate(_samples, _options, now, start, end);
        var filtered = buckets.Where(bucket => _model.SelectedIndex <= 0 || bucket.Model == _model.SelectedItem as string)
            .OrderByDescending(bucket => bucket.End).ThenBy(bucket => bucket.Model).ToArray();
        var seconds = filtered.Sum(bucket => bucket.DurationSeconds);
        _total.SetContent(UiText.Get("SpeedTotalTps"), seconds > 0 ? $"{filtered.Sum(bucket => bucket.OutputTokens) / seconds:N2}" : "—", UiText.Get("SpeedWindowWeighted"));
        _visible.SetContent(UiText.Get("SpeedVisibleTps"), seconds > 0 ? $"{filtered.Sum(bucket => bucket.VisibleTokens) / seconds:N2}" : "—", UiText.Get("SpeedVisibleCaption"));
        _count.SetContent(UiText.Get("SpeedSamples"), filtered.Sum(bucket => bucket.Count).ToString("N0"),
            UiText.Format("SpeedGroupCaption", filtered.Length, filtered.Select(bucket => bucket.Model).Distinct().Count()));
        _chart.SetData(filtered, _models, _metric.SelectedIndex == 1);
        _grid.DataSource = filtered.Select(bucket => new Row(bucket.Start.LocalDateTime,
            bucket.End.LocalDateTime, bucket.Model, bucket.ServiceTier, bucket.Count,
            bucket.TotalTps, bucket.VisibleTps, bucket.DurationSeconds,
            UiText.Get(bucket.Complete ? "SpeedComplete" : "SpeedPartial"))).ToList();
        _grid.ClearSelection();
    }

    /// <summary>创建可本地化的只读列，保留实际数值供排序而不是预先格式化字符串。</summary>
    private void AddColumn(string key, string property, string? format = null)
    {
        var width = property is nameof(Row.Start) or nameof(Row.End) ? 135 : property == nameof(Row.Model) ? 125 : 80;
        var column = new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = UiText.Get(key), MinimumWidth = width, FillWeight = width };
        column.DefaultCellStyle.Format = format;
        _grid.Columns.Add(column); _keys[column] = key;
    }

    /// <summary>释放测量口径说明提示。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }

    private sealed record Row(DateTime Start, DateTime End, string Model, string Tier, int Count,
        double TotalTps, double VisibleTps, double Seconds, string Status);
}
