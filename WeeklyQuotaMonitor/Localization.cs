using System.Globalization;
using System.Resources;
using System.Runtime.InteropServices;

namespace WeeklyQuotaMonitor;

/// <summary>
/// 定义用户可选择的界面语言；自动模式按 Windows 当前 UI 语言选择简体中文或英文。
/// </summary>
public enum UiLanguage
{
    Auto,
    ChineseSimplified,
    English
}

/// <summary>
/// 通过嵌入式 resx 资源提供完整的中英文界面文本和区域格式。
/// </summary>
public static class UiText
{
    private static readonly ResourceManager ResourceManager = new(
        "WeeklyQuotaMonitor.Resources.Strings",
        typeof(UiText).Assembly);

    /// <summary>
    /// 读取当前 Windows 用户配置的显示语言 LANGID，不受进程内手动切换影响。
    /// </summary>
    /// <returns>Windows 当前用户 UI 语言标识。</returns>
    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    public static CultureInfo Culture { get; private set; } = ResolveCulture(
        UiLanguage.Auto,
        ReadWindowsUiCulture());

    /// <summary>
    /// 切换当前界面语言；后续控件刷新和数字格式化立即使用新区域。
    /// </summary>
    /// <param name="language">自动、简体中文或英文。</param>
    public static void SetLanguage(UiLanguage language)
    {
        Culture = ResolveCulture(language, ReadWindowsUiCulture());
        CultureInfo.CurrentUICulture = Culture;
    }

    /// <summary>
    /// 依据用户设置和给定的系统 UI 区域解析实际语言，供启动逻辑和业务测试共用。
    /// </summary>
    /// <param name="language">自动、简体中文或英文。</param>
    /// <param name="windowsUiCulture">Windows 当前用户 UI 区域。</param>
    /// <returns>中文系统下的 zh-CN，或其他系统下的 en-US；显式语言始终覆盖系统值。</returns>
    public static CultureInfo ResolveCulture(UiLanguage language, CultureInfo windowsUiCulture)
    {
        if (language == UiLanguage.Auto)
        {
            return windowsUiCulture.TwoLetterISOLanguageName == "zh"
                ? CultureInfo.GetCultureInfo("zh-CN")
                : CultureInfo.GetCultureInfo("en-US");
        }

        return language switch
        {
            UiLanguage.ChineseSimplified => CultureInfo.GetCultureInfo("zh-CN"),
            UiLanguage.English => CultureInfo.GetCultureInfo("en-US"),
            _ => throw new InvalidDataException($"Unsupported UI language: {language}.")
        };
    }

    /// <summary>
    /// 返回指定资源键在当前界面语言中的文本；缺失键视为发布错误并立即失败。
    /// </summary>
    /// <param name="key">Strings.resx 中的资源键。</param>
    /// <returns>当前语言的非空文本。</returns>
    public static string Get(string key) =>
        ResourceManager.GetString(key, Culture) ??
        throw new InvalidDataException($"Missing localized resource: {key} ({Culture.Name}).");

    /// <summary>
    /// 使用当前界面区域格式化带占位符的资源文本。
    /// </summary>
    /// <param name="key">Strings.resx 中的资源键。</param>
    /// <param name="arguments">格式化占位符参数。</param>
    /// <returns>区域化后的完整文本。</returns>
    public static string Format(string key, params object?[] arguments) =>
        string.Format(Culture, Get(key), arguments);

    /// <summary>
    /// 根据设置和 Windows UI 语言解析程序实际使用的 CultureInfo。
    /// </summary>
    /// <param name="language">用户语言设置。</param>
    /// <returns>zh-CN 或 en-US。</returns>
    private static CultureInfo ReadWindowsUiCulture()
    {
        return CultureInfo.GetCultureInfo(GetUserDefaultUILanguage());
    }
}
