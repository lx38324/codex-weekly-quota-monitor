using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor;

/// <summary>
/// 以分类标签页编辑常规、数据源、采样、估算、模型价格、外观和高级参数，并支持即时中英文切换。
/// </summary>
public sealed class SettingsPanel : UserControl
{
    private readonly TextBox _codexExecutable = new();
    private readonly TextBox _codexArguments = new();
    private readonly TextBox _sessionRoot = new();
    private readonly NumericUpDown _pollSeconds = Number(10, 86400, 60, 0);
    private readonly TextBox _limitId = new();
    private readonly NumericUpDown _minimumWindowMinutes = Number(1, 100000, 1440, 0);
    private readonly NumericUpDown _minimumDelta = Number(0.001m, 100, 0.1m, 3);
    private readonly NumericUpDown _lookbackHours = Number(1, 720, 24, 0);
    private readonly CheckBox _startWithWindows = new();
    private readonly NumericUpDown _historyDays = Number(1, 3650, 90, 0);
    private readonly CheckBox _enableOfficialLongContextEstimate = new();
    private readonly ComboBox _regressionMode = ChoiceBox();
    private readonly NumericUpDown _linearPoints = Number(1, 10000, 120, 0);
    private readonly NumericUpDown _segmentHours = Number(0.1m, 8760, 24, 1);
    private readonly NumericUpDown _gaussianHours = Number(0.1m, 8760, 12, 1);
    private readonly NumericUpDown _maximumSampleUsd = Number(
        0.01m,
        1000000,
        AppSettingsMigration.DefaultMaximumSampleUsd,
        2);
    private readonly PricingEditorPanel _pricingEditor = new();
    private readonly ComboBox _language = ChoiceBox();
    private readonly ComboBox _theme = ChoiceBox();
    private readonly Label _note = new() { AutoSize = true, MaximumSize = new Size(780, 0) };
    private readonly Label _saveStatus = new() { AutoSize = true };
    private readonly TabControl _sections = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<Control, string> _localizedControls = [];
    private readonly List<Button> _browseButtons = [];
    private readonly Button _save = new() { AutoSize = true };
    private readonly Button _revert = new() { AutoSize = true };
    private readonly Button _defaults = new() { AutoSize = true };
    private AppSettings _activeSettings;

    public event Action<AppSettings>? SettingsSaved;

    /// <summary>
    /// 构造分类设置界面并载入当前配置。
    /// </summary>
    /// <param name="settings">当前已生效设置。</param>
    public SettingsPanel(AppSettings settings)
    {
        _activeSettings = settings;
        Dock = DockStyle.Fill;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildSections();
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(8) };
        root.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.AutoSize));
        root.Controls.Add(_sections, 0, 0);
        root.Controls.Add(BuildActionBar(), 0, 1);
        Controls.Add(root);

        LoadSettings(settings);
        ApplyLocalization();
        AppTheme.Apply(this);
    }

    /// <summary>
    /// 重新载入当前设置到全部控件，供标签切换、撤销或外部更新时使用。
    /// </summary>
    /// <param name="settings">需要显示的设置。</param>
    public void LoadSettings(AppSettings settings)
    {
        _activeSettings = settings;
        _codexExecutable.Text = settings.CodexExecutable;
        _codexArguments.Text = settings.CodexArguments;
        _sessionRoot.Text = settings.SessionRoot;
        _pollSeconds.Value = settings.PollIntervalSeconds;
        _limitId.Text = settings.PreferredLimitId;
        _minimumWindowMinutes.Value = settings.MinimumWindowMinutes;
        _minimumDelta.Value = settings.MinimumPercentDelta;
        _lookbackHours.Value = settings.InitialContextLookbackHours;
        _startWithWindows.Checked = settings.StartWithWindows;
        _historyDays.Value = settings.ChartHistoryDays;
        _enableOfficialLongContextEstimate.Checked = settings.EnableOfficialLongContextEstimate;
        _linearPoints.Value = settings.Regression.LinearLookbackPoints;
        _segmentHours.Value = (decimal)settings.Regression.SegmentWindowHours;
        _gaussianHours.Value = (decimal)settings.Regression.GaussianBandwidthHours;
        _maximumSampleUsd.Value = settings.Regression.MaximumSampleUsd;
        _pricingEditor.LoadProfiles(settings.ModelPrices);
        BindChoices(settings.Regression.Mode, settings.Language, settings.Theme);
        _saveStatus.Text = string.Empty;
    }

    /// <summary>
    /// 刷新标签页、字段、按钮和下拉项的中英文文本，同时保留当前未保存输入。
    /// </summary>
    public void ApplyLocalization()
    {
        foreach (var pair in _localizedControls)
        {
            pair.Key.Text = UiText.Get(pair.Value);
        }

        foreach (var button in _browseButtons)
        {
            button.Text = UiText.Get("Browse");
        }

        _save.Text = UiText.Get("SaveApply");
        _revert.Text = UiText.Get("Revert");
        _defaults.Text = UiText.Get("RestoreDefaults");
        _startWithWindows.Text = UiText.Get("SettingsAutostart");
        _enableOfficialLongContextEstimate.Text = UiText.Get("SettingsOfficialLongContextOption");
        _pricingEditor.ApplyLocalization();
        _note.Text = UiText.Format(
            "SettingsNote",
            CodexExecutableResolver.DesktopPackageLocator,
            PublicApiPricing.PriceObservedDate,
            PublicApiPricing.AutoReviewPricingAssumption);
        BindChoices(
            SelectedRegressionMode(),
            SelectedLanguage(),
            SelectedTheme());
        AppTheme.Apply(this);
    }

    /// <summary>
    /// 创建常规、数据源、采样、估算、模型价格、外观和高级设置分类。
    /// </summary>
    private void BuildSections()
    {
        AddSection("SettingsGeneral", [
            ("SettingsAutostart", (Control)_startWithWindows),
            ("SettingsHistoryDays", _historyDays)]);
        AddSection("SettingsDataSource", [
            ("SettingsCodexExecutable", WithBrowse(_codexExecutable, BrowseExecutable)),
            ("SettingsAppServerArgs", _codexArguments),
            ("SettingsSessionRoot", WithBrowse(_sessionRoot, BrowseSessionRoot))]);
        AddSection("SettingsSampling", [
            ("SettingsPollSeconds", _pollSeconds),
            ("SettingsLimitId", _limitId),
            ("SettingsMinimumWindow", _minimumWindowMinutes),
            ("SettingsMinimumDelta", _minimumDelta),
            ("SettingsLookback", _lookbackHours)]);
        AddSection("SettingsEstimation", [
            ("SettingsOfficialLongContext", (Control)_enableOfficialLongContextEstimate),
            ("SettingsRegressionMode", _regressionMode),
            ("SettingsLinearPoints", _linearPoints),
            ("SettingsSegmentHours", _segmentHours),
            ("SettingsGaussianHours", _gaussianHours),
            ("SettingsMaximumSample", _maximumSampleUsd)]);
        var pricing = new TabPage();
        _localizedControls[pricing] = "SettingsPricing";
        pricing.Controls.Add(_pricingEditor);
        _sections.TabPages.Add(pricing);
        AddSection("SettingsAppearance", [
            ("SettingsLanguage", _language),
            ("SettingsTheme", _theme)]);

        var advanced = new TabPage();
        _localizedControls[advanced] = "SettingsAdvanced";
        var advancedPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(18) };
        _note.Dock = DockStyle.Top;
        advancedPanel.Controls.Add(_note);
        advanced.Controls.Add(advancedPanel);
        _sections.TabPages.Add(advanced);
    }

    /// <summary>
    /// 创建一个带区域标题的设置标签页，并添加两列表单字段。
    /// </summary>
    /// <param name="sectionKey">标签页资源键。</param>
    /// <param name="rows">字段资源键与输入控件。</param>
    private void AddSection(string sectionKey, IReadOnlyList<(string Key, Control Control)> rows)
    {
        var page = new TabPage();
        _localizedControls[page] = sectionKey;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(18),
            ColumnCount = 2,
            RowCount = rows.Count
        };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 250));
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (var index = 0; index < rows.Count; index++)
        {
            AddRow(layout, index, rows[index].Key, rows[index].Control);
        }

        page.Controls.Add(layout);
        _sections.TabPages.Add(page);
    }

    /// <summary>
    /// 创建底部保存、撤销、恢复默认和状态区域。
    /// </summary>
    /// <returns>可停靠在设置页底部的操作栏。</returns>
    private Control BuildActionBar()
    {
        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        bar.ColumnStyles.Add(new(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new(SizeType.AutoSize));
        _saveStatus.ForeColor = AppTheme.Current.Positive;
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        _save.Click += SaveClicked;
        _revert.Click += ReloadClicked;
        _defaults.Click += RestoreDefaultsClicked;
        buttons.Controls.AddRange([_save, _revert, _defaults]);
        bar.Controls.Add(_saveStatus, 0, 0);
        bar.Controls.Add(buttons, 1, 0);
        return bar;
    }

    /// <summary>
    /// 从控件构造设置、执行业务校验并通知托盘上下文即时应用。
    /// </summary>
    private void SaveClicked(object? sender, EventArgs e)
    {
        var settings = new AppSettings
        {
            SettingsSchemaVersion = AppSettingsMigration.CurrentVersion,
            CodexExecutable = _codexExecutable.Text.Trim(),
            CodexArguments = _codexArguments.Text.Trim(),
            SessionRoot = _sessionRoot.Text.Trim(),
            PollIntervalSeconds = decimal.ToInt32(_pollSeconds.Value),
            PreferredLimitId = _limitId.Text.Trim(),
            MinimumWindowMinutes = decimal.ToInt32(_minimumWindowMinutes.Value),
            MinimumPercentDelta = _minimumDelta.Value,
            InitialContextLookbackHours = decimal.ToInt32(_lookbackHours.Value),
            StartWithWindows = _startWithWindows.Checked,
            ChartHistoryDays = decimal.ToInt32(_historyDays.Value),
            EnableOfficialLongContextEstimate = _enableOfficialLongContextEstimate.Checked,
            Language = SelectedLanguage(),
            Theme = SelectedTheme(),
            ModelPrices = _pricingEditor.GetProfiles().ToList(),
            Regression = new RegressionOptions
            {
                Mode = SelectedRegressionMode(),
                LinearLookbackPoints = decimal.ToInt32(_linearPoints.Value),
                SegmentWindowHours = (double)_segmentHours.Value,
                GaussianBandwidthHours = (double)_gaussianHours.Value,
                MaximumSampleUsd = _maximumSampleUsd.Value
            }
        };

        var errors = settings.Validate();
        if (errors.Count > 0)
        {
            MessageBox.Show(
                this,
                string.Join(Environment.NewLine, errors),
                UiText.Get("InvalidSettings"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        SettingsSaved?.Invoke(settings);
        _activeSettings = settings;
        _saveStatus.Text = UiText.Format("SavedAt", DateTime.Now);
    }

    /// <summary>
    /// 放弃未保存输入并恢复当前已生效设置。
    /// </summary>
    private void ReloadClicked(object? sender, EventArgs e) => LoadSettings(_activeSettings);

    /// <summary>
    /// 把控件恢复为公开发布版默认设置，但等待用户点击保存后才应用。
    /// </summary>
    private void RestoreDefaultsClicked(object? sender, EventArgs e)
    {
        var active = _activeSettings;
        LoadSettings(new AppSettings());
        _activeSettings = active;
    }

    /// <summary>
    /// 打开可执行文件选择器并写入用户选择的 Codex 路径。
    /// </summary>
    private void BrowseExecutable()
    {
        using var dialog = new OpenFileDialog
        {
            Title = UiText.Get("SelectExecutable"),
            Filter = UiText.Get("ExecutableFilter"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _codexExecutable.Text = dialog.FileName;
        }
    }

    /// <summary>
    /// 打开目录选择器并写入用户选择的 Codex sessions 根目录。
    /// </summary>
    private void BrowseSessionRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = UiText.Get("SelectSessionRoot"),
            SelectedPath = _sessionRoot.Text,
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _sessionRoot.Text = dialog.SelectedPath;
        }
    }

    /// <summary>
    /// 创建文本框和区域化浏览按钮组成的横向输入控件。
    /// </summary>
    /// <param name="textBox">承载路径的文本框。</param>
    /// <param name="browse">点击浏览按钮时执行的选择动作。</param>
    /// <returns>两列组合控件。</returns>
    private Control WithBrowse(TextBox textBox, Action browse)
    {
        textBox.Dock = DockStyle.Fill;
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        panel.ColumnStyles.Add(new(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new(SizeType.Absolute, 92));
        var button = new Button { Dock = DockStyle.Fill };
        button.Click += (_, _) => browse();
        _browseButtons.Add(button);
        panel.Controls.Add(textBox, 0, 0);
        panel.Controls.Add(button, 1, 0);
        return panel;
    }

    /// <summary>
    /// 把区域化标签和输入控件加入两列表格中的指定行。
    /// </summary>
    /// <param name="layout">目标表格。</param>
    /// <param name="row">目标行号。</param>
    /// <param name="labelKey">字段标题资源键。</param>
    /// <param name="control">字段输入控件。</param>
    private void AddRow(TableLayoutPanel layout, int row, string labelKey, Control control)
    {
        layout.RowStyles.Add(new(SizeType.Absolute, 44));
        var label = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
        _localizedControls[label] = labelKey;
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(3, 6, 3, 6);
        layout.Controls.Add(label, 0, row);
        layout.Controls.Add(control, 1, row);
    }

    /// <summary>
    /// 重新绑定回归、语言和主题下拉项，同时保留选中的枚举值。
    /// </summary>
    /// <param name="regression">当前回归模式。</param>
    /// <param name="language">当前语言设置。</param>
    /// <param name="theme">当前主题设置。</param>
    private void BindChoices(RegressionMode regression, UiLanguage language, UiTheme theme)
    {
        _regressionMode.DataSource = new[]
        {
            new Choice<RegressionMode>(RegressionMode.Linear, UiText.Get("RegressionLinear")),
            new Choice<RegressionMode>(RegressionMode.TimeWindowSegmented, UiText.Get("RegressionSegmented")),
            new Choice<RegressionMode>(RegressionMode.GaussianAggregation, UiText.Get("RegressionGaussian"))
        };
        _language.DataSource = new[]
        {
            new Choice<UiLanguage>(UiLanguage.Auto, UiText.Get("LanguageAuto")),
            new Choice<UiLanguage>(UiLanguage.ChineseSimplified, UiText.Get("LanguageChinese")),
            new Choice<UiLanguage>(UiLanguage.English, UiText.Get("LanguageEnglish"))
        };
        _theme.DataSource = new[]
        {
            new Choice<UiTheme>(UiTheme.System, UiText.Get("ThemeSystem")),
            new Choice<UiTheme>(UiTheme.Light, UiText.Get("ThemeLight")),
            new Choice<UiTheme>(UiTheme.Dark, UiText.Get("ThemeDark"))
        };
        _regressionMode.SelectedValue = regression;
        _language.SelectedValue = language;
        _theme.SelectedValue = theme;
    }

    /// <summary>
    /// 返回当前回归模式下拉项的枚举值。
    /// </summary>
    private RegressionMode SelectedRegressionMode() =>
        _regressionMode.SelectedValue is RegressionMode mode ? mode : _activeSettings.Regression.Mode;

    /// <summary>
    /// 返回当前语言下拉项的枚举值。
    /// </summary>
    private UiLanguage SelectedLanguage() =>
        _language.SelectedValue is UiLanguage language ? language : _activeSettings.Language;

    /// <summary>
    /// 返回当前主题下拉项的枚举值。
    /// </summary>
    private UiTheme SelectedTheme() =>
        _theme.SelectedValue is UiTheme theme ? theme : _activeSettings.Theme;

    /// <summary>
    /// 创建范围、小数位和默认值一致的 NumericUpDown。
    /// </summary>
    private static NumericUpDown Number(decimal minimum, decimal maximum, decimal value, int decimalPlaces) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Value = value,
        DecimalPlaces = decimalPlaces,
        Increment = decimalPlaces == 0 ? 1 : 0.1m,
        ThousandsSeparator = true
    };

    /// <summary>
    /// 创建只允许选择已有项的通用下拉框。
    /// </summary>
    private static ComboBox ChoiceBox() => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        DisplayMember = "Name",
        ValueMember = "Value"
    };

    /// <summary>
    /// 保存下拉项的枚举值和区域化显示名称。
    /// </summary>
    private sealed record Choice<T>(T Value, string Name) where T : struct, Enum;
}
