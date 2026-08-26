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
        Padding = new Padding(18, 14, 16, 12);
        Margin = new Padding(8);
        MinimumSize = new Size(210, LogicalHeight);
        Height = LogicalHeight;
        Dock = DockStyle.None;
        DoubleBuffered = true;

        _layout.Dock = DockStyle.Fill;
        _layout.RowCount = 4;
        _layout.ColumnCount = 1;
        _layout.BackColor = Color.Transparent;
        _layout.RowStyles.Add(new(SizeType.AutoSize));
        _layout.RowStyles.Add(new(SizeType.AutoSize));
        _layout.RowStyles.Add(new(SizeType.AutoSize));
        _layout.RowStyles.Add(new(SizeType.Percent, 100));
        _title.AutoSize = true;
        _title.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9.5F, FontStyle.Bold);
        _title.Margin = new Padding(0, 0, 0, 5);
        _title.BackColor = Color.Transparent;
        _value.AutoSize = true;
        _value.Font = new Font(
            SystemFonts.MessageBoxFont!.FontFamily,
            style == MetricCardStyle.Hero ? 25F : 21F,
            FontStyle.Bold);
        _value.Margin = new Padding(0, 0, 0, 7);
        _value.BackColor = Color.Transparent;
        _caption.AutoSize = true;
        _caption.Margin = new Padding(0);
        _caption.BackColor = Color.Transparent;
        _layout.Controls.Add(_title, 0, 0);
        _layout.Controls.Add(_value, 0, 1);
        _layout.Controls.Add(_caption, 0, 2);
        Controls.Add(_layout);
    }

    public MetricCardTone Tone { get; }
    public MetricCardStyle Style { get; }
    public int LogicalHeight => Style == MetricCardStyle.Hero ? 136 : 116;

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
        _title.ForeColor = Style == MetricCardStyle.Hero
            ? Color.FromArgb(174, 191, 211)
            : AppTheme.Current.MutedText;
        _value.ForeColor = ResolveAccent();
        _caption.ForeColor = Style == MetricCardStyle.Hero
            ? Color.FromArgb(137, 155, 177)
            : AppTheme.Current.MutedText;
        BackColor = Style == MetricCardStyle.Hero
            ? Color.FromArgb(13, 23, 40)
            : AppTheme.Current.Surface;
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
    /// 绘制轻量边框和左侧强调条，避免系统 FixedSingle 边框的旧式观感。
    /// </summary>
    /// <param name="e">当前绘制上下文。</param>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var accentColor = ResolveAccent();
        var borderColor = Style == MetricCardStyle.Hero
            ? Color.FromArgb(105, accentColor)
            : AppTheme.Current.Border;
        using var border = new Pen(borderColor, Math.Max(1F, DeviceDpi / 96F));
        using var accent = new SolidBrush(ResolveAccent());
        var borderRectangle = ClientRectangle;
        borderRectangle.Width -= 1;
        borderRectangle.Height -= 1;
        e.Graphics.DrawRectangle(border, borderRectangle);
        e.Graphics.FillRectangle(accent, 0, 0, Math.Max(4, DeviceDpi / 24), Height);
        if (Style == MetricCardStyle.Hero)
        {
            using var circuitPen = new Pen(Color.FromArgb(65, accentColor), Math.Max(1F, DeviceDpi / 96F));
            var step = Math.Max(9, DeviceDpi / 10);
            for (var index = 0; index < 3; index++)
            {
                var x = Width - 22 - index * step;
                e.Graphics.DrawLine(circuitPen, x, 12, x + 9, 12);
                e.Graphics.DrawLine(circuitPen, x + 9, 12, x + 9, 19 + index * 3);
            }
        }

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
}

/// <summary>
/// 按实际客户区宽度和显示器 DPI 将指标卡排列为三列、两列或单列，并在空间不足时提供滚动。
/// </summary>
public sealed class DashboardCardGrid : Panel
{
    private readonly List<MetricCard> _cards = [];

    /// <summary>
    /// 构造填充父级且支持纵向滚动的响应式卡片容器。
    /// </summary>
    public DashboardCardGrid()
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;
        DoubleBuffered = true;
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
    /// 根据当前可用宽度选择最多三列并设置卡片边界；计算过程不依赖 AutoSize。
    /// </summary>
    private void LayoutCards()
    {
        if (_cards.Count == 0 || ClientSize.Width <= 0)
        {
            return;
        }

        var scale = DeviceDpi / 96F;
        var outerPadding = (int)Math.Round(8 * scale);
        var gap = (int)Math.Round(12 * scale);
        var minimumCardWidth = (int)Math.Round(220 * scale);
        var usableWidth = Math.Max(minimumCardWidth, ClientSize.Width - outerPadding * 2);
        var columns = Math.Clamp((usableWidth + gap) / (minimumCardWidth + gap), 1, 3);
        var cardWidth = Math.Max(
            minimumCardWidth,
            (usableWidth - gap * (columns - 1)) / columns);
        var rows = (int)Math.Ceiling(_cards.Count / (double)columns);
        var rowHeights = new int[rows];
        for (var index = 0; index < _cards.Count; index++)
        {
            var row = index / columns;
            rowHeights[row] = Math.Max(
                rowHeights[row],
                (int)Math.Round(_cards[index].LogicalHeight * scale));
        }

        var requiredWidth = outerPadding * 2 + columns * cardWidth + Math.Max(0, columns - 1) * gap;
        var requiredHeight = outerPadding * 2 + rowHeights.Sum() + Math.Max(0, rows - 1) * gap;
        var requiredSize = new Size(requiredWidth, requiredHeight);
        if (AutoScrollMinSize != requiredSize)
        {
            AutoScrollMinSize = requiredSize;
        }

        var scrollOffset = AutoScrollPosition;
        var rowOffsets = new int[rows];
        for (var row = 1; row < rows; row++)
        {
            rowOffsets[row] = rowOffsets[row - 1] + rowHeights[row - 1] + gap;
        }

        for (var index = 0; index < _cards.Count; index++)
        {
            var row = index / columns;
            var column = index % columns;
            _cards[index].Bounds = new Rectangle(
                scrollOffset.X + outerPadding + column * (cardWidth + gap),
                scrollOffset.Y + outerPadding + rowOffsets[row],
                cardWidth,
                rowHeights[row]);
        }
    }
}

/// <summary>
/// 在主窗口左侧显示品牌、四个业务入口和本地版本信息，形成稳定的科技风应用外壳。
/// </summary>
public sealed class ApplicationSidebar : Panel
{
    private readonly Label _overline = new();
    private readonly Label _product = new();
    private readonly Label _tagline = new();
    private readonly Label _version = new();
    private readonly FlowLayoutPanel _navigation = new();
    private readonly Dictionary<DashboardSection, SidebarNavigationButton> _buttons = [];

    public event Action<DashboardSection>? SectionRequested;

    /// <summary>
    /// 构造固定宽度侧栏，并按总览、历史、设置、诊断顺序创建矢量图标导航。
    /// </summary>
    public ApplicationSidebar()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(18, 24, 18, 18);

        _overline.AutoSize = true;
        _overline.Text = "CODEX  //  LOCAL";
        _overline.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8F, FontStyle.Bold);
        _product.AutoSize = true;
        _product.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 13F, FontStyle.Bold);
        _tagline.AutoSize = true;
        _tagline.Text = "QUOTA INTELLIGENCE";
        _tagline.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 7.5F, FontStyle.Regular);

        var brand = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent,
            Padding = new Padding(6, 0, 0, 0)
        };
        brand.RowStyles.Add(new(SizeType.AutoSize));
        brand.RowStyles.Add(new(SizeType.AutoSize));
        brand.RowStyles.Add(new(SizeType.AutoSize));
        brand.Controls.Add(_overline);
        brand.Controls.Add(_product);
        brand.Controls.Add(_tagline);

        _navigation.Dock = DockStyle.Fill;
        _navigation.FlowDirection = FlowDirection.TopDown;
        _navigation.WrapContents = false;
        _navigation.Padding = new Padding(0, 10, 0, 0);
        AddNavigation(DashboardSection.Dashboard, SidebarIcon.Overview);
        AddNavigation(DashboardSection.History, SidebarIcon.History);
        AddNavigation(DashboardSection.Settings, SidebarIcon.Settings);
        AddNavigation(DashboardSection.Diagnostics, SidebarIcon.Diagnostics);

        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version
            ?? throw new InvalidDataException("无法读取应用程序集版本。");
        _version.AutoSize = true;
        _version.Text = $"v{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}  •  LOCAL";
        _version.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8F);
        _version.Margin = new Padding(6, 0, 0, 0);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new(SizeType.Absolute, 104));
        root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.AutoSize));
        root.Controls.Add(brand, 0, 0);
        root.Controls.Add(_navigation, 0, 1);
        root.Controls.Add(_version, 0, 2);
        Controls.Add(root);
        ApplyLocalization();
        ApplyAppearance();
    }

    /// <summary>
    /// 更新产品名和四个导航入口的当前语言文本。
    /// </summary>
    public void ApplyLocalization()
    {
        _product.Text = UiText.Get("ProductName");
        _buttons[DashboardSection.Dashboard].SetText(UiText.Get("TabDashboard"));
        _buttons[DashboardSection.History].SetText(UiText.Get("TabHistory"));
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
        var buttonWidth = Math.Max(120, _navigation.ClientSize.Width - _navigation.Padding.Horizontal - 4);
        foreach (var button in _buttons.Values)
        {
            button.Width = buttonWidth;
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
/// 定义侧栏四个业务入口的无字体依赖矢量图标。
/// </summary>
public enum SidebarIcon
{
    Overview,
    History,
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
        Height = 52;
        Margin = new Padding(0, 0, 0, 8);
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
    /// 绘制选中背景、青色强调条、矢量图标和导航文本。
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
        TextRenderer.DrawText(
            e.Graphics,
            Text,
            font,
            new Rectangle(52, 0, Width - 62, Height),
            _selected ? Color.FromArgb(241, 245, 249) : Color.FromArgb(166, 181, 201),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
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
            case SidebarIcon.History:
                graphics.DrawLine(pen, bounds.Left + 1, bounds.Bottom - 2, bounds.Right - 1, bounds.Bottom - 2);
                graphics.DrawLine(pen, bounds.Left + 2, bounds.Bottom - 2, bounds.Left + 2, bounds.Top + 1);
                graphics.DrawLines(pen, [
                    new(bounds.Left + 4, bounds.Top + 14),
                    new(bounds.Left + 8, bounds.Top + 10),
                    new(bounds.Left + 12, bounds.Top + 12),
                    new(bounds.Left + 18, bounds.Top + 4)]);
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
        MinimumSize = new Size(210, 34);
        Size = new Size(230, 34);
        Anchor = AnchorStyles.Top | AnchorStyles.Right;
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
        AccessibleName = BuildText();
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

        var dotSize = Math.Max(8, DeviceDpi / 12);
        var dotY = (Height - dotSize) / 2;
        using var dot = new SolidBrush(statusColor);
        e.Graphics.FillEllipse(dot, 13, dotY, dotSize, dotSize);
        var textRectangle = new Rectangle(28 + dotSize, 0, Width - 38 - dotSize, Height);
        TextRenderer.DrawText(
            e.Graphics,
            BuildText(),
            SystemFonts.MessageBoxFont!,
            textRectangle,
            AppTheme.Current.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    /// <summary>
    /// 刷新当前主题和语言下的徽标文字与颜色。
    /// </summary>
    public void ApplyAppearance()
    {
        AccessibleName = BuildText();
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
