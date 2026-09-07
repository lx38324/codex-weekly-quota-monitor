using System.Reflection;
using System.Drawing.Drawing2D;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor;

/// <summary>
/// 定义总览指标卡使用的强调色语义。
/// </summary>
public enum MetricCardTone
{
    Primary,
    Positive,
    Warning,
    Purple,
    Neutral
}

/// <summary>
/// 定义指标卡在总览中的视觉层级。
/// </summary>
public enum MetricCardStyle
{
    Standard,
    Hero
}

/// <summary>
/// 显示一个额度关键指标的标题、主值和补充说明，供总览页按 DPI 自动排列。
/// </summary>
public sealed class MetricCard : Panel
{
    private readonly Label _title = new();
    private readonly Label _value = new();
    private readonly Label _caption = new();
    private readonly TableLayoutPanel _layout = new();
    private decimal? _progressPercent;

    /// <summary>
    /// 构造带统一内边距和可访问名称的三行指标卡片。
    /// </summary>
    /// <param name="tone">卡片左侧强调条和主值使用的颜色语义。</param>
    /// <param name="style">核心额度 Hero 卡或普通辅助指标卡。</param>
    public MetricCard(
        MetricCardTone tone = MetricCardTone.Primary,
        MetricCardStyle style = MetricCardStyle.Standard)
    {
        Tone = tone;
        Style = style;
        BorderStyle = BorderStyle.None;
        Padding = new Padding(15, 11, 13, 10);
        Margin = new Padding(5);
        MinimumSize = new Size(135, LogicalHeight);
        Height = LogicalHeight;
        Dock = DockStyle.None;
        DoubleBuffered = true;

        _layout.Dock = DockStyle.Fill;
        _layout.RowCount = 4;
        _layout.ColumnCount = 1;
        _layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        _layout.BackColor = Color.Transparent;
        _layout.RowStyles.Add(new(SizeType.AutoSize));
        _layout.RowStyles.Add(new(SizeType.AutoSize));
        _layout.RowStyles.Add(new(SizeType.AutoSize));
        _layout.RowStyles.Add(new(SizeType.Percent, 100));
        _title.AutoSize = true;
        _title.Dock = DockStyle.Fill;
        _title.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8.5F, FontStyle.Bold);
        _title.Margin = new Padding(0, 0, 0, 3);
        _title.BackColor = Color.Transparent;
        _value.AutoSize = true;
        _value.Dock = DockStyle.Fill;
        _value.Font = new Font(
            SystemFonts.MessageBoxFont!.FontFamily,
            style == MetricCardStyle.Hero ? 20F : 16.5F,
            FontStyle.Bold);
        _value.Margin = new Padding(0, 0, 0, 4);
        _value.BackColor = Color.Transparent;
        _caption.AutoSize = true;
        _caption.Dock = DockStyle.Fill;
        _caption.Margin = new Padding(0);
        _caption.BackColor = Color.Transparent;
        _layout.Controls.Add(_title, 0, 0);
        _layout.Controls.Add(_value, 0, 1);
        _layout.Controls.Add(_caption, 0, 2);
        Controls.Add(_layout);
    }

    public MetricCardTone Tone { get; }
    public MetricCardStyle Style { get; }
    public int LogicalHeight => Style == MetricCardStyle.Hero ? 100 : 92;

    /// <summary>
    /// 根据当前 DPI 下三行文字的真实首选高度计算卡片高度，同时保留设计基线的最小高度。
    /// </summary>
    /// <param name="proposedSize">父级计划分配的卡片宽度。</param>
    /// <returns>不会裁切标题、主值和说明文字的卡片尺寸。</returns>
    public override Size GetPreferredSize(Size proposedSize)
    {
        var availableWidth = Math.Max(1, proposedSize.Width - Padding.Horizontal);
        var contentHeight = new[] { _title, _value, _caption }.Sum(label =>
            label.GetPreferredSize(new Size(availableWidth, 0)).Height + label.Margin.Vertical);
        var scaledBaseline = (int)Math.Ceiling(LogicalHeight * DeviceDpi / 96F);
        return new(
            Math.Max(MinimumSize.Width, proposedSize.Width),
            Math.Max(scaledBaseline, contentHeight + Padding.Vertical + Math.Max(4, DeviceDpi / 24)));
    }

    /// <summary>按实际卡片内容宽度约束三行文字，字体回退和高 DPI 下允许换行，避免服务器字体环境裁切。</summary>
    protected override void OnLayout(LayoutEventArgs levent)
    {
        var width = Math.Max(1, ClientSize.Width - Padding.Horizontal);
        foreach (var label in new[] { _title, _value, _caption })
        {
            if (label.MaximumSize.Width != width) label.MaximumSize = new Size(width, 0);
        }
        base.OnLayout(levent);
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
        var hero = Style == MetricCardStyle.Hero;
        _title.ForeColor = hero ? Color.FromArgb(220, 234, 252) : AppTheme.Current.MutedText;
        _value.ForeColor = ResolveAccent();
        _caption.ForeColor = hero ? Color.FromArgb(190, 209, 233) : AppTheme.Current.MutedText;
        BackColor = hero ? Color.FromArgb(8, 25, 52) : AppTheme.Current.Surface;
        _layout.BackColor = BackColor;
        Invalidate();
    }

    /// <summary>
    /// 设置卡片底部进度指示；null 表示不绘制，0 到 100 表示业务百分比。
    /// </summary>
    /// <param name="percent">需要展示的百分比。</param>
    public void SetProgress(decimal? percent)
    {
        if (percent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "指标卡进度必须在 0 到 100 之间。");
        }

        _progressPercent = percent;
        Invalidate();
    }

    /// <summary>
    /// 绘制圆角轻量边框和顶部状态光带，形成轻盈而清晰的科技卡片层级。
    /// </summary>
    /// <param name="e">当前绘制上下文。</param>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var accentColor = ResolveAccent();
        var borderColor = Style == MetricCardStyle.Hero
            ? Color.FromArgb(AppTheme.Current.IsDark ? 130 : 88, accentColor)
            : AppTheme.Current.Border;
        using var border = new Pen(borderColor, Math.Max(1F, DeviceDpi / 96F));
        using var accent = new SolidBrush(ResolveAccent());
        using var path = CreateRoundedRectangle(ClientRectangle, Math.Max(9, DeviceDpi / 10));
        e.Graphics.DrawPath(border, path);
        e.Graphics.FillRectangle(accent, 14, 0, Math.Max(32, Width / 5), Math.Max(3, DeviceDpi / 32));

        if (_progressPercent is decimal progress)
        {
            var lineHeight = Math.Max(4, DeviceDpi / 24);
            var trackRectangle = new Rectangle(0, Height - lineHeight, Width, lineHeight);
            using var track = new SolidBrush(AppTheme.Current.Grid);
            e.Graphics.FillRectangle(track, trackRectangle);
            var progressWidth = (int)Math.Round(Width * (double)(progress / 100m));
            e.Graphics.FillRectangle(accent, 0, Height - lineHeight, progressWidth, lineHeight);
        }
    }

    /// <summary>
    /// 将卡片语义转换为当前主题下的强调色。
    /// </summary>
    /// <returns>适合当前明暗主题的高对比度颜色。</returns>
    private Color ResolveAccent() => Tone switch
    {
        MetricCardTone.Primary => Style == MetricCardStyle.Hero
            ? Color.FromArgb(34, 211, 238)
            : AppTheme.Current.Accent,
        MetricCardTone.Positive => AppTheme.Current.Positive,
        MetricCardTone.Warning => AppTheme.Current.Warning,
        MetricCardTone.Purple => Style == MetricCardStyle.Hero || AppTheme.Current.IsDark
            ? Color.FromArgb(167, 139, 250)
            : Color.FromArgb(124, 76, 196),
        MetricCardTone.Neutral => AppTheme.Current.Text,
        _ => throw new InvalidOperationException($"不支持的指标卡颜色语义：{Tone}。")
    };

    /// <summary>
    /// 创建贴合指标卡客户区的圆角矩形路径。
    /// </summary>
    /// <param name="rectangle">需要描边的客户区。</param>
    /// <param name="radius">圆角半径。</param>
    /// <returns>由调用方释放的圆角路径。</returns>
    private static GraphicsPath CreateRoundedRectangle(Rectangle rectangle, int radius)
    {
        var diameter = radius * 2;
        var bounds = Rectangle.Inflate(rectangle, -1, -1);
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// 在主工作台最小宽度约束内将五张核心指标卡稳定排列为等宽单行，避免滚动条引发布局抖动。
/// </summary>
public sealed class DashboardCardGrid : Panel
{
    private readonly List<MetricCard> _cards = [];

    /// <summary>
    /// 构造填充父级的双缓冲指标带。
    /// </summary>
    public DashboardCardGrid()
    {
        Dock = DockStyle.Fill;
        AutoScroll = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        DoubleBuffered = true;
    }

    /// <summary>
    /// 以当前 DPI 下最高卡片的首选高度决定整个指标带高度，供父级 AutoSize 行布局使用。
    /// </summary>
    /// <param name="proposedSize">父级可提供的指标带宽度。</param>
    /// <returns>五张卡单行排列且文字完整可见所需的尺寸。</returns>
    public override Size GetPreferredSize(Size proposedSize)
    {
        if (_cards.Count == 0)
        {
            return base.GetPreferredSize(proposedSize);
        }

        var scale = DeviceDpi / 96F;
        var outerPadding = (int)Math.Round(4 * scale);
        var gap = (int)Math.Round(10 * scale);
        var usableWidth = Math.Max(_cards.Count, proposedSize.Width - outerPadding * 2);
        var availableCardWidth = Math.Max(_cards.Count, usableWidth - gap * (_cards.Count - 1));
        var heroWeight = _cards[0].Style == MetricCardStyle.Hero ? 1.55 : 1.0;
        var unitWidth = availableCardWidth / (heroWeight + _cards.Count - 1);
        var maximumHeight = 0;
        for (var index = 0; index < _cards.Count; index++)
        {
            var weight = index == 0 ? heroWeight : 1.0;
            var width = Math.Max(1, (int)Math.Round(unitWidth * weight));
            maximumHeight = Math.Max(maximumHeight, _cards[index].GetPreferredSize(new Size(width, 0)).Height);
        }

        return new(Math.Max(1, proposedSize.Width), maximumHeight + outerPadding * 2);
    }

    /// <summary>
    /// 注册总览需要显示的指标卡，并按照注册顺序从左到右、从上到下排列。
    /// </summary>
    /// <param name="cards">需要由本容器统一定位的指标卡。</param>
    public void SetCards(params MetricCard[] cards)
    {
        Controls.Clear();
        _cards.Clear();
        foreach (var card in cards)
        {
            card.Dock = DockStyle.None;
            _cards.Add(card);
            Controls.Add(card);
        }

        PerformLayout();
    }

    /// <summary>
    /// 在父级布局后重新计算列数、卡片尺寸、滚动范围和每张卡片的位置。
    /// </summary>
    /// <param name="levent">本次布局事件上下文。</param>
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        LayoutCards();
    }

    /// <summary>
    /// 父级 DPI 改变后使用新的物理像素比例重新排列卡片。
    /// </summary>
    /// <param name="e">DPI 变化事件。</param>
    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        PerformLayout();
    }

    /// <summary>
    /// 根据当前可用宽度把全部卡片稳定排成一行；窗口最小宽度保证每张卡仍可读。
    /// </summary>
    private void LayoutCards()
    {
        if (_cards.Count == 0 || ClientSize.Width <= 0)
        {
            return;
        }

        var scale = DeviceDpi / 96F;
        var outerPadding = (int)Math.Round(4 * scale);
        var gap = (int)Math.Round(10 * scale);
        var usableWidth = Math.Max(_cards.Count, ClientSize.Width - outerPadding * 2);
        var availableCardWidth = Math.Max(
            _cards.Count,
            usableWidth - gap * (_cards.Count - 1));
        var heroWeight = _cards[0].Style == MetricCardStyle.Hero ? 1.55 : 1.0;
        var unitWidth = availableCardWidth / (heroWeight + _cards.Count - 1);
        var cardHeight = Math.Max(1, ClientSize.Height - outerPadding * 2);
        var x = outerPadding;

        for (var index = 0; index < _cards.Count; index++)
        {
            var weight = index == 0 ? heroWeight : 1.0;
            var cardWidth = index == _cards.Count - 1
                ? Math.Max(1, ClientSize.Width - outerPadding - x)
                : Math.Max(1, (int)Math.Round(unitWidth * weight));
            _cards[index].Bounds = new Rectangle(
                x,
                outerPadding,
                cardWidth,
                cardHeight);
            x += cardWidth + gap;
        }
    }
}

/// <summary>
/// 在主窗口左侧显示紧凑品牌、三个业务入口和本地版本信息，形成轻量科技风应用外壳。
/// </summary>
public sealed class ApplicationSidebar : Panel
{
    private readonly Label _overline = new();
    private readonly Label _product = new();
    private readonly Label _tagline = new();
    private readonly Label _version = new();
    private readonly TableLayoutPanel _brand = new();
    private readonly FlowLayoutPanel _navigation = new();
    private readonly Dictionary<DashboardSection, SidebarNavigationButton> _buttons = [];

    public event Action<DashboardSection>? SectionRequested;

    /// <summary>
    /// 构造固定宽度侧栏，并按额度趋势、设置、诊断顺序创建矢量图标导航。
    /// </summary>
    public ApplicationSidebar()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(12, 18, 12, 14);

        _overline.AutoSize = true;
        _overline.Text = "CODEX  //  LOCAL";
        _overline.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8F, FontStyle.Bold);
        _product.Name = "SidebarProductLabel";
        _product.AutoSize = true;
        _product.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10.5F, FontStyle.Bold);
        _product.Margin = new Padding(0);
        _tagline.AutoSize = true;
        _tagline.Text = "QUOTA INTELLIGENCE";
        _tagline.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 7.5F, FontStyle.Regular);

        _brand.Dock = DockStyle.Fill;
        _brand.AutoSize = true;
        _brand.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _brand.ColumnCount = 1;
        _brand.RowCount = 3;
        _brand.BackColor = Color.Transparent;
        _brand.Padding = new Padding(6, 0, 0, 0);
        _brand.RowStyles.Add(new(SizeType.AutoSize));
        _brand.RowStyles.Add(new(SizeType.AutoSize));
        _brand.RowStyles.Add(new(SizeType.AutoSize));
        _brand.Controls.Add(_overline);
        _brand.Controls.Add(_product);
        _brand.Controls.Add(_tagline);

        _navigation.Dock = DockStyle.Fill;
        _navigation.FlowDirection = FlowDirection.TopDown;
        _navigation.WrapContents = false;
        _navigation.Padding = new Padding(0, 6, 0, 0);
        AddNavigation(DashboardSection.Dashboard, SidebarIcon.Overview);
        AddNavigation(DashboardSection.Settings, SidebarIcon.Settings);
        AddNavigation(DashboardSection.Diagnostics, SidebarIcon.Diagnostics);

        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version
            ?? throw new InvalidDataException("无法读取应用程序集版本。");
        _version.AutoSize = true;
        _version.Text = $"v{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}  •  LOCAL";
        _version.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8F);
        _version.Margin = new Padding(6, 0, 0, 0);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.AutoSize));
        root.Controls.Add(_brand, 0, 0);
        root.Controls.Add(_navigation, 0, 1);
        root.Controls.Add(_version, 0, 2);
        Controls.Add(root);
        ApplyLocalization();
        ApplyAppearance();
    }

    /// <summary>
    /// 更新产品名和三个导航入口的当前语言文本。
    /// </summary>
    public void ApplyLocalization()
    {
        _product.Text = UiText.Get("ProductName");
        _buttons[DashboardSection.Dashboard].SetText(UiText.Get("TabDashboard"));
        _buttons[DashboardSection.Settings].SetText(UiText.Get("TabSettings"));
        _buttons[DashboardSection.Diagnostics].SetText(UiText.Get("TabDiagnostics"));
    }

    /// <summary>
    /// 选择一个业务入口，并让侧栏只突出显示该入口。
    /// </summary>
    /// <param name="section">当前主窗口展示的业务页。</param>
    public void SetSelected(DashboardSection section)
    {
        foreach (var pair in _buttons)
        {
            pair.Value.SetSelected(pair.Key == section);
        }
    }

    /// <summary>
    /// 根据明暗主题刷新深色侧栏、品牌文本和全部导航按钮。
    /// </summary>
    public void ApplyAppearance()
    {
        BackColor = SidebarBackground;
        _overline.ForeColor = Color.FromArgb(34, 211, 238);
        _product.ForeColor = Color.FromArgb(241, 245, 249);
        _tagline.ForeColor = Color.FromArgb(100, 116, 139);
        _version.ForeColor = Color.FromArgb(100, 116, 139);
        _navigation.BackColor = SidebarBackground;
        foreach (var button in _buttons.Values)
        {
            button.ApplyAppearance();
        }

        Invalidate(true);
    }

    /// <summary>
    /// 侧栏尺寸变化时让全部导航按钮填满扣除内边距后的宽度。
    /// </summary>
    /// <param name="levent">当前布局事件。</param>
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        ConstrainProductNameToBrandWidth();
        var buttonWidth = Math.Max(120, _navigation.ClientSize.Width - _navigation.Padding.Horizontal - 4);
        foreach (var button in _buttons.Values)
        {
            button.Width = buttonWidth;
        }
    }

    /// <summary>
    /// 按侧栏品牌区的实际客户宽度限制产品名，使中英文名称在高缩放时换行而不是横向裁切。
    /// </summary>
    private void ConstrainProductNameToBrandWidth()
    {
        var availableWidth = Math.Max(
            1,
            _brand.ClientSize.Width - _brand.Padding.Horizontal - _product.Margin.Horizontal);
        if (_product.MaximumSize.Width != availableWidth)
        {
            _product.MaximumSize = new Size(availableWidth, 0);
        }
    }

    internal static Color SidebarBackground => AppTheme.Current.IsDark
        ? Color.FromArgb(5, 10, 20)
        : Color.FromArgb(9, 17, 31);

    /// <summary>
    /// 创建一个侧栏按钮并把点击动作转换为业务页请求事件。
    /// </summary>
    /// <param name="section">按钮对应的业务页。</param>
    /// <param name="icon">需要绘制的矢量图标。</param>
    private void AddNavigation(DashboardSection section, SidebarIcon icon)
    {
        var button = new SidebarNavigationButton(section, icon);
        button.Click += (_, _) => SectionRequested?.Invoke(section);
        _buttons.Add(section, button);
        _navigation.Controls.Add(button);
    }
}

/// <summary>
/// 定义侧栏三个业务入口的无字体依赖矢量图标。
/// </summary>
public enum SidebarIcon
{
    Overview,
    Settings,
    Diagnostics
}

/// <summary>
/// 绘制带矢量图标、悬停背景、选中强调条和键盘操作的侧栏导航按钮。
/// </summary>
public sealed class SidebarNavigationButton : Control
{
    private readonly SidebarIcon _icon;
    private bool _selected;
    private bool _hovered;

    /// <summary>
    /// 构造一个绑定业务页和矢量图标的可访问导航按钮。
    /// </summary>
    /// <param name="section">点击后打开的业务页。</param>
    /// <param name="icon">按钮左侧图标。</param>
    public SidebarNavigationButton(DashboardSection section, SidebarIcon icon)
    {
        _icon = icon;
        AccessibleDescription = section.ToString();
        Height = 46;
        Margin = new Padding(0, 0, 0, 6);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        MouseEnter += (_, _) => SetHovered(true);
        MouseLeave += (_, _) => SetHovered(false);
    }

    /// <summary>
    /// 设置当前语言下的按钮文字和屏幕阅读器名称。
    /// </summary>
    /// <param name="text">用户可见导航名称。</param>
    public void SetText(string text)
    {
        Text = text;
        AccessibleName = text;
        Invalidate();
    }

    /// <summary>
    /// 设置按钮是否代表当前可见页面。
    /// </summary>
    /// <param name="selected">当前页面为本按钮对应页面时传入 true。</param>
    public void SetSelected(bool selected)
    {
        _selected = selected;
        AccessibleDescription = selected ? "selected" : string.Empty;
        Invalidate();
    }

    /// <summary>
    /// 刷新固定深色侧栏中的按钮背景和前景。
    /// </summary>
    public void ApplyAppearance()
    {
        BackColor = ApplicationSidebar.SidebarBackground;
        Invalidate();
    }

    /// <summary>
    /// 绘制选中背景、青色强调条、矢量图标和可换行导航文本；英文连接符不作为快捷键标记。
    /// </summary>
    /// <param name="e">当前绘制上下文。</param>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var background = _selected
            ? Color.FromArgb(22, 41, 67)
            : _hovered
                ? Color.FromArgb(16, 30, 50)
                : ApplicationSidebar.SidebarBackground;
        using var backgroundBrush = new SolidBrush(background);
        e.Graphics.FillRectangle(backgroundBrush, ClientRectangle);
        if (_selected)
        {
            using var accent = new SolidBrush(Color.FromArgb(34, 211, 238));
            e.Graphics.FillRectangle(accent, 0, 8, Math.Max(3, DeviceDpi / 32), Height - 16);
        }

        var iconColor = _selected ? Color.FromArgb(34, 211, 238) : Color.FromArgb(117, 137, 162);
        DrawIcon(e.Graphics, new Rectangle(18, (Height - 20) / 2, 20, 20), iconColor);
        using var font = new Font(
            SystemFonts.MessageBoxFont!.FontFamily,
            9.5F,
            _selected ? FontStyle.Bold : FontStyle.Regular);
        var textWidth = Math.Max(1, Width - 62);
        const TextFormatFlags textFlags = TextFormatFlags.Left | TextFormatFlags.WordBreak |
            TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;
        var textHeight = TextRenderer.MeasureText(Text, font, new Size(textWidth, int.MaxValue), textFlags).Height;
        TextRenderer.DrawText(
            e.Graphics,
            Text,
            font,
            new Rectangle(52, Math.Max(0, (Height - textHeight) / 2), textWidth, textHeight),
            _selected ? Color.FromArgb(241, 245, 249) : Color.FromArgb(166, 181, 201),
            textFlags);
    }

    /// <summary>
    /// Enter 或空格键触发与鼠标点击相同的业务页切换。
    /// </summary>
    /// <param name="e">键盘事件。</param>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 更新鼠标悬停状态并触发重绘。
    /// </summary>
    /// <param name="hovered">鼠标当前是否位于按钮内。</param>
    private void SetHovered(bool hovered)
    {
        _hovered = hovered;
        Invalidate();
    }

    /// <summary>
    /// 根据按钮业务类型绘制无需外部字体或图片资源的 20×20 图标。
    /// </summary>
    /// <param name="graphics">当前绘图上下文。</param>
    /// <param name="bounds">图标目标区域。</param>
    /// <param name="color">图标线条颜色。</param>
    private void DrawIcon(Graphics graphics, Rectangle bounds, Color color)
    {
        using var pen = new Pen(color, Math.Max(1.6F, DeviceDpi / 60F))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        switch (_icon)
        {
            case SidebarIcon.Overview:
                graphics.DrawRectangle(pen, bounds.Left + 1, bounds.Top + 1, 7, 7);
                graphics.DrawRectangle(pen, bounds.Left + 12, bounds.Top + 1, 7, 7);
                graphics.DrawRectangle(pen, bounds.Left + 1, bounds.Top + 12, 7, 7);
                graphics.DrawRectangle(pen, bounds.Left + 12, bounds.Top + 12, 7, 7);
                break;
            case SidebarIcon.Settings:
                graphics.DrawLine(pen, bounds.Left + 1, bounds.Top + 4, bounds.Right - 1, bounds.Top + 4);
                graphics.DrawLine(pen, bounds.Left + 1, bounds.Top + 10, bounds.Right - 1, bounds.Top + 10);
                graphics.DrawLine(pen, bounds.Left + 1, bounds.Top + 16, bounds.Right - 1, bounds.Top + 16);
                graphics.DrawEllipse(pen, bounds.Left + 5, bounds.Top + 1, 6, 6);
                graphics.DrawEllipse(pen, bounds.Left + 12, bounds.Top + 7, 6, 6);
                graphics.DrawEllipse(pen, bounds.Left + 3, bounds.Top + 13, 6, 6);
                break;
            case SidebarIcon.Diagnostics:
                graphics.DrawEllipse(pen, bounds.Left + 1, bounds.Top + 1, 18, 18);
                graphics.DrawLines(pen, [
                    new(bounds.Left + 4, bounds.Top + 11),
                    new(bounds.Left + 7, bounds.Top + 11),
                    new(bounds.Left + 9, bounds.Top + 6),
                    new(bounds.Left + 12, bounds.Top + 14),
                    new(bounds.Left + 14, bounds.Top + 10),
                    new(bounds.Left + 17, bounds.Top + 10)]);
                break;
            default:
                throw new InvalidOperationException($"不支持的侧栏图标：{_icon}。");
        }
    }
}

/// <summary>
/// 在总览标题区域显示 App Server 连接状态与最近更新时间。
/// </summary>
public sealed class ConnectionBadge : Control
{
    private bool _connected;
    private DateTimeOffset _updatedAt;

    /// <summary>
    /// 构造透明背景、可随 DPI 缩放的连接状态徽标。
    /// </summary>
    public ConnectionBadge()
    {
        SetStyle(
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer,
            true);
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        AutoSize = true;
        Font = SystemFonts.MessageBoxFont!;
        Text = BuildText();
        Size = GetPreferredSize(Size.Empty);
        Anchor = AnchorStyles.Top | AnchorStyles.Right;
    }

    /// <summary>
    /// 按当前 DPI、字体和紧凑状态文字测量徽标尺寸，确保绘图区不需要省略号。
    /// </summary>
    /// <param name="proposedSize">父布局建议尺寸；徽标按自身内容决定最小所需尺寸。</param>
    /// <returns>完整容纳状态圆点和单行文字的尺寸。</returns>
    public override Size GetPreferredSize(Size proposedSize)
    {
        var textSize = TextRenderer.MeasureText(
            BuildText(),
            Font,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        var dotSize = ScaleLogical(8);
        var width = ScaleLogical(13) + dotSize + ScaleLogical(8) + textSize.Width + ScaleLogical(13);
        var height = Math.Max(ScaleLogical(34), textSize.Height + ScaleLogical(12));
        return new Size(width, height);
    }

    /// <summary>
    /// 更新连接布尔值和快照时间，并立即重绘区域化文本。
    /// </summary>
    /// <param name="connected">App Server 当前是否连接。</param>
    /// <param name="updatedAt">展示快照更新时间。</param>
    public void SetState(bool connected, DateTimeOffset updatedAt)
    {
        _connected = connected;
        _updatedAt = updatedAt;
        Text = BuildText();
        AccessibleName = BuildText();
        Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    /// <summary>
    /// 绘制圆角半透明背景、状态圆点和最近更新时间。
    /// </summary>
    /// <param name="e">当前绘制上下文。</param>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var statusColor = _connected ? AppTheme.Current.Positive : AppTheme.Current.Warning;
        var backgroundColor = AppTheme.Current.IsDark
            ? Color.FromArgb(45, statusColor)
            : Color.FromArgb(24, statusColor);
        using var path = CreateRoundedRectangle(ClientRectangle, Math.Max(8, DeviceDpi / 10));
        using var background = new SolidBrush(backgroundColor);
        using var border = new Pen(Color.FromArgb(AppTheme.Current.IsDark ? 110 : 80, statusColor));
        e.Graphics.FillPath(background, path);
        e.Graphics.DrawPath(border, path);

        var dotSize = ScaleLogical(8);
        var dotY = (Height - dotSize) / 2;
        var leftPadding = ScaleLogical(13);
        var textGap = ScaleLogical(8);
        var rightPadding = ScaleLogical(13);
        using var dot = new SolidBrush(statusColor);
        e.Graphics.FillEllipse(dot, leftPadding, dotY, dotSize, dotSize);
        var textLeft = leftPadding + dotSize + textGap;
        var textRectangle = new Rectangle(textLeft, 0, Math.Max(1, Width - textLeft - rightPadding), Height);
        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            textRectangle,
            AppTheme.Current.Text,
            TextFormatFlags.Left |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding |
            TextFormatFlags.SingleLine);
    }

    /// <summary>
    /// 刷新当前主题和语言下的徽标文字与颜色。
    /// </summary>
    public void ApplyAppearance()
    {
        Text = BuildText();
        AccessibleName = BuildText();
        Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    /// <summary>
    /// 构造当前语言下的连接状态与更新时间文本。
    /// </summary>
    /// <returns>适合绘制和屏幕阅读器使用的单行文本。</returns>
    private string BuildText() => UiText.Format(
        "ConnectionBadgeFormat",
        _connected ? UiText.Get("Connected") : UiText.Get("Disconnected"),
        _updatedAt.LocalDateTime);

    /// <summary>
    /// 将逻辑像素转换为当前显示器 DPI 下的物理像素，供徽标绘制与测量共享。
    /// </summary>
    /// <param name="logicalPixels">96 DPI 基准下的逻辑像素。</param>
    /// <returns>当前 DPI 下至少为 1 的物理像素值。</returns>
    private int ScaleLogical(int logicalPixels) =>
        Math.Max(1, (int)Math.Round(logicalPixels * DeviceDpi / 96F));

    /// <summary>
    /// 创建适合徽标背景的圆角矩形路径。
    /// </summary>
    /// <param name="rectangle">控件客户区。</param>
    /// <param name="radius">圆角半径。</param>
    /// <returns>调用方负责释放的 GraphicsPath。</returns>
    private static GraphicsPath CreateRoundedRectangle(Rectangle rectangle, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter - 1, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter - 1, rectangle.Bottom - diameter - 1, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter - 1, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
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
            $"unattributed={_view.HistoricalReplayUnattributedIntervals}; " +
            $"archive_windows={_view.HistoricalArchiveReplayWindowCount}, " +
            $"archive_samples={_view.HistoricalArchiveReplaySampleCount}, " +
            $"archive_files={_view.HistoricalArchiveReplayFilesScanned}, " +
            $"archive_unattributed={_view.HistoricalArchiveReplayUnattributedIntervals}");
        AddFact("DiagnosticsDataFolder", AppPaths.DataDirectory);
        AddFact("DiagnosticsSessionFolder", _settings.SessionRoot);
        AddFact("DiagnosticsPricing", _view.PricingVersion);
        AddFact("DiagnosticsImageCost", UiText.Get("ImageCostUnavailable"));
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
