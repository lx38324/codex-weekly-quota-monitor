using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor;

/// <summary>
/// 单击托盘图标时显示额度、样本、重置时间和数据质量的紧凑详情。
/// </summary>
public sealed class DetailForm : Form
{
    private readonly Label _estimate = new();
    private readonly Label _usage = new();
    private readonly Label _samples = new();
    private readonly Label _reset = new();
    private readonly Label _quality = new();
    private readonly Label _integrity = new();
    private readonly Label _status = new();

    /// <summary>
    /// 构造可随屏幕 DPI 缩放、置顶且不进入任务栏的详情工具窗。
    /// </summary>
    public DetailForm()
    {
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(500, 320);
        Size = new Size(600, 390);
        Font = SystemFonts.MessageBoxFont!;
        AutoScroll = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 7
        };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));

        _estimate.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12.5F, FontStyle.Bold);
        _estimate.MinimumSize = new Size(0, 64);
        foreach (var label in new[] { _estimate, _usage, _samples, _reset, _quality, _integrity, _status })
        {
            label.Dock = DockStyle.Fill;
            label.AutoSize = true;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.AutoEllipsis = false;
            label.Margin = new Padding(3, 2, 3, 2);
        }

        foreach (var label in new[] { _usage, _samples, _reset, _quality, _integrity })
        {
            label.MinimumSize = new Size(0, 28);
        }

        _samples.MinimumSize = new Size(0, 46);

        layout.Controls.Add(_estimate);
        layout.Controls.Add(_usage);
        layout.Controls.Add(_samples);
        layout.Controls.Add(_reset);
        layout.Controls.Add(_quality);
        layout.Controls.Add(_integrity);
        layout.Controls.Add(_status);
        Controls.Add(layout);

        ApplyAppearance();
        Deactivate += (_, _) => Hide();
        FormClosing += HideOnUserClose;
    }

    /// <summary>
    /// 用最新展示快照更新详情文字，不重新查询额度或读取日志。
    /// </summary>
    /// <param name="view">协调器发布的只读展示快照。</param>
    /// <param name="showOfficialLongContext">是否显示用户显式启用的官方 >272K 对比口径。</param>
    public void UpdateView(MonitorViewSnapshot view, bool showOfficialLongContext)
    {
        _estimate.Text = view.EstimatedWeeklyQuotaUsd is decimal baseEstimate
            ? showOfficialLongContext &&
              view.OfficialLongContextEstimatedWeeklyQuotaUsd is decimal longContextEstimate
                ? UiText.Format("DetailsEstimates", baseEstimate, longContextEstimate)
                : UiText.Format("DetailsEstimateBase", baseEstimate)
            : UiText.Get("DetailsWaiting");
        _usage.Text = view.RateLimit is null
            ? UiText.Format(
                "DetailsNoRateLimit",
                view.AppServerConnected ? UiText.Get("Connected") : UiText.Get("Disconnected"))
            : UiText.Format(
                "DetailsUsage",
                view.RateLimit.UsedPercent,
                100m - view.RateLimit.UsedPercent);
        var replayedSamples = view.Samples.Count(sample =>
            string.Equals(
                sample.SampleSource,
                HistoricalReplayCalculator.HistoricalSampleSource,
                StringComparison.Ordinal));
        var latestValidPercent = view.Samples.OrderBy(sample => sample.Timestamp).LastOrDefault()?.UsedPercent;
        var latestValidText = latestValidPercent is decimal percent
            ? $"{percent.ToString("N2", UiText.Culture)}%"
            : UiText.Get("None");
        var pendingPercents = view.HistoricalReplayUnattributedUsedPercents.Count == 0
            ? UiText.Get("None")
            : string.Join(", ", view.HistoricalReplayUnattributedUsedPercents.Select(percent =>
                $"{percent.ToString("N2", UiText.Culture)}%"));
        _samples.Text = UiText.Format(
            "DetailsSamples",
            view.SampleCount,
            replayedSamples,
            latestValidText,
            pendingPercents,
            view.ArchivedSampleCount);
        _reset.Text = view.RateLimit is null
            ? UiText.Get("DetailsResetUnknown")
            : UiText.Format("DetailsReset", view.RateLimit.ResetsAt.LocalDateTime);
        _quality.Text = UiText.Format(
            "DetailsQuality",
            view.UnattributedPercentChanges,
            view.UnpricedModelResponses,
            view.HistoricalReplayAcceptedCheckpoints,
            view.HistoricalReplayRejectedCheckpoints,
            view.HistoricalReplayUnpricedResponses,
            view.HistoricalReplayUnattributedIntervals,
            view.HistoricalReplayAwaitingLogIntervals,
            view.HistoricalReplayMalformedLines);
        _integrity.Text = UiText.Format(
            "DetailsIntegrity",
            view.MalformedRolloutLines,
            view.MalformedAppServerMessages,
            view.RotatedRolloutFiles,
            view.PrunedRolloutCursors);
        _status.Text = UiText.Format(
            "QuickDetailsStatus",
            view.AppServerConnected ? UiText.Get("Connected") : UiText.Get("Disconnected"));
        ApplyAppearance();
    }

    /// <summary>
    /// 按当前语言和主题刷新工具窗标题、强调色、次要文字和背景。
    /// </summary>
    public void ApplyAppearance()
    {
        Text = UiText.Get("ProductName");
        AppTheme.Apply(this);
        _estimate.ForeColor = AppTheme.Current.Accent;
        _status.ForeColor = AppTheme.Current.MutedText;
    }

    /// <summary>
    /// 在鼠标附近显示窗口，并确保窗口完全落在当前屏幕工作区内。
    /// </summary>
    public void ShowNearCursor()
    {
        var cursor = Cursor.Position;
        var workingArea = Screen.FromPoint(cursor).WorkingArea;
        var x = Math.Min(cursor.X - Width / 2, workingArea.Right - Width);
        var y = Math.Min(cursor.Y - Height - 12, workingArea.Bottom - Height);
        Location = new(Math.Max(workingArea.Left, x), Math.Max(workingArea.Top, y));
        Show();
        Activate();
    }

    /// <summary>
    /// 用户关闭详情窗时仅隐藏窗口，保留托盘上下文持有的实例供后续单击再次显示。
    /// </summary>
    private void HideOnUserClose(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
