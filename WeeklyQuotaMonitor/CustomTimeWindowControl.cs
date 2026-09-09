using System.ComponentModel;

namespace WeeklyQuotaMonitor;

/// <summary>以本地时间编辑固定起止窗口；只有校验通过并应用后才影响统计，避免编辑过程反复补扫日志。</summary>
public sealed class CustomTimeWindowControl : FlowLayoutPanel
{
    private readonly Label _from = new() { AutoSize = true, Margin = new Padding(0, 7, 4, 0) };
    private readonly Label _to = new() { AutoSize = true, Margin = new Padding(8, 7, 4, 0) };
    private readonly DateTimePicker _start = new() { Name = "WindowStart", Width = 180, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss" };
    private readonly DateTimePicker _end = new() { Name = "WindowEnd", Width = 180, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss" };
    private readonly Button _apply = new() { Name = "ApplyTimeWindow", AutoSize = true, FlatStyle = FlatStyle.Flat };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.IndianRed };
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DateTimeOffset Start { get; private set; } = DateTimeOffset.Now.AddDays(-1);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DateTimeOffset End { get; private set; } = DateTimeOffset.Now;
    public event Action? WindowApplied;

    /// <summary>创建可换行的日期编辑区，默认最近一天但不自动提交用户尚未完成的输入。</summary>
    public CustomTimeWindowControl()
    {
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Top; WrapContents = true; Margin = new Padding(0);
        _start.Value = Start.LocalDateTime; _end.Value = End.LocalDateTime;
        Controls.AddRange([_from, _start, _to, _end, _apply, _error]);
        _apply.Click += (_, _) => ApplyWindow();
        ApplyLanguage();
    }

    /// <summary>更新标签，不重置尚未提交的本地时间输入。</summary>
    public void ApplyLanguage()
    {
        _from.Text = UiText.Get("WindowFrom"); _to.Text = UiText.Get("WindowTo");
        _apply.Text = UiText.Get("WindowApply");
        if (_error.Text.Length > 0) _error.Text = UiText.Get("WindowInvalid");
    }

    /// <summary>拒绝逆序、相等及夏令时不确定时间；失败保留已生效窗口并在原位显示原因。</summary>
    public bool ApplyWindow()
    {
        var start = DateTime.SpecifyKind(_start.Value, DateTimeKind.Unspecified);
        var end = DateTime.SpecifyKind(_end.Value, DateTimeKind.Unspecified);
        if (start >= end || TimeZoneInfo.Local.IsInvalidTime(start) || TimeZoneInfo.Local.IsInvalidTime(end) ||
            TimeZoneInfo.Local.IsAmbiguousTime(start) || TimeZoneInfo.Local.IsAmbiguousTime(end))
        { _error.Text = UiText.Get("WindowInvalid"); return false; }
        Start = new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start));
        End = new DateTimeOffset(end, TimeZoneInfo.Local.GetUtcOffset(end));
        _error.Text = string.Empty; WindowApplied?.Invoke(); return true;
    }
}
