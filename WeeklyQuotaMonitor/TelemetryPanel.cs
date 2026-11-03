using Microsoft.Data.Sqlite;

namespace WeeklyQuotaMonitor;

/// <summary>在速度页内展示 OTLP 毫秒指标，按模型、明确层级和自由时间窗分组；没有观测时不显示伪零。</summary>
public sealed class TelemetryPanel : UserControl
{
    private readonly TelemetryService _service;
    private readonly ToolTip _tips = new();
    private readonly ComboBox _metric = new() { Name = "TelemetryMetric", Width = 280, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _model = new() { Name = "TelemetryModel", Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _tier = new() { Name = "TelemetryTier", Width = 230, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _origin = new() { Name = "TelemetryOrigin", Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _mismatches = new() { Name = "TelemetryMismatches", AutoSize = true };
    private static readonly string?[] Origins = [null, "unknown", "memory_extraction", "memory_consolidation", "auto_review", "subagent", "conflicting"];
    private readonly ComboBox _range = new() { Name = "TelemetryRange", Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
    private enum RangePreset { Retained, Hours4, Today, Hours24, Days7, Days30, Custom }
    private int _dayStartMinutes;
    private readonly NumericUpDown _minutes = new() { Minimum = 1, Maximum = 1440, Value = 5, Width = 75 };
    private readonly Button _diagnosticsButton = new() { AutoSize = true };
    private readonly Label _title = new() { AutoSize = true };
    private readonly Label _bucketLabel = new() { AutoSize = true, Margin = new Padding(4, 7, 4, 0) };
    private readonly Label _note = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 8) };
    private readonly Label _status = new() { Name = "TelemetryStatus", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
    private readonly MetricCard _mean = new(MetricCardTone.Primary, MetricCardStyle.Hero);
    private readonly MetricCard _count = new(MetricCardTone.Positive);
    private readonly MetricCard _resolution = new(MetricCardTone.Purple);
    private readonly SpeedChartControl _chart = new();
    private readonly CustomTimeWindowControl _custom = new() { Visible = false };
    private readonly DataGridView _grid = new() { Name = "TelemetryGrid", Dock = DockStyle.Fill, ReadOnly = true,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoGenerateColumns = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders, BorderStyle = BorderStyle.None };
    private readonly TableLayoutPanel _layout = new() { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 8, MinimumSize = new Size(0, 650) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    private ChartTimeWindow? _interactive;
    private bool _binding, _loading, _queued;
    private long _revision = -1;
    private Task<TelemetryQuery>? _query;
    private QueryRequest? _request;
    private string? SelectedTier => _tier.SelectedIndex switch { 1 => "standard", 2 => "fast", 3 => "unknown", _ => null };
    private static readonly string[] Metrics = [.. TelemetryMetrics.Names, RequestTelemetry.Metric];

    /// <summary>用可换行过滤器、自动高度指标卡与可滚动图表组合，刷新仅在页面可见时查询。</summary>
    public TelemetryPanel(TelemetryService service, int dayStartMinutes = 0)
    {
        _dayStartMinutes = dayStartMinutes;
        _service = service; Dock = DockStyle.Fill; AutoScroll = true; Padding = new Padding(20, 16, 20, 16);
        _title.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 18, FontStyle.Bold);
        _layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++) _layout.RowStyles.Add(new(SizeType.AutoSize));
        _layout.RowStyles.Add(new(SizeType.Percent, 65)); _layout.RowStyles.Add(new(SizeType.Percent, 35)); _layout.RowStyles.Add(new(SizeType.AutoSize));
        var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        var bucketEditor = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        bucketEditor.Controls.AddRange([_bucketLabel, _minutes]);
        filters.Controls.AddRange([_range, _metric, _model, _tier, _origin, _mismatches, bucketEditor, _diagnosticsButton]);
        _diagnosticsButton.Click += async (_, _) => await ShowDiagnosticsAsync();
        var cards = new DashboardCardGrid();
        cards.SetCards(_mean, _count, _resolution);
        _layout.Controls.Add(_title, 0, 0); _layout.Controls.Add(filters, 0, 1); _layout.Controls.Add(_custom, 0, 2);
        _layout.Controls.Add(cards, 0, 3); _layout.Controls.Add(_note, 0, 4); _layout.Controls.Add(_chart, 0, 5);
        _layout.Controls.Add(_grid, 0, 6); _layout.Controls.Add(_status, 0, 7); Controls.Add(_layout);
        foreach (var (property, key, format) in new[] { ("Start", "SpeedStart", "MM-dd HH:mm:ss"), ("End", "SpeedEnd", "MM-dd HH:mm:ss"),
            ("Model", "SpeedModel", ""), ("LocalModel", "OtelLocalModel", ""), ("Association", "OtelAssociation", ""),
            ("ModelMismatches", "OtelMismatchCount", "N0"), ("Origin", "OtelOrigin", ""), ("MatchBasis", "OtelMatchBasis", ""),
            ("Tier", "SpeedTier", ""), ("TierSource", "OtelTierSource", ""), ("Count", "OtelObservations", "N0"), ("Mean", "OtelMean", "N2"), ("Resolution", "OtelResolution", "") })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = key, DataPropertyName = property, MinimumWidth = property is "Start" or "End" ? 140 : 90,
                DefaultCellStyle = new DataGridViewCellStyle { Format = format, Padding = new Padding(4),
                    WrapMode = DataGridViewTriState.True } });
        _metric.SelectedIndexChanged += (_, _) => ResetWindow(); _model.SelectedIndexChanged += (_, _) => ResetWindow();
        _tier.SelectedIndexChanged += (_, _) => ResetWindow();
        _origin.SelectedIndexChanged += (_, _) => ResetWindow(); _mismatches.CheckedChanged += (_, _) => ResetWindow();
        _range.SelectedIndexChanged += (_, _) => ResetWindow(); _minutes.ValueChanged += (_, _) => RequestRefresh();
        _custom.WindowApplied += ResetWindow;
        _chart.Navigation.WindowChanged += window => { _interactive = window; RequestRefresh(); };
        _chart.Navigation.ResetRequested += ResetWindow;
        VisibleChanged += (_, _) =>
        {
            if (Visible && IsHandleCreated) BeginInvoke((Action)(() =>
            { if (!IsDisposed && !Disposing) { ApplyLanguage(); RequestRefresh(); } }));
        };
        _timer.Tick += (_, _) =>
        {
            if (_query?.IsCompleted == true) CompleteRefresh();
            else if (!_loading && Visible && (_revision != _service.Revision || !string.IsNullOrEmpty(_service.Error))) RequestRefresh();
        };
        ApplyLanguage(); _timer.Start();
    }

    /// <summary>切换语言时保留选择，字段及时间窗均跟随当前本地化；不改动遥测源。</summary>
    public void ApplyLanguage()
    {
        _binding = true;
        var metric = Math.Max(0, _metric.SelectedIndex); var range = _range.SelectedIndex < 0 ? 1 : _range.SelectedIndex;
        var tier = Math.Max(0, _tier.SelectedIndex);
        var origin = Math.Max(0, _origin.SelectedIndex);
        _origin.Items.Clear(); _origin.Items.AddRange(Origins.Select(o => UiText.Get("OtelEvidence_" + (o ?? "all"))).ToArray());
        _origin.SelectedIndex = origin; _origin.AccessibleName = UiText.Get("OtelOrigin");
        _mismatches.Text = UiText.Get("OtelMismatchesOnly");
        _tier.Items.Clear(); _tier.Items.AddRange(new string?[] { null, "standard", "fast", "unknown" }.Select(UiText.SpeedTierName).ToArray());
        _tier.SelectedIndex = tier; _tier.AccessibleName = UiText.Get("SpeedTier");
        _metric.Items.Clear(); _metric.Items.AddRange(TelemetryMetrics.LabelKeys.Append("OtelRequestTtft").Select(UiText.Get).ToArray()); _metric.SelectedIndex = metric;
        _range.Items.Clear(); _range.Items.AddRange(new[] { "SpeedRetainedRange", "TimeRange4Hours", "TimeRangeToday", "TimeRange24Hours", "TimeRange7Days", "TimeRange30Days", "TimeRangeCustom" }.Select(UiText.Get).ToArray()); _range.SelectedIndex = range;
        if (_model.Items.Count == 0) { _model.Items.Add(UiText.Get("SpeedAllModels")); _model.SelectedIndex = 0; }
        else _model.Items[0] = UiText.Get("SpeedAllModels");
        _title.Text = UiText.Get("OtelTitle"); _bucketLabel.Text = UiText.Get("OtelBucketMinutes");
        _diagnosticsButton.Text = UiText.Get("OtelDiagnosticsDetails");
        foreach (DataGridViewColumn column in _grid.Columns) column.HeaderText = UiText.Get(column.Name);
        _custom.ApplyLanguage(); AppTheme.Apply(this); _binding = false;
    }

    /// <summary>按实际宽度预留说明与状态高度，额外保留绘图和表格空间；高缩放内容通过滚动访问。</summary>
    protected override void OnLayout(LayoutEventArgs e)
    {
        var width = Math.Max(1, ClientSize.Width - Padding.Horizontal - 24);
        _note.MaximumSize = _status.MaximumSize = new Size(width, 0);
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

    /// <summary>下拉范围只重设初始视口，后续范围由图表缩放/拖拽控制。</summary>
    private void ResetWindow()
    {
        if (_binding) return; _interactive = null; _custom.Visible = _range.SelectedIndex == (int)RangePreset.Custom; RequestRefresh();
    }

    /// <summary>同步监控日起点；当天预设立即重算，显式缩放视口及其他选择保持不变。</summary>
    public void UpdateDayStart(int minutes)
    {
        _dayStartMinutes = minutes;
        if (!_interactive.HasValue && _range.SelectedIndex == (int)RangePreset.Today) RequestRefresh();
    }

    /// <summary>合并连续交互请求；只将纯数据库查询交给线程池，不让后台 await 向已销毁的窗口回调。</summary>
    private void RequestRefresh()
    {
        if (_binding || !Visible || IsDisposed || !IsHandleCreated) return;
        if (_loading) { _queued = true; return; }
        _queued = false; _loading = true;
        var now = DateTimeOffset.UtcNow;
        var start = _interactive?.Start ?? ((RangePreset)_range.SelectedIndex switch
        {
            RangePreset.Retained => DateTimeOffset.UnixEpoch,
            RangePreset.Hours4 => now.AddHours(-4),
            RangePreset.Today => ChartTimeWindow.CurrentDay(now, _dayStartMinutes).Start,
            RangePreset.Hours24 => now.AddDays(-1), RangePreset.Days7 => now.AddDays(-7),
            RangePreset.Days30 => now.AddDays(-30), RangePreset.Custom => _custom.Start,
            _ => throw new InvalidOperationException("OTel 时间范围尚未绑定。")
        });
        var end = _interactive?.End ?? (_range.SelectedIndex == (int)RangePreset.Custom ? _custom.End : now);
        var request = new QueryRequest(Metrics[Math.Max(0, _metric.SelectedIndex)],
            _model.SelectedIndex > 0 ? _model.SelectedItem as string : null, SelectedTier, start, end, (int)_minutes.Value, _service.Revision,
            Origins[Math.Max(0, _origin.SelectedIndex)], _mismatches.Checked);
        _request = request;
        _query = Task.Run(() => _service.Query(request.Metric, request.Model, request.Start, request.End, request.Minutes, request.Tier, request.Origin, request.MismatchesOnly));
        // 仅查询在途时短周期收取结果；闲置时仍为五秒，隐藏页不启动查询。
        _timer.Interval = 50;
    }

    /// <summary>由 UI 计时器收取结果并过滤免费审批的展示副本；销毁页面只释放计时器，不修改原始遥测。</summary>
    private void CompleteRefresh()
    {
        var task = _query!; var request = _request!;
        _query = null; _request = null; _loading = false; _timer.Interval = 5000;
        try
        {
            var data = SpeedDisplayFilter.VisibleTelemetry(task.GetAwaiter().GetResult(), request.Start);
            if (_queued) { RequestRefresh(); return; }
            if (!Visible) return;
            if (request.Model is not null && SpeedDisplayFilter.IsFreeReview(request.Model, request.Start) &&
                !data.Models.Contains(request.Model, StringComparer.OrdinalIgnoreCase))
            {
                // 从旧计费窗口切回当前免费窗口时，已隐藏的筛选不能被重新加回，也不能让“全部模型”沿用空的单模型查询。
                _binding = true; _model.SelectedIndex = 0; _binding = false;
                RequestRefresh(); return;
            }
            var now = DateTimeOffset.UtcNow;
            // 空快捷窗必须保持当前时段，不能通过全历史边界把最近4小时悄悄跳回昨天。
            var emptyPreset = data.Buckets.Count == 0 && !_interactive.HasValue && _range.SelectedIndex != (int)RangePreset.Custom;
            _chart.Navigation.SetDataBounds(emptyPreset ? null : data.First,
                emptyPreset ? null : data.Last is { } last && last > now ? now : data.Last);
            var window = _chart.Navigation.SetWindow(request.Start, request.End);
            if (_interactive.HasValue) _interactive = window;
            Render(data, request.Model, window); _revision = request.Revision;
        }
        catch (Exception e) when (e is SqliteException or IOException or InvalidDataException)
        { _status.Text = UiText.Get("OtelReadFailed") + " " + e.Message; }
    }

    /// <summary>图表、均值和观测次数使用同一查询结果；跨边界导出区间明确提示，不伪造逐请求精度。</summary>
    private void Render(TelemetryQuery data, string? selectedModel, ChartTimeWindow window)
    {
        _binding = true;
        _model.Items.Clear(); _model.Items.Add(UiText.Get("SpeedAllModels")); _model.Items.AddRange(data.Models);
        if (selectedModel is not null && !_model.Items.Contains(selectedModel)) _model.Items.Add(selectedModel);
        _model.SelectedIndex = selectedModel is null ? 0 : _model.Items.IndexOf(selectedModel);
        _binding = false;
        var count = data.Buckets.Sum(b => b.Count); var resolution = data.Buckets.Count > 0 ? data.Buckets.Max(b => b.ResolutionSeconds) : 0;
        var scope = UiText.SpeedTierName(SelectedTier);
        var requests = Metrics[Math.Max(0, _metric.SelectedIndex)] == RequestTelemetry.Metric;
        _origin.Enabled = _mismatches.Enabled = requests;
        foreach (var key in new[] { "OtelLocalModel", "OtelAssociation", "OtelMismatchCount", "OtelOrigin", "OtelMatchBasis" }) _grid.Columns[key]!.Visible = requests;
        _grid.Columns["SpeedModel"]!.HeaderText = UiText.Get(requests ? "OtelReportedModel" : "SpeedModel");
        _mean.SetContent(UiText.Get("OtelMean"), count > 0 ? $"{data.Buckets.Sum(b => b.Sum) / count:N2} ms" : "—", _metric.Text + " · " + scope);
        _count.SetContent(UiText.Get("OtelObservations"), count.ToString("N0"), UiText.Get(requests ? "OtelRequestTtft" : "OtelNotRequests"));
        _resolution.SetContent(UiText.Get("OtelResolution"), requests ? UiText.Get("OtelEventTime") : resolution == 0 ? UiText.Get("OtelExportInterval") : $"{resolution} s", UiText.Get(requests ? "OtelRequestTtft" : "OtelBoundaryNote"));
        _chart.SetLatencyData(data.Buckets, data.Models);
        _grid.DataSource = data.Buckets.Select(b => new Row(b.Start.LocalDateTime, b.End.LocalDateTime, b.Model, UiText.SpeedTierName(b.Tier), b.Count, b.Mean,
            b.ResolutionSeconds == 0 ? UiText.Get(requests ? "OtelEventTime" : "OtelExportInterval") : $"{b.ResolutionSeconds}s", UiText.Get("OtelSource_" + b.TierSource),
            b.LocalModel == "mixed" ? UiText.Get("OtelEvidence_mixed") : string.IsNullOrEmpty(b.LocalModel) ? "—" : b.LocalModel,
            UiText.Get("OtelEvidence_" + b.Origin), UiText.Get("OtelEvidence_" + b.Association), UiText.Get("OtelEvidence_" + b.MatchBasis), b.ModelMismatches)).ToArray(); _grid.ClearSelection();
        foreach (DataGridViewRow row in _grid.Rows)
            if (row.DataBoundItem is Row { ModelMismatches: > 0 }) row.Cells["OtelAssociation"].Style.ForeColor = Color.DarkOrange;
        _note.Text = UiText.Format("ChartInteractionHint", window.Start.LocalDateTime, window.End.LocalDateTime) + "\n" + UiText.Get(requests ? "OtelRequestNote" : "OtelMeasurementNote");
        _status.Text = UiText.Format("OtelStatus", _service.Listening ? $"127.0.0.1:{_service.Options.Port}" : UiText.Get("OtelNotListening"),
            _service.LastReceived?.LocalDateTime.ToString("MM-dd HH:mm:ss") ?? "—", _service.Accepted, _service.Failed,
            _service.Ignored, _service.Bytes / 1048576d, _service.Evicted, _service.Baselines, _service.Duplicates, _service.Discontinuities);
        _status.Text += "\n" + UiText.Format("SpeedTierScope", scope,
            data.Buckets.Where(b => b.Tier == "standard").Sum(b => b.Count),
            data.Buckets.Where(b => b.Tier == "fast").Sum(b => b.Count),
            data.Buckets.Where(b => b.Tier == "unknown").Sum(b => b.Count));
        if (requests) _status.Text += "\n" + UiText.Get("OtelMismatchCount") + ": " + data.Buckets.Sum(b => b.ModelMismatches).ToString("N0");
        if (count == 0 || data.Buckets.Any(b => b.Tier == "unknown")) _status.Text += "\n" + UiText.Get("SpeedUnknownNote");
        if (data.Truncated) _status.Text += "\n" + UiText.Get("OtelTruncated");
        if (count == 0) _status.Text += "\n" + UiText.Get("OtelWaiting");
        if (!string.IsNullOrEmpty(_service.Error)) _status.Text += "\n" + UiText.Get("OtelLastError") + " " + _service.Error.Split('\n')[0];
        _status.Text += "\n" + UiText.Get("OtelDiagnosticsHeading");
        if (data.Diagnostics.Count == 0) _status.Text += "\n" + UiText.Get("OtelDiagnosticsEmpty");
        foreach (var diagnostic in data.Diagnostics.OrderByDescending(d => d.RequestEvents).ThenBy(d => d.Model).Take(16))
        {
            _status.Text += "\n" + UiText.Format("OtelDiagnosticsLine", diagnostic.Model, diagnostic.Received,
                diagnostic.Stored, diagnostic.Baselines, diagnostic.Duplicates, diagnostic.Discontinuities,
                diagnostic.Rejected, diagnostic.Ignored, diagnostic.RequestEvents, diagnostic.Empty);
            if (diagnostic.Received == 0 && diagnostic.RequestEvents > 0 && !requests)
            {
                var serverTiming = Metrics[Math.Max(0, _metric.SelectedIndex)].Contains("responses_api_engine_", StringComparison.Ordinal);
                _status.Text += " · " + UiText.Get(!serverTiming ? "OtelDiagnosticsAbsent" :
                    diagnostic.WebSocketTimingPoints > 0 ? "OtelServerMetricNotExported" :
                    diagnostic.WebSocketEventPoints > 0 ? "OtelServerTimingNotReceived" : "OtelServerTransportUnconfirmed");
                if (serverTiming) _status.Text += " " + UiText.Format("OtelTransportPoints", diagnostic.WebSocketEventPoints, diagnostic.WebSocketTimingPoints);
            }
            if (diagnostic.Reason.Length > 0) _status.Text += "\n  " + diagnostic.Reason;
        }
        _tips.SetToolTip(_status, _service.Error);
    }

    /// <summary>异步读取当前时间窗全部指标的受限诊断报告，以可滚动文本展示新指标名和失败原因，不阻塞图表。</summary>
    private async Task ShowDiagnosticsAsync()
    {
        _diagnosticsButton.Enabled = false;
        var now = DateTimeOffset.UtcNow;
        var window = _chart.Navigation.Window ?? new ChartTimeWindow(now.AddHours(-4), now);
        try
        {
            var report = await Task.Run(() => _service.DiagnosticReport(window.Start, window.End));
            if (IsDisposed || Disposing) return;
            using var form = new Form { Text = UiText.Get("OtelDiagnosticsDetails"), StartPosition = FormStartPosition.CenterParent,
                AutoScaleMode = AutoScaleMode.Dpi, ClientSize = new Size(1100, 650), MinimumSize = new Size(600, 400) };
            var text = new TextBox { Name = "TelemetryDiagnosticsReport", Dock = DockStyle.Fill, ReadOnly = true,
                Multiline = true, WordWrap = false, ScrollBars = ScrollBars.Both,
                Text = UiText.Format("OtelDiagnosticsReportHeader", window.Start.LocalDateTime, window.End.LocalDateTime) +
                    Environment.NewLine + (report.Length == 0 ? UiText.Get("OtelDiagnosticsEmpty") : report) };
            form.Controls.Add(text); AppTheme.Apply(form); form.ShowDialog(FindForm());
        }
        catch (Exception e) when (e is SqliteException or IOException or InvalidDataException)
        {
            if (!IsDisposed && !Disposing)
            { _status.Text = UiText.Get("OtelReadFailed") + " " + e.Message; _tips.SetToolTip(_status, e.ToString()); }
        }
        finally { if (!IsDisposed && !Disposing) _diagnosticsButton.Enabled = true; }
    }

    /// <summary>释放页面自己的刷新计时器；接收端由托盘生命周期管理。</summary>
    protected override void Dispose(bool disposing) { if (disposing) { _timer.Dispose(); _tips.Dispose(); } base.Dispose(disposing); }
    private sealed record Row(DateTime Start, DateTime End, string Model, string Tier, long Count, double Mean, string Resolution, string TierSource,
        string LocalModel, string Origin, string Association, string MatchBasis, long ModelMismatches);
    private sealed record QueryRequest(string Metric, string? Model, string? Tier, DateTimeOffset Start, DateTimeOffset End, int Minutes, long Revision, string? Origin, bool MismatchesOnly);
}
