using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor;

/// <summary>
/// 以原子替换方式读写用户设置和监控状态 JSON 文件。
/// </summary>
public sealed class JsonStorage
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 创建应用数据目录并读取设置；首次运行时写入默认设置。
    /// </summary>
    /// <returns>有效的应用设置。</returns>
    public AppSettings LoadSettings()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        if (!File.Exists(AppPaths.SettingsFile))
        {
            var defaults = new AppSettings();
            SaveSettings(defaults);
            return defaults;
        }

        var json = File.ReadAllText(AppPaths.SettingsFile);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, Options)
            ?? throw new InvalidDataException("settings.json 反序列化结果为空。");
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(nameof(AppSettings.SettingsSchemaVersion), out _))
        {
            settings.SettingsSchemaVersion = 0;
        }

        var modelPricesPresent = document.RootElement.TryGetProperty(nameof(AppSettings.ModelPrices), out _);
        if (AppSettingsMigration.Apply(settings, modelPricesPresent))
        {
            SaveSettings(settings);
        }

        return settings;
    }

    /// <summary>
    /// 验证并原子保存应用设置。
    /// </summary>
    /// <param name="settings">需要持久化的设置。</param>
    public void SaveSettings(AppSettings settings)
    {
        var errors = settings.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }

        AtomicWrite(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, Options));
    }

    /// <summary>
    /// 读取监控状态并恢复 Windows 路径和模型集合的大小写不敏感比较器。
    /// </summary>
    /// <returns>已有状态或首次运行的空状态。</returns>
    public MonitorState LoadState()
        => LoadState(PublicApiPricing.PricingVersion);

    /// <summary>
    /// 读取监控状态并按当前价格版本清理不能安全续接的待采样区间。
    /// </summary>
    /// <param name="pricingVersion">当前生效模型价格配置的稳定版本。</param>
    /// <returns>已恢复比较器且与当前价格兼容的监控状态。</returns>
    public MonitorState LoadState(string pricingVersion)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        if (!File.Exists(AppPaths.StateFile))
        {
            var newState = new MonitorState();
            RolloutLogReader.MigrateServiceTierTrackingState(newState);
            RolloutLogReader.MigrateResponseAssociationTrackingState(newState);
            return newState;
        }

        var state = JsonSerializer.Deserialize<MonitorState>(File.ReadAllText(AppPaths.StateFile), Options)
            ?? throw new InvalidDataException("state.json 反序列化结果为空。");
        state.FileCursors = new(state.FileCursors, StringComparer.OrdinalIgnoreCase);
        state.PendingModels = new(state.PendingModels, StringComparer.OrdinalIgnoreCase);
        state.PendingServiceTiers = new(state.PendingServiceTiers, StringComparer.OrdinalIgnoreCase);
        state.PendingCreditMultipliers = new(state.PendingCreditMultipliers, StringComparer.OrdinalIgnoreCase);
        state.HistoricalReplayUnresolvedFileLengths = new(
            state.HistoricalReplayUnresolvedFileLengths,
            StringComparer.OrdinalIgnoreCase);
        RolloutLogReader.MigrateServiceTierTrackingState(state);
        RolloutLogReader.MigrateResponseAssociationTrackingState(state);
        QuotaEstimator.ResetIncompatiblePendingInterval(state, pricingVersion);
        return state;
    }

    /// <summary>
    /// 原子保存文件游标、额度基线、累积量和采样点。
    /// </summary>
    /// <param name="state">需要持久化的监控状态。</param>
    public void SaveState(MonitorState state) =>
        AtomicWrite(AppPaths.StateFile, JsonSerializer.Serialize(state, Options));

    /// <summary>加载独立速度摘要；未知版本或损坏数据保留原错误，不覆盖为新空状态。</summary>
    public SpeedState LoadSpeedState()
    {
        if (!File.Exists(AppPaths.SpeedFile)) return new();
        var state = JsonSerializer.Deserialize<SpeedState>(File.ReadAllText(AppPaths.SpeedFile), Options)
            ?? throw new InvalidDataException("speed.json 反序列化结果为空。");
        if (state.Version != 1) throw new InvalidDataException("不支持的速度数据版本。");
        return state;
    }

    /// <summary>原子保存独立速度文件，频繁速度更新不重写体积更大的额度历史。</summary>
    public void SaveSpeedState(SpeedState state) => AtomicWrite(AppPaths.SpeedFile, JsonSerializer.Serialize(state, Options));

    /// <summary>
    /// 先写同目录临时文件再覆盖目标，避免进程退出留下半个 JSON。
    /// </summary>
    private static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, true);
    }
}

/// <summary>
/// 将协议状态和程序生命周期信息追加到本地运行日志，不写入对话正文或凭据。
/// </summary>
public static class RuntimeLog
{
    private static readonly object Gate = new();

    /// <summary>
    /// 追加一条带本地时间戳的单行运行信息。
    /// </summary>
    /// <param name="message">不包含凭据和对话正文的运行信息。</param>
    public static void Write(string message)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        lock (Gate)
        {
            File.AppendAllText(
                AppPaths.RuntimeLogFile,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}");
        }
    }
}

/// <summary>
/// 表示配置值解析为本次可执行路径后的结果；失败时保留可直接显示给用户的原因。
/// </summary>
public sealed record CodexExecutableResolution(
    bool Success,
    string ExecutablePath,
    string PersistedSetting,
    string Status);

/// <summary>
/// 将稳定定位符、旧版 WindowsApps 路径或显式命令解析为当前可启动的 codex.exe。
/// </summary>
public static partial class CodexExecutableResolver
{
    public const string DesktopPackageLocator = "codex-desktop://current";

    private const string PackageRepositoryKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    private const string PackageExecutableRelativePath = @"app\resources\codex.exe";

    /// <summary>
    /// 解析当前设置；旧 Codex Desktop 版本路径会迁移为稳定定位符，显式路径和 PATH 命令保持原语义。
    /// </summary>
    /// <param name="configuredValue">settings.json 中保存的 CodexExecutable 值。</param>
    /// <returns>成功时包含本次启动路径和应持久化设置；失败时包含状态说明。</returns>
    public static CodexExecutableResolution Resolve(string configuredValue)
    {
        var configured = configuredValue.Trim();
        if (string.Equals(configured, DesktopPackageLocator, StringComparison.OrdinalIgnoreCase) ||
            IsVersionedDesktopPackagePath(configured))
        {
            return ResolveDesktopPackage();
        }

        if (Path.IsPathFullyQualified(configured))
        {
            return File.Exists(configured)
                ? new(true, configured, configured, "已使用显式 Codex 路径。")
                : new(
                    false,
                    string.Empty,
                    configured,
                    $"配置的 Codex 可执行文件不存在：{configured}。请在设置中重新选择，或改用 {DesktopPackageLocator}。");
        }

        var pathExecutable = ResolveFromPath(configured);
        return pathExecutable is not null
            ? new(true, pathExecutable, configured, "已从 PATH 解析 Codex 命令。")
            : new(
                false,
                string.Empty,
                configured,
                $"无法从 PATH 解析 Codex 命令：{configured}。请在设置中重新选择，或改用 {DesktopPackageLocator}。");
    }

    /// <summary>
    /// 判断配置是否是 Codex Desktop 更新后会失效的 WindowsApps 版本化绝对路径。
    /// </summary>
    /// <param name="path">待判断的设置值。</param>
    /// <returns>属于 Codex Desktop 包内 codex.exe 时返回 true。</returns>
    private static bool IsVersionedDesktopPackagePath(string path)
    {
        var normalized = path.Replace('/', '\\');
        return normalized.Contains(
                   @"\WindowsApps\OpenAI.Codex_",
                   StringComparison.OrdinalIgnoreCase) &&
               normalized.EndsWith(
                   @"\app\resources\codex.exe",
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从当前用户 AppModel 包仓库选择版本最高且文件存在的 Codex Desktop 包。
    /// </summary>
    /// <returns>当前包的可执行路径；找不到有效包时返回失败原因。</returns>
    private static CodexExecutableResolution ResolveDesktopPackage()
    {
        using var packages = Registry.CurrentUser.OpenSubKey(PackageRepositoryKey, writable: false);
        if (packages is null)
        {
            return new(
                false,
                string.Empty,
                DesktopPackageLocator,
                "无法读取当前用户的 Codex Desktop 包注册信息；监控将保持运行，请稍后重试或在设置中选择 codex.exe。");
        }

        var candidates = new List<DesktopPackageCandidate>();
        foreach (var packageName in packages.GetSubKeyNames())
        {
            var match = DesktopPackageNamePattern().Match(packageName);
            if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var version))
            {
                continue;
            }

            using var package = packages.OpenSubKey(packageName, writable: false);
            var root = package?.GetValue("PackageRootFolder") as string;
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var executable = Path.Combine(root, PackageExecutableRelativePath);
            if (File.Exists(executable))
            {
                candidates.Add(new(version, executable));
            }
        }

        var current = candidates.MaxBy(candidate => candidate.Version);
        return current is null
            ? new(
                false,
                string.Empty,
                DesktopPackageLocator,
                "当前用户没有可用的 Codex Desktop 包；监控将保持运行，请安装或启动 Codex 后再检查设置。")
            : PrepareDesktopRuntime(current);
    }

    /// <summary>
    /// 把受 WindowsApps 执行 ACL 保护的包内 CLI 同步为当前用户可执行的版本化运行副本。
    /// </summary>
    /// <param name="package">已确认存在的当前 Codex Desktop 包。</param>
    /// <returns>可启动的本地运行副本；同步失败时返回可见错误状态。</returns>
    private static CodexExecutableResolution PrepareDesktopRuntime(DesktopPackageCandidate package)
    {
        var runtimeDirectory = Path.Combine(AppPaths.DataDirectory, "codex-runtime");
        var runtimeExecutable = Path.Combine(runtimeDirectory, "codex.exe");
        var versionFile = Path.Combine(runtimeDirectory, "package-version.txt");
        var expectedVersion = package.Version.ToString();
        var cacheIsCurrent = File.Exists(runtimeExecutable) &&
                             File.Exists(versionFile) &&
                             string.Equals(
                                 File.ReadAllText(versionFile).Trim(),
                                 expectedVersion,
                                 StringComparison.Ordinal) &&
                             new FileInfo(runtimeExecutable).Length ==
                             new FileInfo(package.ExecutablePath).Length;
        if (!cacheIsCurrent)
        {
            Directory.CreateDirectory(runtimeDirectory);
            var stagedExecutable = runtimeExecutable + ".installing";
            var stagedVersion = versionFile + ".tmp";
            try
            {
                File.Copy(package.ExecutablePath, stagedExecutable, overwrite: true);
                File.Move(stagedExecutable, runtimeExecutable, overwrite: true);
                File.WriteAllText(stagedVersion, expectedVersion);
                File.Move(stagedVersion, versionFile, overwrite: true);
            }
            catch (IOException exception)
            {
                return new(
                    false,
                    string.Empty,
                    DesktopPackageLocator,
                    $"同步 Codex Desktop {expectedVersion} 本地运行副本失败：{exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                return new(
                    false,
                    string.Empty,
                    DesktopPackageLocator,
                    $"同步 Codex Desktop {expectedVersion} 本地运行副本被拒绝：{exception.Message}");
            }
        }

        return new(
            true,
            runtimeExecutable,
            DesktopPackageLocator,
            $"已同步并使用 Codex Desktop {expectedVersion} 本地运行副本。");
    }

    /// <summary>
    /// 按当前进程 PATH 的 Windows 命令查找规则解析非绝对 Codex 命令。
    /// </summary>
    /// <param name="command">不含目录或使用相对目录的命令。</param>
    /// <returns>找到时返回绝对路径，否则返回 null。</returns>
    private static string? ResolveFromPath(string command)
    {
        if (File.Exists(command))
        {
            return Path.GetFullPath(command);
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        var names = Path.HasExtension(command) ? new[] { command } : new[] { command + ".exe", command };
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var cleanDirectory = directory.Trim().Trim('"');
            foreach (var name in names)
            {
                var candidate = Path.Combine(cleanDirectory, name);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 匹配 Codex Desktop 当前用户包全名并提取四段版本号。
    /// </summary>
    [GeneratedRegex(@"^OpenAI\.Codex_(?<version>\d+(?:\.\d+){3})_(?:x64|arm64)__2p2nqsd0c76g0$", RegexOptions.IgnoreCase)]
    private static partial Regex DesktopPackageNamePattern();

    /// <summary>
    /// 保存可用于版本排序的已安装 Codex Desktop 包候选。
    /// </summary>
    private sealed record DesktopPackageCandidate(Version Version, string ExecutablePath);
}

/// <summary>
/// 使用当前用户 Run 注册表项管理无管理员权限的 Windows 开机自启。
/// </summary>
public static class AutostartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexWeeklyQuotaMonitor";

    /// <summary>
    /// 根据设置创建或删除当前用户开机自启项。
    /// </summary>
    /// <param name="enabled">true 表示启用，false 表示禁用。</param>
    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户 Run 注册表项。");
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --autostart", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// 判断当前用户 Run 注册表项是否包含本程序。
    /// </summary>
    /// <returns>已配置开机自启时返回 true。</returns>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false)
            ?? throw new InvalidOperationException("无法读取当前用户 Run 注册表项。");
        return key.GetValue(ValueName) is string;
    }
}
