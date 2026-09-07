using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor;

/// <summary>
/// 以模型选择器和短/长上下文价格表单编辑程序支持的全部模型价格，并提供独立的内置价格复位入口。
/// </summary>
public sealed class PricingEditorPanel : UserControl
{
    private readonly ComboBox _model = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Dock = DockStyle.Fill,
        Name = "PricingModelSelector"
    };
    private readonly NumericUpDown _shortInput = PriceNumber();
    private readonly NumericUpDown _shortCached = PriceNumber();
    private readonly NumericUpDown _shortCacheWrite = PriceNumber();
    private readonly NumericUpDown _shortOutput = PriceNumber();
    private readonly CheckBox _shortCacheWriteEnabled = new() { AutoSize = true };
    private readonly CheckBox _longEnabled = new() { AutoSize = true };
    private readonly NumericUpDown _longThreshold = Number(1, 10_000_000, 272_000, 0);
    private readonly NumericUpDown _longInput = PriceNumber();
    private readonly NumericUpDown _longCached = PriceNumber();
    private readonly NumericUpDown _longCacheWrite = PriceNumber();
    private readonly NumericUpDown _longOutput = PriceNumber();
    private readonly CheckBox _longCacheWriteEnabled = new() { AutoSize = true };
    private readonly GroupBox _shortGroup = new() { AutoSize = true, Dock = DockStyle.Top };
    private readonly GroupBox _longGroup = new() { AutoSize = true, Dock = DockStyle.Top };
    private readonly Label _description = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Label _status = new() { AutoSize = true };
    private readonly Button _restoreBuiltIn = new() { AutoSize = true, Name = "RestoreBuiltInPricesButton" };
    private readonly Dictionary<Control, string> _localizedControls = [];
    private readonly List<Control> _longValueControls = [];
    private Dictionary<string, ModelPriceProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private string _editingModel = string.Empty;
    private bool _loading;

    /// <summary>
    /// 构建可滚动价格编辑器并绑定模型切换、长上下文开关和价格复位行为。
    /// </summary>
    public PricingEditorPanel()
    {
        Dock = DockStyle.Fill;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(18)
        };
        content.ColumnStyles.Add(new(SizeType.Percent, 100));
        content.Controls.Add(_description);
        content.Controls.Add(BuildLabeledRow("PricingModel", _model));
        content.Controls.Add(BuildActionRow());
        content.Controls.Add(BuildShortContextGroup());
        content.Controls.Add(BuildLongContextGroup());

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(content);
        Controls.Add(scroll);

        _model.SelectedIndexChanged += ModelSelectionChanged;
        _shortCacheWriteEnabled.CheckedChanged += (_, _) =>
            _shortCacheWrite.Enabled = _shortCacheWriteEnabled.Checked;
        _longEnabled.CheckedChanged += (_, _) => UpdateLongContextEnabledState();
        _longCacheWriteEnabled.CheckedChanged += (_, _) => UpdateLongContextEnabledState();
        _restoreBuiltIn.Click += (_, _) => RestoreBuiltInPrices();
        ApplyLocalization();
    }

    /// <summary>
    /// 用已生效设置替换编辑副本，并选中首个模型；调用不会修改传入集合。
    /// </summary>
    /// <param name="profiles">需要显示的完整模型价格集合。</param>
    public void LoadProfiles(IReadOnlyCollection<ModelPriceProfile> profiles)
    {
        _loading = true;
        _profiles = profiles.ToDictionary(
            profile => profile.Model,
            profile => profile,
            StringComparer.OrdinalIgnoreCase);
        var models = _profiles.Keys.OrderBy(model => model, StringComparer.OrdinalIgnoreCase).ToArray();
        _model.DataSource = models;
        _editingModel = models.FirstOrDefault(model =>
                            string.Equals(model, "gpt-6-astra", StringComparison.OrdinalIgnoreCase))
                        ?? models.FirstOrDefault()
                        ?? string.Empty;
        if (_editingModel.Length > 0)
        {
            _model.SelectedItem = _editingModel;
            PopulateEditor(_profiles[_editingModel]);
        }

        _status.Text = string.Empty;
        _loading = false;
    }

    /// <summary>
    /// 提交当前模型控件值并导出按模型标识排序的价格集合，供设置保存和业务校验使用。
    /// </summary>
    /// <returns>包含当前全部未保存编辑值的模型价格列表。</returns>
    public IReadOnlyList<ModelPriceProfile> GetProfiles()
    {
        CommitCurrentProfile();
        return _profiles.Values.OrderBy(profile => profile.Model, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// 仅把价格编辑副本恢复为当前程序内置值，其他设置不变且等待用户保存后才生效。
    /// </summary>
    public void RestoreBuiltInPrices()
    {
        var selectedModel = _editingModel;
        LoadProfiles(PublicApiPricing.GetBuiltInProfiles());
        if (_profiles.ContainsKey(selectedModel))
        {
            _model.SelectedItem = selectedModel;
        }

        _status.Text = UiText.Get("PricingRestoredPending");
    }

    /// <summary>
    /// 刷新价格页说明、字段、分组和按钮的区域化文本，不触碰当前编辑值。
    /// </summary>
    public void ApplyLocalization()
    {
        foreach (var pair in _localizedControls)
        {
            pair.Key.Text = UiText.Get(pair.Value);
        }

        _shortGroup.Text = UiText.Get("PricingShortContext");
        _longGroup.Text = UiText.Get("PricingLongContext");
        _shortCacheWriteEnabled.Text = UiText.Get("PricingCacheWriteEnabled");
        _longEnabled.Text = UiText.Get("PricingLongEnabled");
        _longCacheWriteEnabled.Text = UiText.Get("PricingCacheWriteEnabled");
        _restoreBuiltIn.Text = UiText.Get("PricingRestoreBuiltIn");
        _description.Text = UiText.Get("PricingDescription");
        AppTheme.Apply(this);
    }

    /// <summary>
    /// 构建短上下文输入、缓存读取、可选缓存写入和输出价格表单。
    /// </summary>
    /// <returns>自动适应 DPI 和宽度的短上下文分组。</returns>
    private Control BuildShortContextGroup()
    {
        var layout = CreateFieldLayout();
        AddField(layout, "PricingInput", _shortInput);
        AddField(layout, "PricingCachedInput", _shortCached);
        AddField(layout, "PricingCacheWrite", WithCheckBox(_shortCacheWriteEnabled, _shortCacheWrite));
        AddField(layout, "PricingOutput", _shortOutput);
        _shortGroup.Controls.Add(layout);
        return _shortGroup;
    }

    /// <summary>
    /// 构建可关闭的长上下文阈值与四类价格表单；关闭时模型明确不支持长上下文价格。
    /// </summary>
    /// <returns>自动适应 DPI 和宽度的长上下文分组。</returns>
    private Control BuildLongContextGroup()
    {
        var layout = CreateFieldLayout();
        AddField(layout, "PricingLongPricing", _longEnabled);
        AddField(layout, "PricingThreshold", _longThreshold);
        AddField(layout, "PricingInput", _longInput);
        AddField(layout, "PricingCachedInput", _longCached);
        AddField(layout, "PricingCacheWrite", WithCheckBox(_longCacheWriteEnabled, _longCacheWrite));
        AddField(layout, "PricingOutput", _longOutput);
        _longValueControls.AddRange([
            _longThreshold,
            _longInput,
            _longCached,
            _longCacheWrite,
            _longCacheWriteEnabled,
            _longOutput]);
        _longGroup.Controls.Add(layout);
        return _longGroup;
    }

    /// <summary>
    /// 创建位于价格页首屏的价格复位按钮和未保存状态提示，不复用全局“恢复默认”以免影响其他设置。
    /// </summary>
    /// <returns>右对齐按钮与左侧状态组成的操作行。</returns>
    private Control BuildActionRow()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        row.ColumnStyles.Add(new(SizeType.Percent, 100));
        row.ColumnStyles.Add(new(SizeType.AutoSize));
        _status.ForeColor = AppTheme.Current.Positive;
        row.Controls.Add(_status, 0, 0);
        row.Controls.Add(_restoreBuiltIn, 1, 0);
        return row;
    }

    /// <summary>
    /// 在模型选择变化前保存旧模型，再把新模型的完整价格载入控件。
    /// </summary>
    private void ModelSelectionChanged(object? sender, EventArgs e)
    {
        if (_loading || _model.SelectedItem is not string selectedModel)
        {
            return;
        }

        CommitCurrentProfile();
        _editingModel = selectedModel;
        PopulateEditor(_profiles[selectedModel]);
    }

    /// <summary>
    /// 把当前控件值写回正在编辑的模型；禁用长上下文时明确保存为不支持该档位。
    /// </summary>
    private void CommitCurrentProfile()
    {
        if (_loading || string.IsNullOrWhiteSpace(_editingModel) || !_profiles.ContainsKey(_editingModel))
        {
            return;
        }

        var existingProfile = _profiles[_editingModel];
        var shortContext = new PriceBand(
            _shortInput.Value,
            _shortCached.Value,
            _shortCacheWriteEnabled.Checked ? _shortCacheWrite.Value : null,
            _shortOutput.Value);
        PriceBand? longContext = _longEnabled.Checked
            ? new(
                _longInput.Value,
                _longCached.Value,
                _longCacheWriteEnabled.Checked ? _longCacheWrite.Value : null,
                _longOutput.Value)
            : null;
        _profiles[_editingModel] = new(
            _editingModel,
            shortContext,
            longContext,
            longContext is null
                ? existingProfile.LongContextThresholdTokens
                : decimal.ToInt64(_longThreshold.Value));
    }

    /// <summary>
    /// 把一个模型价格填入全部数值和可选项，并为尚无长上下文价的模型准备可编辑初值。
    /// </summary>
    /// <param name="profile">当前选中模型的价格配置。</param>
    private void PopulateEditor(ModelPriceProfile profile)
    {
        _loading = true;
        SetBandValues(
            profile.ShortContext,
            _shortInput,
            _shortCached,
            _shortCacheWrite,
            _shortOutput,
            _shortCacheWriteEnabled);
        _longEnabled.Checked = profile.LongContext is not null;
        _longThreshold.Value = profile.LongContextThresholdTokens == long.MaxValue
            ? 272_000
            : Math.Clamp(profile.LongContextThresholdTokens, 1, 10_000_000);
        SetBandValues(
            profile.LongContext ?? profile.ShortContext,
            _longInput,
            _longCached,
            _longCacheWrite,
            _longOutput,
            _longCacheWriteEnabled);
        UpdateLongContextEnabledState();
        _shortCacheWrite.Enabled = _shortCacheWriteEnabled.Checked;
        _loading = false;
    }

    /// <summary>
    /// 将一个价格档位写入四个数值控件，并同步缓存写入是否有明确价格。
    /// </summary>
    private static void SetBandValues(
        PriceBand band,
        NumericUpDown input,
        NumericUpDown cached,
        NumericUpDown cacheWrite,
        NumericUpDown output,
        CheckBox cacheWriteEnabled)
    {
        input.Value = band.InputPerMillion;
        cached.Value = band.CachedInputPerMillion;
        cacheWrite.Value = band.CacheWritePerMillion ?? 0;
        output.Value = band.OutputPerMillion;
        cacheWriteEnabled.Checked = band.CacheWritePerMillion is not null;
    }

    /// <summary>
    /// 根据长上下文及缓存写入开关统一启用相关字段，避免保存不可见的陈旧价格。
    /// </summary>
    private void UpdateLongContextEnabledState()
    {
        foreach (var control in _longValueControls)
        {
            control.Enabled = _longEnabled.Checked;
        }

        _longCacheWrite.Enabled = _longEnabled.Checked && _longCacheWriteEnabled.Checked;
    }

    /// <summary>
    /// 创建一个区域化标签与输入控件组成的两列表单行。
    /// </summary>
    /// <param name="labelKey">左侧标签资源键。</param>
    /// <param name="control">右侧输入控件。</param>
    /// <returns>可随容器宽度扩展的表单行。</returns>
    private Control BuildLabeledRow(string labelKey, Control control)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        row.ColumnStyles.Add(new(SizeType.Absolute, 250));
        row.ColumnStyles.Add(new(SizeType.Percent, 100));
        var label = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
        _localizedControls[label] = labelKey;
        control.Margin = new Padding(3, 6, 3, 6);
        row.Controls.Add(label, 0, 0);
        row.Controls.Add(control, 1, 0);
        return row;
    }

    /// <summary>
    /// 创建价格分组内部统一的两列表单布局。
    /// </summary>
    /// <returns>标签列固定、输入列自适应的表格。</returns>
    private static TableLayoutPanel CreateFieldLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(14, 22, 14, 12)
        };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 232));
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        return layout;
    }

    /// <summary>
    /// 向价格分组追加一个固定高度字段，并记录标签资源键供语言切换刷新。
    /// </summary>
    private void AddField(TableLayoutPanel layout, string labelKey, Control control)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new(SizeType.Absolute, 44));
        var label = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
        _localizedControls[label] = labelKey;
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(3, 6, 3, 6);
        layout.Controls.Add(label, 0, row);
        layout.Controls.Add(control, 1, row);
    }

    /// <summary>
    /// 把“存在明确价格”复选框与对应数值输入组成一行，空值语义由复选框保存。
    /// </summary>
    /// <returns>左侧开关和右侧价格输入组成的组合控件。</returns>
    private static Control WithCheckBox(CheckBox checkBox, NumericUpDown number)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        row.ColumnStyles.Add(new(SizeType.AutoSize));
        row.ColumnStyles.Add(new(SizeType.Percent, 100));
        number.Dock = DockStyle.Fill;
        row.Controls.Add(checkBox, 0, 0);
        row.Controls.Add(number, 1, 0);
        return row;
    }

    /// <summary>
    /// 创建支持小数价格的输入控件，单位固定为 USD/百万 token。
    /// </summary>
    /// <returns>允许零缓存价和六位小数精度的价格输入框。</returns>
    private static NumericUpDown PriceNumber() => Number(0, 1_000_000, 0, 6);

    /// <summary>
    /// 创建范围、小数位和默认值明确的数值输入控件。
    /// </summary>
    private static NumericUpDown Number(
        decimal minimum,
        decimal maximum,
        decimal value,
        int decimalPlaces) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Value = value,
        DecimalPlaces = decimalPlaces,
        Increment = decimalPlaces == 0 ? 1 : 0.01m,
        ThousandsSeparator = true
    };
}
