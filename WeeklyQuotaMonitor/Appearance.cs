using Microsoft.Win32;

namespace WeeklyQuotaMonitor;

/// <summary>
/// 定义界面主题选择；系统模式读取 Windows 应用明暗设置。
/// </summary>
public enum UiTheme
{
    System,
    Light,
    Dark
}

/// <summary>
/// 集中保存窗口、卡片、边框、正文、次要文字和图表背景颜色。
/// </summary>
public sealed record ThemePalette(
    Color Window,
    Color Surface,
    Color SurfaceAlternate,
    Color Border,
    Color Text,
    Color MutedText,
    Color Accent,
    Color Positive,
    Color Warning,
    Color Grid,
    bool IsDark);

/// <summary>
/// 解析 Windows 主题并把统一颜色、字体和可访问样式应用到 WinForms 控件树。
/// </summary>
public static class AppTheme
{
    public static ThemePalette Current { get; private set; } = ResolvePalette(
        UiTheme.System,
        IsWindowsDarkMode());

    /// <summary>
    /// 切换当前主题并返回新调色板，供所有已打开窗口同步刷新。
    /// </summary>
    /// <param name="theme">系统、浅色或深色主题。</param>
    /// <returns>解析后的完整调色板。</returns>
    public static ThemePalette Set(UiTheme theme)
    {
        Current = ResolvePalette(theme, IsWindowsDarkMode());
        return Current;
    }

    /// <summary>
    /// 根据用户主题设置和给定的 Windows 明暗状态生成确定的调色板，供运行时和业务测试共用。
    /// </summary>
    /// <param name="theme">系统、浅色或深色主题。</param>
    /// <param name="windowsDarkMode">Windows 当前是否偏好深色应用。</param>
    /// <returns>完整的浅色或深色调色板。</returns>
    public static ThemePalette ResolvePalette(UiTheme theme, bool windowsDarkMode)
    {
        var dark = theme switch
        {
            UiTheme.System => windowsDarkMode,
            UiTheme.Light => false,
            UiTheme.Dark => true,
            _ => throw new InvalidDataException($"不支持的界面主题：{theme}。")
        };
        return dark
            ? new(
                Color.FromArgb(24, 27, 33),
                Color.FromArgb(34, 38, 46),
                Color.FromArgb(42, 47, 57),
                Color.FromArgb(68, 75, 88),
                Color.FromArgb(239, 242, 247),
                Color.FromArgb(165, 174, 190),
                Color.FromArgb(92, 164, 255),
                Color.FromArgb(74, 201, 139),
                Color.FromArgb(246, 184, 82),
                Color.FromArgb(62, 68, 79),
                true)
            : new(
                Color.FromArgb(244, 247, 251),
                Color.White,
                Color.FromArgb(248, 250, 253),
                Color.FromArgb(218, 224, 233),
                Color.FromArgb(31, 39, 51),
                Color.FromArgb(96, 106, 122),
                Color.FromArgb(32, 111, 211),
                Color.FromArgb(25, 143, 88),
                Color.FromArgb(190, 119, 18),
                Color.FromArgb(229, 233, 240),
                false);
    }

    /// <summary>
    /// 递归设置窗口及子控件的背景、前景、边框和表格样式。
    /// </summary>
    /// <param name="root">需要应用主题的根控件。</param>
    public static void Apply(Control root)
    {
        ApplyControl(root, Current);
        root.Invalidate(true);
    }

    /// <summary>
    /// 根据显式设置或 Windows AppsUseLightTheme 值生成调色板。
    /// </summary>
    /// <param name="theme">用户主题设置。</param>
    /// <returns>浅色或深色调色板。</returns>
    /// <summary>
    /// 读取 Windows 当前用户的应用明暗偏好；注册表默认值 1 表示浅色。
    /// </summary>
    /// <returns>Windows 配置为深色应用主题时返回 true。</returns>
    private static bool IsWindowsDarkMode()
    {
        var value = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme",
            1);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) == 0;
    }

    /// <summary>
    /// 为单个控件设置主题，并递归处理全部子控件。
    /// </summary>
    /// <param name="control">当前控件。</param>
    /// <param name="palette">需要应用的调色板。</param>
    private static void ApplyControl(Control control, ThemePalette palette)
    {
        control.Font = SystemFonts.MessageBoxFont;
        control.ForeColor = palette.Text;
        control.BackColor = control is Form or TabPage or TableLayoutPanel or FlowLayoutPanel
            ? palette.Window
            : palette.Surface;

        if (control is TextBoxBase or ComboBox or NumericUpDown)
        {
            control.BackColor = palette.SurfaceAlternate;
        }

        if (control is Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = palette.Border;
            button.BackColor = palette.SurfaceAlternate;
        }

        if (control is DataGridView grid)
        {
            grid.BackgroundColor = palette.Surface;
            grid.GridColor = palette.Grid;
            grid.DefaultCellStyle.BackColor = palette.Surface;
            grid.DefaultCellStyle.ForeColor = palette.Text;
            grid.DefaultCellStyle.SelectionBackColor = palette.Accent;
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.ColumnHeadersDefaultCellStyle.BackColor = palette.SurfaceAlternate;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = palette.Text;
            grid.EnableHeadersVisualStyles = false;
        }

        foreach (Control child in control.Controls)
        {
            ApplyControl(child, palette);
        }
    }
}
