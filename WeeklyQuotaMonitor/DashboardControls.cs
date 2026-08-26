using System.Reflection;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor;

/// <summary>
/// 显示一个额度关键指标的标题、主值和补充说明，供总览页按 DPI 自动排列。
/// </summary>
public sealed class MetricCard : Panel
{
    private readonly Label _title = new();
    private readonly Label _value = new();
    private readonly Label _caption = new();

    /// <summary>
    /// 构造带统一内边距和可访问名称的三行指标卡片。
    /// </summary>
    public MetricCard()
    {
        BorderStyle = BorderStyle.FixedSingle;
        Padding = new Padding(16, 12, 16, 12);
        Margin = new Padding(6);
        MinimumSize = new Size(190, 118);
        Dock = DockStyle.Fill;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        _title.AutoSize = true;
        _value.AutoSize = true;
        _value.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 20F, FontStyle.Bold);
        _caption.AutoSize = true;
        layout.Controls.Add(_title, 0, 0);
        layout.Controls.Add(_value, 0, 1);
        layout.Controls.Add(_caption, 0, 2);
        Controls.Add(layout);
    }

    /// <summary>
    /// 更新指标卡片的可见内容和屏幕阅读器描述。
    /// </summary>
    /// <param name="title">简短指标名称。</param>
    /// <param name="value">突出显示的主值。</param>
    /// <param name="caption">补充说明。</param>
    public void SetContent(string title, string value, string caption)
    {
        _title.Text = title;
        _value.Text = value;
        _caption.Text = caption;
        AccessibleName = $"{title}: {value}. {caption}";
        _title.ForeColor = AppTheme.Current.MutedText;
        _value.ForeColor = AppTheme.Current.Accent;
        _caption.ForeColor = AppTheme.Current.MutedText;
        BackColor = AppTheme.Current.Surface;
    }
}

/// <summary>
/// 显示适合 GitHub Issue 的非敏感运行事实，并允许用户主动复制诊断摘要。
/// </summary>
public sealed class DiagnosticsPanel : UserControl
{
    private readonly Label _title = new();
    private readonly Label _description = new();
    private readonly TableLayoutPanel _facts = new();
    private readonly Button _copy = new();
    private readonly Label _copied = new();
    private MonitorViewSnapshot? _view;
    private AppSettings? _settings;

    /// <summary>
    /// 构造诊断说明、键值事实表和复制按钮。
    /// </summary>
    public DiagnosticsPanel()
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;
        Padding = new Padding(24);
        _title.AutoSize = true;
        _title.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 18F, FontStyle.Bold);
        _description.AutoSize = true;
        _description.MaximumSize = new Size(850, 0);
        _facts.AutoSize = true;
        _facts.Dock = DockStyle.Top;
        _facts.ColumnCount = 2;
        _facts.ColumnStyles.Add(new(SizeType.Absolute, 210));
        _facts.ColumnStyles.Add(new(SizeType.Percent, 100));
        _copy.AutoSize = true;
        _copy.Click += CopyClicked;
        _copied.AutoSize = true;

        var root = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.AutoSize));
        root.Controls.Add(_title);
        root.Controls.Add(_description);
        root.Controls.Add(_facts);
        root.Controls.Add(_copy);
        root.Controls.Add(_copied);
        Controls.Add(root);
        ApplyLocalization();
    }

    /// <summary>
    /// 更新诊断页使用的最新展示快照与设置路径。
    /// </summary>
    /// <param name="view">协调器展示快照。</param>
    /// <param name="settings">当前应用设置。</param>
    public void UpdateView(MonitorViewSnapshot view, AppSettings settings)
    {
        _view = view;
        _settings = settings;
        RebuildFacts();
    }

    /// <summary>
    /// 刷新诊断页中英文标题、说明、按钮和键名。
    /// </summary>
    public void ApplyLocalization()
    {
        _title.Text = UiText.Get("DiagnosticsTitle");
        _description.Text = UiText.Get("DiagnosticsDescription");
        _copy.Text = UiText.Get("DiagnosticsCopy");
        _copied.Text = string.Empty;
        RebuildFacts();
        AppTheme.Apply(this);
    }

    /// <summary>
    /// 按当前快照重建非敏感诊断键值表。
    /// </summary>
    private void RebuildFacts()
    {
        _facts.Controls.Clear();
        _facts.RowStyles.Clear();
        if (_view is null || _settings is null)
        {
            return;
        }

        AddFact("DiagnosticsVersion", Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0");
        AddFact("DiagnosticsConnection", _view.AppServerConnected ? UiText.Get("Connected") : UiText.Get("Disconnected"));
        AddFact("DiagnosticsSamples", $"{_view.SampleCount} / {_view.ArchivedSampleCount}");
        AddFact(
            "DiagnosticsReplay",
            $"accepted={_view.HistoricalReplayAcceptedCheckpoints}, rejected={_view.HistoricalReplayRejectedCheckpoints}, " +
            $"unattributed={_view.HistoricalReplayUnattributedIntervals}");
        AddFact("DiagnosticsDataFolder", AppPaths.DataDirectory);
        AddFact("DiagnosticsSessionFolder", _settings.SessionRoot);
        AddFact("DiagnosticsPricing", PublicApiPricing.PricingVersion);
    }

    /// <summary>
    /// 向诊断表添加一行区域化键名和可复制值。
    /// </summary>
    /// <param name="key">键名资源键。</param>
    /// <param name="value">非敏感事实值。</param>
    private void AddFact(string key, string value)
    {
        var row = _facts.RowCount++;
        _facts.RowStyles.Add(new(SizeType.AutoSize));
        var name = new Label { AutoSize = true, Text = UiText.Get(key), ForeColor = AppTheme.Current.MutedText };
        var content = new TextBox { ReadOnly = true, BorderStyle = BorderStyle.None, Text = value, Dock = DockStyle.Fill };
        _facts.Controls.Add(name, 0, row);
        _facts.Controls.Add(content, 1, row);
    }

    /// <summary>
    /// 将当前诊断事实复制到剪贴板，并显示区域化成功提示。
    /// </summary>
    private void CopyClicked(object? sender, EventArgs e)
    {
        var lines = new List<string>();
        for (var row = 0; row < _facts.RowCount; row++)
        {
            var name = _facts.GetControlFromPosition(0, row)?.Text;
            var value = _facts.GetControlFromPosition(1, row)?.Text;
            lines.Add($"{name}: {value}");
        }

        Clipboard.SetText(string.Join(Environment.NewLine, lines));
        _copied.Text = UiText.Get("DiagnosticsCopied");
        _copied.ForeColor = AppTheme.Current.Positive;
    }
}
