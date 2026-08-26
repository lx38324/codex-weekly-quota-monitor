using System.Text;
using System.Text.Json;
using WeeklyQuotaMonitor;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor.BusinessTests;

/// <summary>
/// 使用可执行的业务场景验证 Standard API 等价计价、重置、回归和增量日志鲁棒性。
/// </summary>
internal static class Program
{
    /// <summary>
    /// 顺序执行全部业务测试；任一断言失败时以异常和非零退出码暴露问题。
    /// </summary>
    [STAThread]
    private static void Main(string[] arguments)
    {
        UiText.SetLanguage(UiLanguage.ChineseSimplified);
        AppTheme.Set(UiTheme.Light);
        if (arguments.Length > 0)
        {
            if (arguments.Length == 2 && string.Equals(arguments[0], "--capture-dashboard", StringComparison.Ordinal))
            {
                CaptureDashboard(arguments[1]);
                return;
            }

            throw new InvalidDataException("仅支持 --capture-dashboard <png-path> 预览参数。");
        }

        var tests = new Action[]
        {
            TestAutomaticWindowsLanguageSelection,
            TestManualLanguagePersistsAndOverridesWindows,
            TestSettingsSchema2AddsAppearancePreferences,
            TestThemeSelectionRespectsManualOverride,
            TestDashboardLayoutAtSupportedDpiScales,
            TestDashboardFitsReportedViewport,
            TestShortContextStandardPricing,
            TestLongContextStandardPricing,
            TestLongContextOfficialSurchargeCombinesWithFast,
            TestGpt56FastCreditMultiplier,
            TestGpt54FastCreditMultiplier,
            TestUnsupportedFastMultiplierFailsClosed,
            TestObservedGpt52StandardPricing,
            TestAutoReviewUsesLunaPricingEndToEnd,
            TestWeeklyWindowSelection,
            TestQuotaEstimationPersistsAmountDefinition,
            TestConfirmedResetRecoversCurrentWindowSample,
            TestResetFutureStartClockSkewDoesNotCrashHistory,
            TestEarlyZeroResetIsConfirmed,
            TestEarlyZeroResetRecoversFromPersistedCheckpoint,
            TestUnfilteredResetDoesNotCreateSample,
            TestUnconfirmedResetDriftUsesIncrementalPercent,
            TestResetRequiresCrossingPreviousDeadline,
            TestPercentDecreaseWithoutResetIsConservative,
            TestBucketSwitchClearsPending,
            TestMalformedIntervalIsRejected,
            TestRegressionExcludesLegacyPricing,
            TestRolloutPriorityResponseAssociation,
            TestIntermediateEventsPreserveResponseAssociation,
            TestTokenHeartbeatPreservesResponseAssociation,
            TestThreadSettingsAppliedSwitchesTier,
            TestUnknownTierFailsClosedAfterCursorMigration,
            TestResponseAssociationMigrationClearsStalePending,
            TestHistoricalFactsExtractRateLimitAndResponse,
            TestHistoricalFactsBackfillConsistentTier,
            TestHistoricalFactsDoNotBackfillMixedTier,
            TestHistoricalFactsWithoutTierRemainUnpriced,
            TestDelayedTierAppendTriggersRecoverableReplay,
            TestAuthoritativeCheckpointPreservesFirstObservation,
            TestHistoricalReplayExcludesResponsesAfterFirstObservation,
            TestHistoricalReplayRejectsSlidingZeroAndBuildsSamples,
            TestHistoricalReplayApplyIsIdempotent,
            TestMalformedLineDoesNotBlockLaterResponse,
            TestPartialLineWaitsForNewline,
            TestTruncatedFileRestartsCursor,
            TestDeletedFileCursorIsPruned,
            TestTrayIconOpensOnlyOnLeftDoubleClick,
            TestDetailWindowCanReopenFromMenuAfterUserClose,
            TestChartGridUsesLocalSampleTime,
            TestLegacyRegressionLimitMigrationRestoresCurve,
            TestDashboardTabsSeriesAndEstimate
        };

        foreach (var test in tests)
        {
            Console.WriteLine($"执行：{test.Method.Name}");
            test();
        }

        Console.WriteLine($"业务测试通过：{tests.Length}/{tests.Length}。");
    }

    /// <summary>
    /// 验证自动语言在中文 Windows 上选择简体中文，在其他系统语言上选择英文。
    /// </summary>
    private static void TestAutomaticWindowsLanguageSelection()
    {
        Equal(
            "zh-CN",
            UiText.ResolveCulture(UiLanguage.Auto, System.Globalization.CultureInfo.GetCultureInfo("zh-TW")).Name,
            "自动语言应将中文 Windows 映射为简体中文界面");
        Equal(
            "en-US",
            UiText.ResolveCulture(UiLanguage.Auto, System.Globalization.CultureInfo.GetCultureInfo("de-DE")).Name,
            "自动语言应将非中文 Windows 映射为英文界面");
    }

    /// <summary>
    /// 验证手动中文或英文覆盖 Windows 语言，保存和反序列化后仍保留，并可切回自动。
    /// </summary>
    private static void TestManualLanguagePersistsAndOverridesWindows()
    {
        Equal(
            "en-US",
            UiText.ResolveCulture(
                UiLanguage.English,
                System.Globalization.CultureInfo.GetCultureInfo("zh-CN")).Name,
            "手动英文应覆盖中文 Windows");
        Equal(
            "zh-CN",
            UiText.ResolveCulture(
                UiLanguage.ChineseSimplified,
                System.Globalization.CultureInfo.GetCultureInfo("en-US")).Name,
            "手动中文应覆盖英文 Windows");

        var json = JsonSerializer.Serialize(new AppSettings { Language = UiLanguage.English });
        var restored = JsonSerializer.Deserialize<AppSettings>(json)
            ?? throw new InvalidOperationException("语言设置反序列化结果为空。");
        Equal(UiLanguage.English, restored.Language, "手动语言应持久化");
        restored.Language = UiLanguage.Auto;
        Equal(
            "zh-CN",
            UiText.ResolveCulture(
                restored.Language,
                System.Globalization.CultureInfo.GetCultureInfo("zh-CN")).Name,
            "切回自动后应重新读取系统语言");

        UiText.SetLanguage(UiLanguage.English);
        using (var englishForm = new ChartForm(restored, _ => { }))
        {
            Equal(
                true,
                FindControls<SidebarNavigationButton>(englishForm).Any(button => button.Text == "Dashboard"),
                "手动英文后侧栏应显示英文导航");
        }

        UiText.SetLanguage(UiLanguage.ChineseSimplified);
    }

    /// <summary>
    /// 验证 v1.2 的 schema 2 设置升级后获得自动语言和系统主题默认值，同时保留已有回归上限。
    /// </summary>
    private static void TestSettingsSchema2AddsAppearancePreferences()
    {
        var settings = new AppSettings
        {
            SettingsSchemaVersion = 2,
            Regression = new RegressionOptions
            {
                MaximumSampleUsd = AppSettingsMigration.DefaultMaximumSampleUsd
            }
        };
        var changed = AppSettingsMigration.Apply(settings);
        Equal(true, changed, "schema 2 应迁移到当前版本");
        Equal(3, settings.SettingsSchemaVersion, "迁移后设置版本");
        Equal(UiLanguage.Auto, settings.Language, "旧设置默认使用自动系统语言");
        Equal(UiTheme.System, settings.Theme, "旧设置默认使用系统主题");
        Equal(
            AppSettingsMigration.DefaultMaximumSampleUsd,
            settings.Regression.MaximumSampleUsd,
            "schema 2 已迁移上限不得再次改变");
    }

    /// <summary>
    /// 验证系统主题跟随 Windows，而手动浅色和深色始终覆盖系统偏好。
    /// </summary>
    private static void TestThemeSelectionRespectsManualOverride()
    {
        Equal(true, AppTheme.ResolvePalette(UiTheme.System, true).IsDark, "系统主题应跟随 Windows 深色");
        Equal(false, AppTheme.ResolvePalette(UiTheme.System, false).IsDark, "系统主题应跟随 Windows 浅色");
        Equal(false, AppTheme.ResolvePalette(UiTheme.Light, true).IsDark, "手动浅色应覆盖 Windows 深色");
        Equal(true, AppTheme.ResolvePalette(UiTheme.Dark, false).IsDark, "手动深色应覆盖 Windows 浅色");
    }

    /// <summary>
    /// 验证主窗口在 100%、125%、150% 和 200% 缩放后，侧栏与四个业务页面仍可切换和访问。
    /// </summary>
    private static void TestDashboardLayoutAtSupportedDpiScales()
    {
        foreach (var scale in new[] { 1F, 1.25F, 1.5F, 2F })
        {
            using var form = new ChartForm(new AppSettings(), _ => { });
            form.Scale(new SizeF(scale, scale));
            form.PerformLayout();
            Equal(1, FindControls<ApplicationSidebar>(form).Count(), $"{scale:P0} 缩放下应保留唯一科技侧栏");
            Equal(4, FindControls<SidebarNavigationButton>(form).Count(), $"{scale:P0} 缩放下应保留四个业务入口");
            form.ShowDashboardTab();
            Equal(DashboardSection.Dashboard, form.SelectedSection, $"{scale:P0} 缩放下总览可达");
            form.ShowChartTab();
            Equal(DashboardSection.History, form.SelectedSection, $"{scale:P0} 缩放下历史可达");
            form.ShowSettingsTab(new AppSettings());
            Equal(DashboardSection.Settings, form.SelectedSection, $"{scale:P0} 缩放下设置可达");
            form.ShowDiagnosticsTab();
            Equal(DashboardSection.Diagnostics, form.SelectedSection, $"{scale:P0} 缩放下诊断可达");
            form.Hide();
        }
    }

    /// <summary>
    /// 验证用户截图对应的 1160×730 客户区内六张指标卡和连接徽标完整可见，且卡片文字保持透明背景和大号主值。
    /// </summary>
    private static void TestDashboardFitsReportedViewport()
    {
        using var form = new ChartForm(new AppSettings(), _ => { });
        form.ClientSize = new Size(1160, 730);
        form.UpdateView(CreateDashboardPreviewView(), new RegressionOptions { Mode = RegressionMode.GaussianAggregation });
        form.ShowDashboardTab();
        Application.DoEvents();

        var cardGrid = FindControls<DashboardCardGrid>(form).Single();
        var visibleGrid = cardGrid.RectangleToScreen(cardGrid.ClientRectangle);
        var cards = FindControls<MetricCard>(cardGrid).ToArray();
        Equal(6, cards.Length, "总览应只显示六张核心指标卡");
        foreach (var card in cards)
        {
            var cardRectangle = card.RectangleToScreen(card.ClientRectangle);
            Equal(true, visibleGrid.Contains(cardRectangle), $"指标卡 {card.AccessibleName} 不得被卡片工作区裁切");
            Equal(
                true,
                FindControls<Label>(card).All(label => label.BackColor == Color.Transparent),
                $"指标卡 {card.AccessibleName} 的文字不得出现白色贴片背景");
            Equal(
                true,
                FindControls<Label>(card).Any(label => label.Font.Size >= 20F),
                $"指标卡 {card.AccessibleName} 应保留醒目的大号主值");
        }

        var badge = FindControls<ConnectionBadge>(form).Single();
        var visibleForm = form.RectangleToScreen(form.ClientRectangle);
        Equal(true, visibleForm.Contains(badge.RectangleToScreen(badge.ClientRectangle)), "连接状态徽标不得被裁切");
        form.Hide();
    }

    /// <summary>
    /// 生成与用户截图数据规模相近的总览快照，供布局业务测试和人工视觉预览共用。
    /// </summary>
    /// <returns>包含双口径额度、3% 已用、24 个回归点和归档统计的展示快照。</returns>
    private static MonitorViewSnapshot CreateDashboardPreviewView()
    {
        var timestamp = new DateTimeOffset(2026, 9, 1, 12, 34, 56, TimeSpan.FromHours(8));
        var baseCurve = Enumerable.Range(0, 24)
            .Select(index => new CurvePoint(timestamp.AddMinutes(index), 1800m + index * 3m))
            .ToArray();
        var officialCurve = Enumerable.Range(0, 24)
            .Select(index => new CurvePoint(timestamp.AddMinutes(index), 2250m + index * 3m))
            .ToArray();
        return new MonitorViewSnapshot(
            timestamp,
            "preview",
            new RateLimitSnapshot(timestamp, "codex", "Codex", 3m, 10080, timestamp.AddDays(1).AddHours(19).AddMinutes(47)),
            1873.68m,
            24,
            0,
            0,
            [],
            baseCurve)
        {
            OfficialLongContextEstimatedWeeklyQuotaUsd = 2317.14m,
            OfficialLongContextRegressionCurve = officialCurve,
            ArchivedSampleCount = 6,
            AppServerConnected = true
        };
    }

    /// <summary>
    /// 在真实 WinForms 句柄和当前 Windows DPI 下渲染总览页 PNG，供发布前人工检查布局与视觉层级。
    /// </summary>
    /// <param name="outputPath">需要写入的 PNG 绝对或相对路径。</param>
    private static void CaptureDashboard(string outputPath)
    {
        using var form = new ChartForm(new AppSettings(), _ => { });
        form.ClientSize = new Size(1160, 730);
        form.UpdateView(CreateDashboardPreviewView(), new RegressionOptions { Mode = RegressionMode.GaussianAggregation });
        form.ShowDashboardTab();
        Application.DoEvents();

        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        var absolutePath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        bitmap.Save(absolutePath, System.Drawing.Imaging.ImageFormat.Png);
        form.Hide();
        Console.WriteLine($"总览预览已写入：{absolutePath}");
    }

    /// <summary>
    /// 验证托盘单击不打开窗口，只有左键双击才允许打开完整图表窗。
    /// </summary>
    private static void TestTrayIconOpensOnlyOnLeftDoubleClick()
    {
        Equal(
            false,
            TrayApplicationContext.ShouldOpenChartFromTray(MouseButtons.Left, 1),
            "左键单击不得打开任何窗口");
        Equal(
            true,
            TrayApplicationContext.ShouldOpenChartFromTray(MouseButtons.Left, 2),
            "左键双击应打开完整图表窗");
        Equal(
            false,
            TrayApplicationContext.ShouldOpenChartFromTray(MouseButtons.Right, 2),
            "右键双击不得绕过右键菜单打开窗口");
    }

    /// <summary>
    /// 验证用户关闭详情窗后对象只会隐藏，右键菜单下一次打开仍复用同一实例。
    /// </summary>
    private static void TestDetailWindowCanReopenFromMenuAfterUserClose()
    {
        using var form = new DetailForm();
        form.Show();
        form.Close();

        Equal(false, form.IsDisposed, "用户关闭详情窗不得释放托盘持有的实例");
        Equal(false, form.Visible, "用户关闭详情窗后应隐藏窗口");

        form.ShowNearCursor();
        Equal(true, form.Visible, "详情窗关闭后应能由托盘再次显示");
        form.Hide();
    }

    /// <summary>
    /// 验证采样表格把带时区的持久化时间转换为与曲线横轴一致的系统本地时间。
    /// </summary>
    private static void TestChartGridUsesLocalSampleTime()
    {
        var timestamp = new DateTimeOffset(2026, 8, 24, 1, 2, 3, TimeSpan.FromHours(-4));
        var sample = new QuotaSample(
            timestamp,
            "codex",
            5m,
            1m,
            0.5m,
            50m,
            new TokenUsage(1_000, 100, 0, 100, 0),
            1,
            "gpt-5.6-sol")
        {
            OfficialLongContextIntervalApiEquivalentUsd = 0.5m,
            OfficialLongContextEstimatedWeeklyQuotaUsd = 50m,
            PricingVersion = PublicApiPricing.PricingVersion,
            ServiceTiers = "standard",
            CreditMultipliers = "1x"
        };
        var view = new MonitorViewSnapshot(
            timestamp,
            "测试",
            null,
            50m,
            1,
            0,
            0,
            [sample],
            [new CurvePoint(timestamp, 50m)])
        {
            OfficialLongContextEstimatedWeeklyQuotaUsd = 50m,
            OfficialLongContextRegressionCurve = [new CurvePoint(timestamp, 50m)]
        };

        var settings = new AppSettings
        {
            Regression = new RegressionOptions { Mode = RegressionMode.Linear }
        };
        using var form = new ChartForm(settings, _ => { });
        form.UpdateView(view, settings.Regression);
        var grid = FindControls<DataGridView>(form).Single();
        var timestampCell = grid.Rows[0].Cells[0];
        if (timestampCell.FormattedValue is not string formattedTimestamp)
        {
            throw new InvalidOperationException("表格时间单元格没有产生字符串显示值。");
        }

        Equal(timestamp.LocalDateTime, (DateTime)timestampCell.Value!, "表格时间值应转换为系统本地时间");
        Equal(
            timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
            formattedTimestamp,
            "表格时间文本应与曲线本地时间口径一致");
    }

    /// <summary>
    /// 验证旧版 1000 美元默认上限迁移后，截图中的两千美元级样本能够生成回归曲线。
    /// </summary>
    private static void TestLegacyRegressionLimitMigrationRestoresCurve()
    {
        var settings = new AppSettings
        {
            SettingsSchemaVersion = 0,
            Regression = new RegressionOptions
            {
                Mode = RegressionMode.GaussianAggregation,
                MaximumSampleUsd = AppSettingsMigration.LegacyMaximumSampleUsd
            }
        };
        var changed = AppSettingsMigration.Apply(settings);
        var timestamp = new DateTimeOffset(2026, 8, 24, 16, 40, 0, TimeSpan.FromHours(8));
        var sample = new QuotaSample(
            timestamp,
            "codex",
            16m,
            1m,
            21m,
            2100m,
            new TokenUsage(36_000_000, 35_000_000, 0, 100_000, 0),
            1,
            "gpt-5.6-sol")
        {
            AmountDefinition = PublicApiPricing.AmountDefinition,
            OfficialLongContextIntervalApiEquivalentUsd = 26m,
            OfficialLongContextEstimatedWeeklyQuotaUsd = 2600m,
            OfficialLongContextAmountDefinition = PublicApiPricing.OfficialLongContextAmountDefinition,
            PricingVersion = PublicApiPricing.PricingVersion,
            ServiceTiers = "standard",
            CreditMultipliers = "1x"
        };

        var curve = RegressionCalculator.BuildCurve([sample], settings.Regression);
        Equal(true, changed, "旧默认回归上限应执行设置迁移");
        Equal(AppSettingsMigration.CurrentVersion, settings.SettingsSchemaVersion, "设置迁移版本");
        Equal(AppSettingsMigration.DefaultMaximumSampleUsd, settings.Regression.MaximumSampleUsd, "迁移后的样本金额上限");
        Equal(1, curve.Count, "两千美元级有效样本应重新进入回归曲线");
        Near(2100m, RegressionCalculator.CurrentEstimate(curve)!.Value, 0.000001m, "迁移后的当前回归额度");
    }

    /// <summary>
    /// 验证统一主窗口可切换图表与设置、四条系列可选，并在图外显示当前回归额度。
    /// </summary>
    private static void TestDashboardTabsSeriesAndEstimate()
    {
        var settings = new AppSettings
        {
            Regression = new RegressionOptions
            {
                Mode = RegressionMode.Linear,
                MaximumSampleUsd = AppSettingsMigration.DefaultMaximumSampleUsd
            }
        };
        var timestamp = new DateTimeOffset(2026, 8, 24, 16, 40, 0, TimeSpan.FromHours(8));
        var sample = new QuotaSample(
            timestamp,
            "codex",
            16m,
            1m,
            21m,
            2100m,
            new TokenUsage(36_000_000, 35_000_000, 0, 100_000, 0),
            1,
            "gpt-5.6-sol")
        {
            OfficialLongContextIntervalApiEquivalentUsd = 26m,
            OfficialLongContextEstimatedWeeklyQuotaUsd = 2600m,
            PricingVersion = PublicApiPricing.PricingVersion,
            ServiceTiers = "standard",
            CreditMultipliers = "1x"
        };
        var view = new MonitorViewSnapshot(
            timestamp,
            "测试",
            new RateLimitSnapshot(timestamp, "codex", "Codex", 18m, 10080, timestamp.AddDays(7)),
            2100m,
            1,
            0,
            0,
            [sample],
            [new CurvePoint(timestamp, 2100m)])
        {
            OfficialLongContextEstimatedWeeklyQuotaUsd = 2600m,
            OfficialLongContextRegressionCurve = [new CurvePoint(timestamp, 2600m)],
            HistoricalReplayUnattributedUsedPercents = [17m]
        };

        using var form = new ChartForm(settings, _ => { });
        form.UpdateView(view, settings.Regression);
        form.ShowChartTab();
        Equal(DashboardSection.History, form.SelectedSection, "托盘图表入口应直达历史与图表标签页");

        var estimate = FindControls<Label>(form)
            .Single(label => label.Name == "RegressionEstimateLabel");
        Equal(true, estimate.Text.Contains("基础 约 $2,100.00", StringComparison.Ordinal), "图外应显示基础当前回归额度");
        Equal(true, estimate.Text.Contains("官方 >272K 约 $2,600.00", StringComparison.Ordinal), "图外应显示官方长上下文当前回归额度");
        var summary = FindControls<Label>(form)
            .Single(label => label.Name == "HistorySummaryLabel");
        Equal(true, summary.Text.Contains("权威已用 18.00%", StringComparison.Ordinal), "图表外应显示当前权威百分比");
        Equal(true, summary.Text.Contains("最新有效金额点 16.00%", StringComparison.Ordinal), "图表外应区分最新有效金额点");
        Equal(true, summary.Text.Contains("待归因点 17.00%", StringComparison.Ordinal), "图表外应显示待恢复额度点");

        var checkBoxes = FindControls<CheckBox>(form).ToArray();
        Equal(4, checkBoxes.Count(checkBox => checkBox.Text.Contains("样本折线", StringComparison.Ordinal) ||
                                                 checkBox.Text.Contains("回归曲线", StringComparison.Ordinal)),
            "图表应提供四个系列显隐选项");
        var baseRegression = checkBoxes.Single(checkBox => checkBox.Text == "基础回归曲线");
        baseRegression.Checked = false;
        var chart = FindControls<QuotaChartControl>(form).Single();
        Equal(false, chart.SeriesVisibility.ShowBaseRegression, "取消基础回归后绘图控件应隐藏该曲线");

        form.ShowSettingsTab(settings);
        Equal(DashboardSection.Settings, form.SelectedSection, "托盘设置入口应直达设置标签页");
        Equal(1, FindControls<SettingsPanel>(form).Count(), "主窗口应内嵌唯一设置面板");
        form.ShowDashboardTab();
        Equal(DashboardSection.Dashboard, form.SelectedSection, "托盘总览入口应直达总览标签页");
        form.ShowDiagnosticsTab();
        Equal(DashboardSection.Diagnostics, form.SelectedSection, "托盘诊断入口应直达诊断标签页");
        Equal(1, FindControls<ApplicationSidebar>(form).Count(), "主窗口应有唯一科技侧栏");
        Equal(4, FindControls<SidebarNavigationButton>(form).Count(), "科技侧栏应提供四个一级业务入口");
        form.Hide();
    }

    /// <summary>
    /// 验证 Sol 短上下文按当前 Standard 价分别计算未缓存、缓存读、缓存写和输出。
    /// </summary>
    private static void TestShortContextStandardPricing()
    {
        var usage = new TokenUsage(100_000, 50_000, 10_000, 10_000, 2_000);
        var result = PublicApiPricing.Calculate("gpt-5.6-sol", "standard", usage);
        Equal(true, result.Success, "Sol 短上下文应可定价");
        Near(0.43m, result.StandardCostUsd, 0.000001m, "Sol Standard 短上下文金额");
        Near(0.43m, result.CostUsd, 0.000001m, "Standard 等价金额");
        Near(0.43m, result.OfficialLongContextCostUsd, 0.000001m, "短上下文两种口径应一致");
        Near(1m, result.CreditMultiplier, 0.000001m, "Standard 额度倍率");
    }

    /// <summary>
    /// 验证输入超过 272K 后整次请求使用当前 Sol Standard 长上下文价格。
    /// </summary>
    private static void TestLongContextStandardPricing()
    {
        var usage = new TokenUsage(300_000, 200_000, 50_000, 10_000, 2_000);
        var result = PublicApiPricing.Calculate("gpt-5.6-sol", "default", usage);
        Equal(true, result.Success, "Sol 长上下文应可定价");
        Near(0.73m, result.CostUsd, 0.000001m, "Sol 不含长上下文加价金额");
        Near(1.36m, result.OfficialLongContextCostUsd, 0.000001m, "Sol 官方长上下文加价金额");
        Equal(true, result.IsLongContext, "超过 272K 应标记为长上下文");
    }

    /// <summary>
    /// 验证官方长上下文加价与 GPT-5.6 Fast 额度倍率分别应用且不会互相替代。
    /// </summary>
    private static void TestLongContextOfficialSurchargeCombinesWithFast()
    {
        var usage = new TokenUsage(300_000, 200_000, 50_000, 10_000, 2_000);
        var result = PublicApiPricing.Calculate("gpt-5.6-sol", "priority", usage);
        Equal(true, result.Success, "GPT-5.6 Fast 长上下文应可定价");
        Near(1.825m, result.CostUsd, 0.000001m, "无长上下文加价金额仍应应用 2.5 倍 Fast 倍率");
        Near(3.4m, result.OfficialLongContextCostUsd, 0.000001m, "官方长上下文加价后再应用 2.5 倍 Fast 倍率");
        Near(2.5m, result.CreditMultiplier, 0.000001m, "Fast 额度倍率");
    }

    /// <summary>
    /// 验证 GPT-5.6 priority 使用 Standard API 成本乘以 2.5 倍 ChatGPT 额度倍率。
    /// </summary>
    private static void TestGpt56FastCreditMultiplier()
    {
        var usage = new TokenUsage(100_000, 50_000, 10_000, 10_000, 2_000);
        var result = PublicApiPricing.Calculate("gpt-5.6-sol", "priority", usage);
        Equal(true, result.Success, "GPT-5.6 Fast 应可定价");
        Near(0.43m, result.StandardCostUsd, 0.000001m, "Fast 响应的 Standard API 成本");
        Near(2.5m, result.CreditMultiplier, 0.000001m, "GPT-5.6 Fast 额度倍率");
        Near(1.075m, result.CostUsd, 0.000001m, "GPT-5.6 Standard API 等价金额");
        Equal("fast", result.NormalizedServiceTier, "priority 应规范化为 fast");
    }

    /// <summary>
    /// 验证 GPT-5.4 Fast 使用官方 2 倍额度倍率，而不是统一套用 2.5 倍。
    /// </summary>
    private static void TestGpt54FastCreditMultiplier()
    {
        var usage = new TokenUsage(100_000, 50_000, 0, 10_000, 2_000);
        var result = PublicApiPricing.Calculate("gpt-5.4", "fast", usage);
        Equal(true, result.Success, "GPT-5.4 Fast 应可定价");
        Near(2m, result.CreditMultiplier, 0.000001m, "GPT-5.4 Fast 额度倍率");
        Near(0.575m, result.CostUsd, 0.000001m, "GPT-5.4 Standard API 等价金额");
    }

    /// <summary>
    /// 验证官方未定义 ChatGPT Fast 倍率的模型组合明确失败，不使用 API Fast 价格代替。
    /// </summary>
    private static void TestUnsupportedFastMultiplierFailsClosed()
    {
        var result = PublicApiPricing.Calculate("gpt-5.3-codex", "priority", new(10_000, 5_000, 0, 1_000, 0));
        Equal(false, result.Success, "未定义的 Fast credit multiplier 必须不可定价");
    }

    /// <summary>
    /// 验证真实历史日志中出现过的 GPT-5.2 能按当前 Standard API 价格计价。
    /// </summary>
    private static void TestObservedGpt52StandardPricing()
    {
        var result = PublicApiPricing.Calculate("gpt-5.2", "default", new(100_000, 50_000, 0, 10_000, 0));
        Equal(true, result.Success, "GPT-5.2 Standard 应可定价");
        Near(0.23625m, result.CostUsd, 0.000001m, "GPT-5.2 Standard 金额");
    }

    /// <summary>
    /// 验证 Auto-review rollout 使用 GPT-5.6 Luna Standard 代理价格，并能形成有效周额度样本。
    /// </summary>
    private static void TestAutoReviewUsesLunaPricingEndToEnd()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "rollout-auto-review.jsonl");
        var timestamp = DateTimeOffset.UtcNow;
        File.WriteAllLines(path,
        [
            TurnContextLine(timestamp, "codex-auto-review", "standard"),
            ResponseItemLine(timestamp.AddSeconds(1)),
            TokenCountLine(timestamp.AddSeconds(2), 100_000, 80_000, 10_000, 5_000, 2_000)
        ]);

        var scan = new RolloutLogReader().ScanNew(directory, new MonitorState { RolloutFilesPrimed = true });
        Equal(1, scan.ModelResponseCount, "Auto-review 响应应进入可定价响应数");
        Equal(0, scan.UnpricedModelResponses, "Auto-review 响应不应再标为未定价");
        Near(0.0121m, scan.ApiEquivalentUsd, 0.000001m, "Auto-review 应使用 Luna Standard 代理金额");
        Near(0.0121m, scan.OfficialLongContextApiEquivalentUsd, 0.000001m, "短上下文 Auto-review 双口径金额应一致");
        Equal("codex-auto-review", scan.Models.Single(), "样本应保留 Auto-review 原始模型标识");
        Equal("standard", scan.ServiceTiers.Single(), "Auto-review Standard 层级");
        Equal("1x", scan.CreditMultipliers.Single(), "Auto-review Standard 额度倍率");

        var state = new MonitorState();
        var reset = timestamp.AddDays(7);
        var baseline = new RateLimitSnapshot(timestamp, "codex", "Codex", 4m, 10080, reset);
        QuotaEstimator.Process(state, baseline, RolloutScanResult.Empty, 0.1m);
        var changed = baseline with { SampledAt = timestamp.AddMinutes(1), UsedPercent = 5m };
        var update = QuotaEstimator.Process(state, changed, scan, 0.1m);
        Equal(true, update.SampleCreated, "包含 Auto-review 的有效区间应形成样本");
        Near(1.21m, update.Sample!.EstimatedWeeklyQuotaUsd, 0.000001m, "Auto-review 区间反推周额度");
        Equal(PublicApiPricing.PricingVersion, update.Sample.PricingVersion, "Auto-review 样本价格策略版本");
        Directory.Delete(directory, true);
    }

    /// <summary>
    /// 验证同一额度桶内自动选择一周 secondary，而非 5 小时 primary。
    /// </summary>
    private static void TestWeeklyWindowSelection()
    {
        using var document = JsonDocument.Parse("""
        {
          "rateLimits": {
            "limitId": "codex",
            "limitName": "Codex",
            "primary": { "usedPercent": 12.5, "windowDurationMins": 300, "resetsAt": 1787300000 },
            "secondary": { "usedPercent": 34.25, "windowDurationMins": 10080, "resetsAt": 1787900000 }
          }
        }
        """);
        var snapshot = RateLimitParser.Parse(document.RootElement, string.Empty, 1440, DateTimeOffset.Now);
        Equal(10080, snapshot!.WindowDurationMinutes, "应选择一周窗口");
        Near(34.25m, snapshot.UsedPercent, 0.000001m, "周窗口百分比");
    }

    /// <summary>
    /// 验证有效区间生成的样本保存金额定义、价格版本、服务层级和额度倍率。
    /// </summary>
    private static void TestQuotaEstimationPersistsAmountDefinition()
    {
        var state = new MonitorState();
        var reset = DateTimeOffset.Now.AddDays(5);
        var first = new RateLimitSnapshot(DateTimeOffset.Now, "codex", "Codex", 20m, 10080, reset);
        QuotaEstimator.Process(state, first, RolloutScanResult.Empty, 0.1m);

        var scan = PricedScan(new(100_000, 80_000, 0, 5_000, 2_000), 0.5m, 0.8m, "standard", "1x");
        var second = first with { SampledAt = first.SampledAt.AddMinutes(10), UsedPercent = 20.5m };
        var update = QuotaEstimator.Process(state, second, scan, 0.1m);

        Equal(true, update.SampleCreated, "百分比变化应生成样本");
        Near(100m, update.Sample!.EstimatedWeeklyQuotaUsd, 0.000001m, "反推周限额");
        Near(160m, update.Sample.OfficialLongContextEstimatedWeeklyQuotaUsd, 0.000001m, "官方长上下文加价口径周限额");
        Equal(PublicApiPricing.AmountDefinition, update.Sample.AmountDefinition, "样本金额定义");
        Equal(PublicApiPricing.OfficialLongContextAmountDefinition, update.Sample.OfficialLongContextAmountDefinition, "样本官方长上下文金额定义");
        Equal(PublicApiPricing.PricingVersion, update.Sample.PricingVersion, "样本价格版本");
        Equal("standard", update.Sample.ServiceTiers, "样本服务层级");
        Equal("1x", update.Sample.CreditMultipliers, "样本额度倍率");
    }

    /// <summary>
    /// 验证确认重置后只使用当前窗口起点后的日志，并可从零百分比恢复首个样本。
    /// </summary>
    private static void TestConfirmedResetRecoversCurrentWindowSample()
    {
        var state = new MonitorState();
        var first = new RateLimitSnapshot(DateTimeOffset.Now, "codex", null, 99m, 10080, DateTimeOffset.Now.AddHours(1));
        QuotaEstimator.Process(state, first, RolloutScanResult.Empty, 0.1m);
        var reset = first with
        {
            SampledAt = first.SampledAt.AddHours(2),
            UsedPercent = 1m,
            ResetsAt = first.ResetsAt.AddDays(7)
        };
        var windowStart = reset.ResetsAt.AddMinutes(-reset.WindowDurationMinutes);
        var scan = PricedScan(new(1000, 500, 0, 20, 10), 0.5m, 0.5m, "standard", "1x") with
        {
            IncludedSince = windowStart
        };

        var update = QuotaEstimator.Process(state, reset, scan, 0.1m);
        Equal(true, update.SampleCreated, "新窗口日志完整时应恢复重置后的首个样本");
        Near(50m, update.Sample!.EstimatedWeeklyQuotaUsd, 0.000001m, "重置后从零百分比反推");
    }

    /// <summary>
    /// 验证重置后服务端名义窗口起点晚于采样时间几十秒时会钳制到采样时刻，历史读取不得崩溃。
    /// </summary>
    private static void TestResetFutureStartClockSkewDoesNotCrashHistory()
    {
        var directory = CreateTemporaryDirectory();
        var sampledAt = new DateTimeOffset(2026, 8, 26, 8, 17, 3, TimeSpan.FromHours(8));
        var snapshot = new RateLimitSnapshot(
            sampledAt,
            "codex",
            "Codex",
            0m,
            10080,
            sampledAt.AddMinutes(10080).AddSeconds(38));

        var effectiveWindowStart = QuotaEstimator.GetEffectiveWindowStart(snapshot);
        Equal(sampledAt, effectiveWindowStart, "未来 38 秒的名义窗口起点应钳制到本次采样时刻");
        var facts = new RolloutLogReader().ReadHistoricalFacts(
            directory,
            effectiveWindowStart,
            snapshot.SampledAt,
            snapshot.WindowDurationMinutes);
        var replay = HistoricalReplayCalculator.Build(snapshot, facts, []);
        Equal(0, replay.Samples.Count, "刚重置为 0% 时不应虚构历史金额样本");
        Equal(0, replay.UnattributedIntervalCount, "零长度新窗口不应产生未归因区间");
        Directory.Delete(directory, true);
    }

    /// <summary>
    /// 验证百分比归零且新窗口起点接近当前时刻时，即使未跨旧截止日也能确认提前重置并更新承诺。
    /// </summary>
    private static void TestEarlyZeroResetIsConfirmed()
    {
        var firstAt = new DateTimeOffset(2026, 8, 25, 17, 30, 0, TimeSpan.FromHours(8));
        var state = new MonitorState();
        var baseline = new RateLimitSnapshot(firstAt, "codex", "Codex", 27m, 10080, firstAt.AddDays(5));
        QuotaEstimator.Process(state, baseline, RolloutScanResult.Empty, 0.1m);
        var resetAt = firstAt.AddHours(15).AddMinutes(10080).AddSeconds(38);
        var reset = baseline with
        {
            SampledAt = firstAt.AddHours(15),
            UsedPercent = 0m,
            ResetsAt = resetAt
        };

        var scanStart = QuotaEstimator.GetRolloutScanStart(state, reset);
        Equal(reset.SampledAt, scanStart!.Value, "提前重置应按不晚于观察时刻的新窗口起点切分日志");
        var update = QuotaEstimator.Process(
            state,
            reset,
            RolloutScanResult.Empty with { IncludedSince = scanStart },
            0.1m);

        Equal(false, update.SampleCreated, "0% 重置点本身不应生成金额样本");
        Equal(resetAt, state.LastResetAt!.Value, "提前重置后应接受新的重置承诺");
        Near(0m, state.ReferenceUsedPercent!.Value, 0.000001m, "提前重置后应从 0% 建立新基线");
    }

    /// <summary>
    /// 验证旧版已保存首次 0% 检查点但未更新重置承诺时，升级重启后仍能恢复确认提前重置。
    /// </summary>
    private static void TestEarlyZeroResetRecoversFromPersistedCheckpoint()
    {
        var firstAt = new DateTimeOffset(2026, 8, 25, 17, 30, 0, TimeSpan.FromHours(8));
        var firstZeroAt = firstAt.AddHours(15);
        var resetAt = firstZeroAt.AddMinutes(10080).AddSeconds(38);
        var oldResetAt = firstAt.AddDays(5);
        var state = new MonitorState
        {
            ReferenceUsedPercent = 0m,
            LastObservedUsedPercent = 0m,
            LastResetAt = oldResetAt,
            LastLimitId = "codex",
            AuthoritativeRateLimitCheckpoints =
            [
                new(firstZeroAt, "codex", 0m, 10080, resetAt)
            ]
        };
        var delayedSnapshot = new RateLimitSnapshot(
            firstZeroAt.AddMinutes(10),
            "codex",
            "Codex",
            0m,
            10080,
            resetAt);

        var scanStart = QuotaEstimator.GetRolloutScanStart(state, delayedSnapshot);
        Equal(resetAt.AddMinutes(-10080), scanStart!.Value, "升级重启后应使用首次 0% 检查点确认新窗口");
        QuotaEstimator.Process(
            state,
            delayedSnapshot,
            RolloutScanResult.Empty with { IncludedSince = scanStart },
            0.1m);
        Equal(resetAt, state.LastResetAt!.Value, "升级后应把旧重置承诺迁移为当前提前重置承诺");
    }

    /// <summary>
    /// 验证重置时未按窗口时间切分的扫描结果被保守丢弃，不能形成跨窗口样本。
    /// </summary>
    private static void TestUnfilteredResetDoesNotCreateSample()
    {
        var state = new MonitorState();
        var first = new RateLimitSnapshot(DateTimeOffset.Now, "codex", null, 99m, 10080, DateTimeOffset.Now.AddHours(1));
        QuotaEstimator.Process(state, first, RolloutScanResult.Empty, 0.1m);
        var reset = first with
        {
            SampledAt = first.SampledAt.AddHours(2),
            UsedPercent = 1m,
            ResetsAt = first.ResetsAt.AddDays(7)
        };
        var update = QuotaEstimator.Process(state, reset, PricedScan(new(1000, 500, 0, 20, 10), 0.5m, 0.5m, "standard", "1x"), 0.1m);
        Equal(false, update.SampleCreated, "未切分的重置扫描不得生成样本");
        Equal(0, state.Samples.Count, "重置后样本数应保持为零");
        Near(1m, state.ReferenceUsedPercent!.Value, 0.000001m, "重置后应以当前百分比建立基线");
    }

    /// <summary>
    /// 验证旧重置时刻之前的 resetsAt 漂移不会重建零基线，10% 到 11% 必须按 1% 增量估算。
    /// </summary>
    private static void TestUnconfirmedResetDriftUsesIncrementalPercent()
    {
        var sampledAt = DateTimeOffset.Now;
        var previousResetAt = sampledAt.AddDays(5);
        var state = new MonitorState();
        var baseline = new RateLimitSnapshot(sampledAt, "codex", null, 10m, 10080, previousResetAt);
        QuotaEstimator.Process(state, baseline, RolloutScanResult.Empty, 0.1m);
        var drifted = baseline with
        {
            SampledAt = sampledAt.AddMinutes(1),
            UsedPercent = 11m,
            ResetsAt = previousResetAt.AddMinutes(5)
        };

        Equal(true, QuotaEstimator.RequiresRolloutScan(state, drifted), "百分比增加仍应触发日志扫描");
        Equal(true, QuotaEstimator.GetRolloutScanStart(state, drifted) is null, "旧重置时刻前的漂移不得切分新窗口");
        var update = QuotaEstimator.Process(
            state,
            drifted,
            PricedScan(new(1000, 500, 0, 20, 10), 0.5m, 0.5m, "standard", "1x"),
            0.1m);

        Equal(true, update.SampleCreated, "普通 1% 增量应形成样本");
        Near(1m, update.Sample!.DeltaPercent, 0.000001m, "resetsAt 漂移后的百分比增量");
        Near(50m, update.Sample.EstimatedWeeklyQuotaUsd, 0.000001m, "漂移场景不得把分母放大到 11%");
        Equal(previousResetAt, state.LastResetAt!.Value, "未确认漂移不得覆盖旧重置承诺");
    }

    /// <summary>
    /// 验证更晚的 resetsAt 只有在采样跨过旧重置时刻后才被确认并用于日志窗口切分。
    /// </summary>
    private static void TestResetRequiresCrossingPreviousDeadline()
    {
        var sampledAt = DateTimeOffset.Now;
        var previousResetAt = sampledAt.AddHours(1);
        var nextResetAt = previousResetAt.AddDays(7);
        var state = new MonitorState();
        var baseline = new RateLimitSnapshot(sampledAt, "codex", null, 40m, 10080, previousResetAt);
        QuotaEstimator.Process(state, baseline, RolloutScanResult.Empty, 0.1m);
        var beforeDeadline = baseline with
        {
            SampledAt = sampledAt.AddMinutes(30),
            UsedPercent = 41m,
            ResetsAt = nextResetAt
        };
        Equal(true, QuotaEstimator.GetRolloutScanStart(state, beforeDeadline) is null, "旧重置时刻前不得确认新窗口");
        QuotaEstimator.Process(
            state,
            beforeDeadline,
            PricedScan(new(1000, 500, 0, 20, 10), 0.5m, 0.5m, "standard", "1x"),
            0.1m);

        var afterDeadline = beforeDeadline with
        {
            SampledAt = sampledAt.AddHours(2),
            UsedPercent = 1m
        };
        var expectedWindowStart = nextResetAt.AddMinutes(-afterDeadline.WindowDurationMinutes);
        Equal(expectedWindowStart, QuotaEstimator.GetRolloutScanStart(state, afterDeadline)!.Value, "跨过旧重置时刻后应切分新窗口");
        var resetScan = PricedScan(new(1000, 500, 0, 20, 10), 0.5m, 0.5m, "standard", "1x") with
        {
            IncludedSince = expectedWindowStart
        };
        var update = QuotaEstimator.Process(state, afterDeadline, resetScan, 0.1m);
        Equal(true, update.SampleCreated, "确认重置后应使用新窗口日志形成样本");
        Equal(nextResetAt, state.LastResetAt!.Value, "确认重置后应接受新重置时刻");
    }

    /// <summary>
    /// 验证只有百分比下降而 resetsAt 未变化时保守重建基线并清除 pending。
    /// </summary>
    private static void TestPercentDecreaseWithoutResetIsConservative()
    {
        var state = new MonitorState();
        var first = new RateLimitSnapshot(DateTimeOffset.Now, "codex", null, 20m, 10080, DateTimeOffset.Now.AddDays(5));
        QuotaEstimator.Process(state, first, RolloutScanResult.Empty, 0.1m);
        var increase = first with { UsedPercent = 20.05m };
        QuotaEstimator.Process(state, increase, PricedScan(new(1000, 500, 0, 20, 10), 0.01m, 0.01m, "standard", "1x"), 0.1m);
        var decreased = increase with { UsedPercent = 19.9m };
        var update = QuotaEstimator.Process(state, decreased, RolloutScanResult.Empty, 0.1m);
        Equal(false, update.SampleCreated, "疑似修正不得生成样本");
        Equal(0, state.PendingModelResponseCount, "疑似修正后 pending 响应应清空");
        Near(19.9m, state.ReferenceUsedPercent!.Value, 0.000001m, "疑似修正后的新基线");
    }

    /// <summary>
    /// 验证额度桶切换时不会把旧桶 pending 成本带入新桶。
    /// </summary>
    private static void TestBucketSwitchClearsPending()
    {
        var state = new MonitorState();
        var first = new RateLimitSnapshot(DateTimeOffset.Now, "codex", null, 10m, 10080, DateTimeOffset.Now.AddDays(5));
        QuotaEstimator.Process(state, first, RolloutScanResult.Empty, 0.1m);
        var increase = first with { UsedPercent = 10.05m };
        QuotaEstimator.Process(state, increase, PricedScan(new(1000, 500, 0, 20, 10), 0.01m, 0.01m, "standard", "1x"), 0.1m);
        var switched = increase with { LimitId = "codex_other", UsedPercent = 4m };
        var update = QuotaEstimator.Process(state, switched, RolloutScanResult.Empty, 0.1m);
        Equal(false, update.SampleCreated, "桶切换不得生成样本");
        Equal(0, state.PendingModelResponseCount, "桶切换后 pending 响应应清空");
        Equal("codex_other", state.LastLimitId, "应记录新额度桶");
    }

    /// <summary>
    /// 验证包含畸形日志的百分比区间按数据完整性要求放弃，不生成偏低金额样本。
    /// </summary>
    private static void TestMalformedIntervalIsRejected()
    {
        var state = new MonitorState();
        var first = new RateLimitSnapshot(DateTimeOffset.Now, "codex", null, 10m, 10080, DateTimeOffset.Now.AddDays(5));
        QuotaEstimator.Process(state, first, RolloutScanResult.Empty, 0.1m);
        var scan = PricedScan(new(1000, 500, 0, 20, 10), 0.5m, 0.5m, "standard", "1x") with
        {
            MalformedLineCount = 1
        };
        var update = QuotaEstimator.Process(state, first with { UsedPercent = 10.5m }, scan, 0.1m);
        Equal(false, update.SampleCreated, "畸形日志区间不得生成样本");
        Equal(1, state.MalformedRolloutLines, "畸形日志应累计到长期诊断");
        Equal(1, state.UnattributedPercentChanges, "被放弃区间应计为未归因变化");
    }

    /// <summary>
    /// 验证旧金额口径或旧价格版本样本不参与当前回归曲线。
    /// </summary>
    private static void TestRegressionExcludesLegacyPricing()
    {
        var start = DateTimeOffset.Now;
        var samples = new[]
        {
            LegacySample(start, 900),
            CurrentSample(start.AddHours(1), 42)
        };
        var curve = RegressionCalculator.BuildCurve(samples, new RegressionOptions { Mode = RegressionMode.GaussianAggregation });
        var officialLongCurve = RegressionCalculator.BuildOfficialLongContextCurve(
            samples,
            new RegressionOptions { Mode = RegressionMode.GaussianAggregation });
        Equal(1, curve.Count, "回归只能使用当前口径样本");
        Near(42m, curve[0].Value, 0.000001m, "旧口径样本不得影响当前估计");
        Equal(1, officialLongCurve.Count, "官方长上下文回归只能使用当前双口径样本");
        Near(42m, officialLongCurve[0].Value, 0.000001m, "官方长上下文回归不得混入旧口径样本");
    }

    /// <summary>
    /// 验证 priority rollout 被识别为 Fast，并按 GPT-5.6 的 2.5 倍额度倍率计入。
    /// </summary>
    private static void TestRolloutPriorityResponseAssociation()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "rollout-priority.jsonl");
        var timestamp = DateTimeOffset.UtcNow;
        File.WriteAllLines(path,
        [
            TurnContextLine(timestamp, "gpt-5.6-sol", "priority"),
            ResponseItemLine(timestamp.AddSeconds(1)),
            TokenCountLine(timestamp.AddSeconds(2), 100_000, 80_000, 0, 5_000, 2_000),
            TokenCountLine(timestamp.AddSeconds(3), 100_000, 80_000, 0, 5_000, 2_000)
        ]);

        var result = new RolloutLogReader().ScanNew(directory, new MonitorState { RolloutFilesPrimed = true });
        Equal(1, result.ModelResponseCount, "孤立重复 token_count 不得重复计价");
        Near(0.53m, result.ApiEquivalentUsd, 0.000001m, "priority Standard API 等价金额");
        Equal("fast", result.ServiceTiers.Single(), "priority 应记录为 fast");
        Equal("2.5x", result.CreditMultipliers.Single(), "priority 应记录 2.5 倍额度倍率");
        Directory.Delete(directory, true);
    }

    /// <summary>
    /// 验证模型工具调用后的完成事件、工具输出和 agent 事件不会在 token_count 前破坏响应关联。
    /// </summary>
    private static void TestIntermediateEventsPreserveResponseAssociation()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "rollout-intermediate-events.jsonl");
        var timestamp = DateTimeOffset.UtcNow;
        File.WriteAllLines(path,
        [
            TurnContextLine(timestamp, "gpt-5.6-sol", "default"),
            ResponseItemLine(timestamp.AddSeconds(1), "custom_tool_call"),
            EventMessageLine(timestamp.AddSeconds(2), "item_completed"),
            ResponseItemOutputLine(timestamp.AddSeconds(3), "custom_tool_call_output"),
            EventMessageLine(timestamp.AddSeconds(4), "agent_message"),
            TokenCountLine(timestamp.AddSeconds(5), 100_000, 80_000, 0, 5_000, 2_000),
            TokenCountLine(timestamp.AddSeconds(6), 100_000, 80_000, 0, 5_000, 2_000)
        ]);

        var result = new RolloutLogReader().ScanNew(directory, new MonitorState { RolloutFilesPrimed = true });
        Equal(1, result.ModelResponseCount, "跨中间事件的响应应且仅应计价一次");
        Equal("standard", result.ServiceTiers.Single(), "跨中间事件后仍应保留响应时 Standard 层级");
        Directory.Delete(directory, true);
    }

    /// <summary>
    /// 验证 info=null 的 token_count 心跳不会消费待关联响应，后续真实用量仍只计价一次。
    /// </summary>
    private static void TestTokenHeartbeatPreservesResponseAssociation()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "rollout-token-heartbeat.jsonl");
        var timestamp = DateTimeOffset.UtcNow;
        File.WriteAllLines(path,
        [
            TurnContextLine(timestamp, "gpt-5.6-sol", "default"),
            ResponseItemLine(timestamp.AddSeconds(1)),
            TokenHeartbeatLine(timestamp.AddSeconds(2)),
            TokenCountLine(timestamp.AddSeconds(3), 100_000, 80_000, 0, 5_000, 2_000)
        ]);

        var result = new RolloutLogReader().ScanNew(directory, new MonitorState { RolloutFilesPrimed = true });
        Equal(1, result.ModelResponseCount, "心跳之后的真实 token_count 应关联到原响应");
        Directory.Delete(directory, true);
    }

    /// <summary>
    /// 验证新版设置事件可从 Standard 切换到 Fast，且缺少旧层级字段的 turn_context 不会覆盖设置。
    /// </summary>
    private static void TestThreadSettingsAppliedSwitchesTier()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "rollout-tier-switch.jsonl");
        var timestamp = DateTimeOffset.UtcNow;
        File.WriteAllLines(path,
        [
            ThreadSettingsAppliedLine(timestamp, "gpt-5.6-sol", "default"),
            TurnContextWithoutTierLine(timestamp.AddSeconds(1), "gpt-5.6-sol"),
            ResponseItemLine(timestamp.AddSeconds(2)),
            TokenCountLine(timestamp.AddSeconds(3), 100_000, 80_000, 0, 5_000, 2_000),
            ThreadSettingsAppliedLine(timestamp.AddSeconds(4), "gpt-5.6-sol", "priority"),
            TurnContextWithoutTierLine(timestamp.AddSeconds(5), "gpt-5.6-sol"),
            ResponseItemLine(timestamp.AddSeconds(6)),
            TokenCountLine(timestamp.AddSeconds(7), 100_000, 80_000, 0, 5_000, 2_000)
        ]);

        var state = new MonitorState { RolloutFilesPrimed = true };
        var result = new RolloutLogReader().ScanNew(directory, state);
        Equal(2, result.ModelResponseCount, "Standard 和 Fast 响应都应计价");
        Near(0.742m, result.ApiEquivalentUsd, 0.000001m, "切换层级后的 Standard API 等价总金额");
        Equal(true, result.ServiceTiers.Contains("standard"), "default 设置应记录为 Standard");
        Equal(true, result.ServiceTiers.Contains("fast"), "priority 设置应记录为 Fast");
        Equal(true, result.CreditMultipliers.Contains("1x"), "Standard 应记录 1 倍额度倍率");
        Equal(true, result.CreditMultipliers.Contains("2.5x"), "GPT-5.6 Fast 应记录 2.5 倍额度倍率");
        Directory.Delete(directory, true);
    }

    /// <summary>
    /// 验证旧游标的隐式 Standard 会迁移为 unknown，且没有设置事件的新版响应明确不可定价。
    /// </summary>
    private static void TestUnknownTierFailsClosedAfterCursorMigration()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "rollout-unknown-tier.jsonl");
        var timestamp = DateTimeOffset.UtcNow;
        File.WriteAllLines(path,
        [
            TurnContextWithoutTierLine(timestamp, "gpt-5.6-sol"),
            ResponseItemLine(timestamp.AddSeconds(1)),
            TokenCountLine(timestamp.AddSeconds(2), 100_000, 80_000, 0, 5_000, 2_000)
        ]);
        var state = new MonitorState { RolloutFilesPrimed = true };
        state.FileCursors[path] = new FileCursorState { CurrentServiceTier = "standard" };

        RolloutLogReader.MigrateServiceTierTrackingState(state);
        var result = new RolloutLogReader().ScanNew(directory, state);
        Equal(RolloutLogReader.CurrentServiceTierTrackingVersion, state.ServiceTierTrackingVersion, "旧状态应升级服务层级追踪版本");
        Equal("unknown", state.FileCursors[path].CurrentServiceTier, "旧游标的隐式 Standard 应迁移为 unknown");
        Equal(0, result.ModelResponseCount, "未知层级响应不得进入可定价响应数");
        Equal(1, result.UnpricedModelResponses, "未知层级响应应进入未定价诊断");
        Equal("gpt-5.6-sol@unknown", result.UnpricedModels.Single(), "未定价诊断应保留模型和未知层级");
        Directory.Delete(directory, true);
    }

    /// <summary>
    /// 验证旧响应关联状态因缺少模型和层级快照而被迁移清空，不能跨版本误计价。
    /// </summary>
    private static void TestResponseAssociationMigrationClearsStalePending()
    {
        var cursor = new FileCursorState
        {
            PendingModelResponse = true,
            PendingResponseModel = "gpt-5.6-sol",
            PendingResponseServiceTier = "priority"
        };
        var state = new MonitorState
        {
            ServiceTierTrackingVersion = RolloutLogReader.CurrentServiceTierTrackingVersion,
            ResponseAssociationTrackingVersion = 0,
            FileCursors = new Dictionary<string, FileCursorState>(StringComparer.OrdinalIgnoreCase)
            {
                ["stale-rollout.jsonl"] = cursor
            }
        };

        RolloutLogReader.MigrateResponseAssociationTrackingState(state);
        Equal(RolloutLogReader.CurrentResponseAssociationTrackingVersion, state.ResponseAssociationTrackingVersion, "响应关联状态版本应升级");
        Equal(false, cursor.PendingModelResponse, "旧 pending 响应应清除");
        Equal(string.Empty, cursor.PendingResponseModel, "旧 pending 模型快照应清除");
        Equal(string.Empty, cursor.PendingResponseServiceTier, "旧 pending 层级快照应清除");
    }

    /// <summary>
    /// 验证历史读取能从真实 token_count 形态同时提取逐响应事实和 secondary 周额度快照。
    /// </summary>
    private static void TestHistoricalFactsExtractRateLimitAndResponse()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "rollout-historical-facts.jsonl");
        var timestamp = DateTimeOffset.UtcNow;
        var resetAt = timestamp.AddDays(7);
        File.WriteAllLines(path,
        [
            TurnContextLine(timestamp, "gpt-5.6-sol", "default"),
            ResponseItemLine(timestamp.AddSeconds(1), "custom_tool_call"),
            EventMessageLine(timestamp.AddSeconds(2), "item_completed"),
            TokenCountWithRateLimitsLine(
                timestamp.AddSeconds(3),
                100_000,
                80_000,
                0,
                5_000,
                2_000,
                1m,
                10080,
                resetAt)
        ]);

        var facts = new RolloutLogReader().ReadHistoricalFacts(
            directory,
            timestamp.AddMinutes(-1),
            timestamp.AddMinutes(1),
            10080);
        Equal(1, facts.FilesScanned, "历史事实应扫描一个活动 rollout");
        Equal(1, facts.Responses.Count, "跨 item_completed 的响应应进入历史事实");
        Equal(true, facts.Responses[0].PricingSucceeded, "历史响应应使用当前 Standard 价格成功计价");
        Equal(1, facts.RateLimitCheckpoints.Count, "secondary 周窗口应形成一个额度候选");
        Near(1m, facts.RateLimitCheckpoints[0].UsedPercent, 0.000001m, "历史周额度百分比");
        Equal(10080, facts.RateLimitCheckpoints[0].WindowDurationMinutes, "历史周窗口分钟数");
        Directory.Delete(directory, true);
    }

    /// <summary>
    /// 验证同一文件全部明确层级一致时，可把首个设置事件前的历史响应安全回填为该层级。
    /// </summary>
    private static void TestHistoricalFactsBackfillConsistentTier()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "rollout-consistent-tier.jsonl");
        var timestamp = DateTimeOffset.UtcNow;
        var resetAt = timestamp.AddDays(7);
        File.WriteAllLines(path,
        [
            TurnContextWithoutTierLine(timestamp, "gpt-5.6-sol"),
            ResponseItemLine(timestamp.AddSeconds(1)),
            TokenCountWithRateLimitsLine(timestamp.AddSeconds(2), 100_000, 80_000, 0, 5_000, 2_000, 1m, 10080, resetAt),
            ThreadSettingsAppliedLine(timestamp.AddSeconds(3), "gpt-5.6-sol", "default")
        ]);

        var facts = new RolloutLogReader().ReadHistoricalFacts(
            directory,
            timestamp.AddMinutes(-1),
            timestamp.AddMinutes(1),
            10080);
        Equal(1, facts.Responses.Count, "应提取设置事件前的一条历史响应");
        Equal(true, facts.Responses[0].PricingSucceeded, "一致的后续 Standard 证据应安全回填首个响应");
        Equal("standard", facts.Responses[0].NormalizedServiceTier, "回填后的规范化层级");
        Directory.Delete(directory, true);
    }

    /// <summary>
    /// 验证同一文件出现 Standard 与 Fast 切换时不会向首个明确设置之前反向猜测层级。
    /// </summary>
    private static void TestHistoricalFactsDoNotBackfillMixedTier()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "rollout-mixed-tier.jsonl");
        var timestamp = DateTimeOffset.UtcNow;
        var resetAt = timestamp.AddDays(7);
        File.WriteAllLines(path,
        [
            TurnContextWithoutTierLine(timestamp, "gpt-5.6-sol"),
            ResponseItemLine(timestamp.AddSeconds(1)),
            TokenCountWithRateLimitsLine(timestamp.AddSeconds(2), 100_000, 80_000, 0, 5_000, 2_000, 1m, 10080, resetAt),
            ThreadSettingsAppliedLine(timestamp.AddSeconds(3), "gpt-5.6-sol", "default"),
            ThreadSettingsAppliedLine(timestamp.AddSeconds(4), "gpt-5.6-sol", "priority")
        ]);

        var facts = new RolloutLogReader().ReadHistoricalFacts(
            directory,
            timestamp.AddMinutes(-1),
            timestamp.AddMinutes(1),
            10080);
        Equal(1, facts.Responses.Count, "混合层级文件仍应提取响应事实");
        Equal(false, facts.Responses[0].PricingSucceeded, "层级发生切换时首个未知响应必须继续失败关闭");
        Equal("unknown", facts.Responses[0].ServiceTier, "混合层级不得反向猜测首个响应");
        Directory.Delete(directory, true);
    }

    /// <summary>
    /// 验证整份文件完全没有层级证据时，历史读取仍保留 unknown 并拒绝猜测 Standard。
    /// </summary>
    private static void TestHistoricalFactsWithoutTierRemainUnpriced()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "rollout-no-tier.jsonl");
        var timestamp = DateTimeOffset.UtcNow;
        File.WriteAllLines(path,
        [
            TurnContextWithoutTierLine(timestamp, "codex-auto-review"),
            ResponseItemLine(timestamp.AddSeconds(1)),
            TokenCountLine(timestamp.AddSeconds(2), 20_000, 5_000, 0, 200, 100)
        ]);

        var facts = new RolloutLogReader().ReadHistoricalFacts(
            directory,
            timestamp.AddMinutes(-1),
            timestamp.AddMinutes(1),
            10080);

        Equal(1, facts.Responses.Count, "无层级文件仍应提取响应事实");
        Equal(false, facts.Responses[0].PricingSucceeded, "完全无层级证据时必须继续失败关闭");
        Equal("unknown", facts.Responses[0].ServiceTier, "不得把完全未知层级默认成 Standard");
        Directory.Delete(directory, true);
    }

    /// <summary>
    /// 验证百分比不变时，同一 rollout 后补唯一层级事件会改变受跟踪文件并使历史区间恢复。
    /// </summary>
    private static void TestDelayedTierAppendTriggersRecoverableReplay()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "rollout-delayed-tier.jsonl");
        var windowStart = DateTimeOffset.UtcNow.AddMinutes(-5);
        var responseAt = windowStart.AddMinutes(2);
        var resetAt = windowStart.AddMinutes(10080);
        var sampledAt = windowStart.AddMinutes(4);
        File.WriteAllLines(path,
        [
            TurnContextWithoutTierLine(responseAt.AddSeconds(-2), "gpt-5.6-sol"),
            ResponseItemLine(responseAt.AddSeconds(-1)),
            TokenCountWithRateLimitsLine(responseAt, 100_000, 80_000, 0, 5_000, 2_000, 18m, 10080, resetAt)
        ]);
        var authoritative = new[]
        {
            new AuthoritativeRateLimitCheckpoint(responseAt.AddSeconds(-3), "codex", 17m, 10080, resetAt),
            new AuthoritativeRateLimitCheckpoint(responseAt, "codex", 18m, 10080, resetAt)
        };
        var snapshot = new RateLimitSnapshot(sampledAt, "codex", "Codex", 18m, 10080, resetAt);
        var reader = new RolloutLogReader();
        var initialFacts = reader.ReadHistoricalFacts(directory, windowStart, sampledAt, 10080);
        var initialReplay = HistoricalReplayCalculator.Build(snapshot, initialFacts, authoritative);

        Equal(0, initialReplay.Samples.Count, "层级尚未补写时不得形成样本");
        Equal(1, initialReplay.UnpricedResponseCount, "初次重放应报告一条未知层级响应");
        Equal(path, initialReplay.UnresolvedSourceFiles.Single(), "应跟踪未知层级响应所在文件");
        var trackedLengths = RolloutLogReader.CaptureFileLengths(initialReplay.UnresolvedSourceFiles);

        File.AppendAllLines(path,
        [
            ThreadSettingsAppliedLine(responseAt.AddSeconds(1), "gpt-5.6-sol", "default")
        ]);
        Equal(true, RolloutLogReader.HaveTrackedFilesChanged(trackedLengths), "层级事件补写后应触发按需重放");

        var recoveredFacts = reader.ReadHistoricalFacts(directory, windowStart, sampledAt, 10080);
        var recoveredReplay = HistoricalReplayCalculator.Build(snapshot, recoveredFacts, authoritative);
        Equal(1, recoveredReplay.Samples.Count, "后补唯一 Standard 层级后应恢复 17% 到 18% 样本");
        Equal(0, recoveredReplay.UnpricedResponseCount, "恢复后不应再有未定价响应");
        Equal("standard", recoveredReplay.Samples[0].ServiceTiers, "恢复样本应标记 Standard 层级");
        Directory.Delete(directory, true);
    }

    /// <summary>
    /// 验证重复查询相同百分比时只保留首次观察时刻，防止后续响应扩大已经结束的区间。
    /// </summary>
    private static void TestAuthoritativeCheckpointPreservesFirstObservation()
    {
        var state = new MonitorState();
        var firstAt = DateTimeOffset.UtcNow;
        var resetAt = firstAt.AddDays(7);
        var first = new RateLimitSnapshot(firstAt, "codex", "Codex", 18m, 10080, resetAt);
        var repeated = first with { SampledAt = firstAt.AddMinutes(10) };

        Equal(true, HistoricalReplayCalculator.RecordAuthoritativeCheckpoint(state, first), "首次百分比观察应建立权威检查点");
        Equal(false, HistoricalReplayCalculator.RecordAuthoritativeCheckpoint(state, repeated), "重复百分比不得移动检查点");
        Equal(1, state.AuthoritativeRateLimitCheckpoints.Count, "重复查询后应只有一个 18% 检查点");
        Equal(firstAt, state.AuthoritativeRateLimitCheckpoints[0].Timestamp, "应持久化首次观察时刻");
    }

    /// <summary>
    /// 验证当前百分比首次观察之后产生的响应不会被历史重放计入已经结束的旧区间。
    /// </summary>
    private static void TestHistoricalReplayExcludesResponsesAfterFirstObservation()
    {
        var windowStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var resetAt = windowStart.AddMinutes(10080);
        var boundary = windowStart.AddMinutes(4);
        var snapshot = new RateLimitSnapshot(windowStart.AddMinutes(8), "codex", "Codex", 18m, 10080, resetAt);
        var facts = new HistoricalRolloutFacts(
            windowStart,
            snapshot.SampledAt,
            [
                HistoricalPricedResponse(boundary.AddMinutes(-1), 0.5m),
                HistoricalPricedResponse(boundary.AddMinutes(1), 9m)
            ],
            [],
            1,
            0);
        var authoritative = new[]
        {
            new AuthoritativeRateLimitCheckpoint(windowStart.AddMinutes(1), "codex", 17m, 10080, resetAt),
            new AuthoritativeRateLimitCheckpoint(boundary, "codex", 18m, 10080, resetAt)
        };

        var result = HistoricalReplayCalculator.Build(snapshot, facts, authoritative);
        Equal(1, result.Samples.Count, "17% 到 18% 应形成一个样本");
        Equal(1, result.Samples[0].ModelResponseCount, "首次观察之后的响应不得污染旧区间");
        Near(50m, result.Samples[0].EstimatedWeeklyQuotaUsd, 0.000001m, "旧区间只应包含边界前的 0.5 美元成本");
    }

    /// <summary>
    /// 验证稳定 resetsAt 聚类和百分比单调规则会排除滑动零值，并重建两个完整区间样本。
    /// </summary>
    private static void TestHistoricalReplayRejectsSlidingZeroAndBuildsSamples()
    {
        var windowStart = new DateTimeOffset(2026, 8, 24, 8, 41, 41, TimeSpan.FromHours(8));
        var resetAt = windowStart.AddMinutes(10080);
        var sampledAt = windowStart.AddHours(3);
        var snapshot = new RateLimitSnapshot(sampledAt, "codex", "Codex", 2m, 10080, resetAt);
        var responses = new[]
        {
            HistoricalPricedResponse(windowStart.AddMinutes(30), 0.5m),
            HistoricalPricedResponse(windowStart.AddMinutes(90), 0.75m)
        };
        var checkpoints = new[]
        {
            new HistoricalRateLimitCheckpoint(windowStart.AddMinutes(1), 0m, 10080, resetAt.AddSeconds(-20)),
            new HistoricalRateLimitCheckpoint(windowStart.AddMinutes(40), 1m, 10080, resetAt),
            new HistoricalRateLimitCheckpoint(windowStart.AddMinutes(50), 0m, 10080, resetAt.AddHours(2)),
            new HistoricalRateLimitCheckpoint(windowStart.AddMinutes(100), 2m, 10080, resetAt)
        };
        var facts = new HistoricalRolloutFacts(windowStart, sampledAt, responses, checkpoints, 2, 0);

        var result = HistoricalReplayCalculator.Build(snapshot, facts, []);
        Equal(2, result.Samples.Count, "0 到 2% 应重建两个样本");
        Equal(3, result.AcceptedCheckpointCount, "可信时间线应包含 0%、1%、2% 三个点");
        Equal(1, result.RejectedCheckpointCount, "滑动 resetsAt 的零值应被排除");
        Equal(0, result.UnattributedIntervalCount, "两个完整区间均应可归因");
        Near(50m, result.Samples[0].EstimatedWeeklyQuotaUsd, 0.000001m, "首个历史区间周额度");
        Near(75m, result.Samples[1].EstimatedWeeklyQuotaUsd, 0.000001m, "第二个历史区间周额度");
        Equal(HistoricalReplayCalculator.HistoricalSampleSource, result.Samples[0].SampleSource, "历史样本来源标记");
    }

    /// <summary>
    /// 验证历史重放合入状态可重复执行，替换同百分比当前点但保留旧价格归档样本。
    /// </summary>
    private static void TestHistoricalReplayApplyIsIdempotent()
    {
        var timestamp = DateTimeOffset.Now;
        var oldCurrent = CurrentSample(timestamp, 900m);
        var archived = LegacySample(timestamp.AddMinutes(-1), 700m);
        var replayed = CurrentSample(timestamp, 42m) with
        {
            SampleSource = HistoricalReplayCalculator.HistoricalSampleSource
        };
        var state = new MonitorState { Samples = [archived, oldCurrent] };
        var result = new HistoricalReplayResult(
            timestamp.AddHours(-1),
            timestamp.AddHours(1),
            "codex",
            [replayed],
            10,
            2,
            8,
            3,
            0,
            0,
            0,
            [],
            [],
            0);

        HistoricalReplayCalculator.ApplyToState(state, result);
        HistoricalReplayCalculator.ApplyToState(state, result);
        Equal(2, state.Samples.Count, "重复合入后应保留一个归档点和一个重放点");
        Equal(1, state.Samples.Count(PublicApiPricing.IsCurrentSample), "当前价格版本不得产生重复样本");
        Near(42m, state.Samples.Single(PublicApiPricing.IsCurrentSample).EstimatedWeeklyQuotaUsd, 0.000001m, "重放点应替换旧当前点");
        Equal(HistoricalReplayCalculator.CurrentReplayVersion, state.HistoricalReplayVersion, "历史重放版本应持久化");
        Equal(false, HistoricalReplayCalculator.IsReplayRequired(state), "相同重放和价格版本不应再次扫描");
    }

    /// <summary>
    /// 验证一条畸形完整 JSONL 行只计入诊断，后续有效响应仍能被读取。
    /// </summary>
    private static void TestMalformedLineDoesNotBlockLaterResponse()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "rollout-malformed.jsonl");
        var timestamp = DateTimeOffset.UtcNow;
        File.WriteAllLines(path,
        [
            "{not-json}",
            TurnContextLine(timestamp, "gpt-5.6-sol", "default"),
            ResponseItemLine(timestamp.AddSeconds(1)),
            TokenCountLine(timestamp.AddSeconds(2), 10_000, 5_000, 0, 1_000, 0)
        ]);

        var result = new RolloutLogReader().ScanNew(directory, new MonitorState { RolloutFilesPrimed = true });
        Equal(1, result.MalformedLineCount, "应隔离一条畸形日志");
        Equal(1, result.ModelResponseCount, "畸形行之后的有效响应仍应计价");
        Directory.Delete(directory, true);
    }

    /// <summary>
    /// 验证没有换行结束的 token_count 不会提前消费，补齐换行后只计价一次。
    /// </summary>
    private static void TestPartialLineWaitsForNewline()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "rollout-partial.jsonl");
        var timestamp = DateTimeOffset.UtcNow;
        var prefix = string.Join(Environment.NewLine,
        [
            TurnContextLine(timestamp, "gpt-5.6-sol", "default"),
            ResponseItemLine(timestamp.AddSeconds(1))
        ]) + Environment.NewLine;
        var partial = TokenCountLine(timestamp.AddSeconds(2), 10_000, 5_000, 0, 1_000, 0);
        File.WriteAllText(path, prefix + partial, new UTF8Encoding(false));

        var state = new MonitorState { RolloutFilesPrimed = true };
        var reader = new RolloutLogReader();
        var first = reader.ScanNew(directory, state);
        Equal(0, first.ModelResponseCount, "未换行的 token_count 不应被消费");
        File.AppendAllText(path, Environment.NewLine, new UTF8Encoding(false));
        var second = reader.ScanNew(directory, state);
        Equal(1, second.ModelResponseCount, "补齐换行后应计价一次");
        Directory.Delete(directory, true);
    }

    /// <summary>
    /// 验证同路径文件被截短后从头恢复读取，并记录一次轮转而不是永久抛错。
    /// </summary>
    private static void TestTruncatedFileRestartsCursor()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "rollout-rotated.jsonl");
        var timestamp = DateTimeOffset.UtcNow;
        File.WriteAllLines(path,
        [
            JsonSerializer.Serialize(new { timestamp, type = "session_meta", payload = new { padding = new string('x', 5000) } }),
            TurnContextLine(timestamp, "gpt-5.6-sol", "default"),
            ResponseItemLine(timestamp.AddSeconds(1)),
            TokenCountLine(timestamp.AddSeconds(2), 10_000, 5_000, 0, 1_000, 0)
        ]);

        var state = new MonitorState { RolloutFilesPrimed = true };
        var reader = new RolloutLogReader();
        Equal(1, reader.ScanNew(directory, state).ModelResponseCount, "轮转前响应应计价");
        File.WriteAllLines(path,
        [
            TurnContextLine(timestamp.AddMinutes(1), "gpt-5.6-sol", "default"),
            ResponseItemLine(timestamp.AddMinutes(1).AddSeconds(1)),
            TokenCountLine(timestamp.AddMinutes(1).AddSeconds(2), 20_000, 10_000, 0, 2_000, 0)
        ]);
        var rotated = reader.ScanNew(directory, state);
        Equal(1, rotated.RotatedFileCount, "文件截短应记录轮转恢复");
        Equal(1, rotated.ModelResponseCount, "文件截短后新响应应从头读取");
        Directory.Delete(directory, true);
    }

    /// <summary>
    /// 验证已删除 rollout 文件的持久化游标会被清理，避免状态文件无限增长。
    /// </summary>
    private static void TestDeletedFileCursorIsPruned()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "rollout-deleted.jsonl");
        File.WriteAllText(path, JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, type = "session_meta", payload = new { } }) + Environment.NewLine);
        var state = new MonitorState { RolloutFilesPrimed = true };
        var reader = new RolloutLogReader();
        reader.ScanNew(directory, state);
        Equal(1, state.FileCursors.Count, "首次扫描应保存一个游标");
        File.Delete(path);
        var result = reader.ScanNew(directory, state);
        Equal(1, result.PrunedCursorCount, "删除文件后应清理一个游标");
        Equal(0, state.FileCursors.Count, "删除文件后状态不应保留游标");
        Directory.Delete(directory, true);
    }

    /// <summary>
    /// 创建带当前价格版本和口径元数据的最小扫描结果。
    /// </summary>
    /// <param name="usage">区间 token 用量。</param>
    /// <param name="costUsd">区间 Standard API 等价金额。</param>
    /// <param name="officialLongContextCostUsd">区间官方长上下文加价金额。</param>
    /// <param name="serviceTier">规范化服务层级。</param>
    /// <param name="multiplier">额度倍率标签。</param>
    /// <returns>可直接交给 QuotaEstimator 的扫描结果。</returns>
    private static RolloutScanResult PricedScan(
        TokenUsage usage,
        decimal costUsd,
        decimal officialLongContextCostUsd,
        string serviceTier,
        string multiplier) => new(
            usage,
            costUsd,
            1,
            ["gpt-5.6-sol"],
            0,
            [])
        {
            OfficialLongContextApiEquivalentUsd = officialLongContextCostUsd,
            ServiceTiers = [serviceTier],
            CreditMultipliers = [multiplier],
            PricingVersion = PublicApiPricing.PricingVersion
        };

    /// <summary>
    /// 创建测试用已定价历史响应事实。
    /// </summary>
    /// <param name="timestamp">逐响应用量时间。</param>
    /// <param name="costUsd">两套测试口径共用的区间成本。</param>
    /// <returns>可直接参与历史区间重放的响应事实。</returns>
    private static HistoricalResponseFact HistoricalPricedResponse(DateTimeOffset timestamp, decimal costUsd) => new(
        timestamp,
        "fixture.jsonl",
        "gpt-5.6-sol",
        "default",
        new(1000, 500, 0, 20, 10),
        true,
        costUsd,
        costUsd,
        "standard",
        1m);

    /// <summary>
    /// 创建使用当前金额定义和价格版本的最小回归样本。
    /// </summary>
    /// <param name="timestamp">样本时间。</param>
    /// <param name="estimate">周额度估计。</param>
    /// <returns>当前口径样本。</returns>
    private static QuotaSample CurrentSample(DateTimeOffset timestamp, decimal estimate) =>
        LegacySample(timestamp, estimate) with
        {
            AmountDefinition = PublicApiPricing.AmountDefinition,
            OfficialLongContextIntervalApiEquivalentUsd = estimate / 100m,
            OfficialLongContextEstimatedWeeklyQuotaUsd = estimate,
            OfficialLongContextAmountDefinition = PublicApiPricing.OfficialLongContextAmountDefinition,
            PricingVersion = PublicApiPricing.PricingVersion,
            ServiceTiers = "standard",
            CreditMultipliers = "1x"
        };

    /// <summary>
    /// 创建不含金额定义和价格版本的旧版样本。
    /// </summary>
    /// <param name="timestamp">样本时间。</param>
    /// <param name="estimate">周额度估计。</param>
    /// <returns>旧口径样本。</returns>
    private static QuotaSample LegacySample(DateTimeOffset timestamp, decimal estimate) => new(
        timestamp,
        "codex",
        20,
        1,
        estimate / 100m,
        estimate,
        TokenUsage.Zero,
        1,
        "gpt-5.6-sol");

    /// <summary>
    /// 创建测试用 turn_context JSONL 行。
    /// </summary>
    /// <param name="timestamp">事件时间。</param>
    /// <param name="model">实际模型。</param>
    /// <param name="serviceTier">实际服务层级。</param>
    /// <returns>序列化后的单行 JSON。</returns>
    private static string TurnContextLine(DateTimeOffset timestamp, string model, string serviceTier) =>
        JsonSerializer.Serialize(new { timestamp, type = "turn_context", payload = new { model, service_tier = serviceTier } });

    /// <summary>
    /// 创建不含旧版 service_tier 字段的新版 turn_context JSONL 行。
    /// </summary>
    /// <param name="timestamp">事件时间。</param>
    /// <param name="model">实际模型。</param>
    /// <returns>序列化后的单行 JSON。</returns>
    private static string TurnContextWithoutTierLine(DateTimeOffset timestamp, string model) =>
        JsonSerializer.Serialize(new { timestamp, type = "turn_context", payload = new { model } });

    /// <summary>
    /// 创建新版 thread_settings_applied JSONL 行，用于明确设置模型服务层级。
    /// </summary>
    /// <param name="timestamp">事件时间。</param>
    /// <param name="model">实际模型。</param>
    /// <param name="serviceTier">default 或 priority 服务层级。</param>
    /// <returns>序列化后的单行 JSON。</returns>
    private static string ThreadSettingsAppliedLine(DateTimeOffset timestamp, string model, string serviceTier) =>
        JsonSerializer.Serialize(new
        {
            timestamp,
            type = "event_msg",
            payload = new
            {
                type = "thread_settings_applied",
                thread_settings = new { model, service_tier = serviceTier }
            }
        });

    /// <summary>
    /// 创建测试用模型 response_item JSONL 行。
    /// </summary>
    /// <param name="timestamp">事件时间。</param>
    /// <returns>序列化后的单行 JSON。</returns>
    private static string ResponseItemLine(DateTimeOffset timestamp) =>
        ResponseItemLine(timestamp, "reasoning");

    /// <summary>
    /// 创建指定模型输出类型的 response_item JSONL 行。
    /// </summary>
    /// <param name="timestamp">事件时间。</param>
    /// <param name="itemType">模型输出项类型。</param>
    /// <returns>序列化后的单行 JSON。</returns>
    private static string ResponseItemLine(DateTimeOffset timestamp, string itemType) =>
        JsonSerializer.Serialize(new { timestamp, type = "response_item", payload = new { type = itemType } });

    /// <summary>
    /// 创建工具输出 response_item JSONL 行，用于验证响应与用量之间的中间事件。
    /// </summary>
    /// <param name="timestamp">事件时间。</param>
    /// <param name="itemType">以 _output 结尾的工具输出类型。</param>
    /// <returns>序列化后的单行 JSON。</returns>
    private static string ResponseItemOutputLine(DateTimeOffset timestamp, string itemType) =>
        JsonSerializer.Serialize(new { timestamp, type = "response_item", payload = new { type = itemType } });

    /// <summary>
    /// 创建不带额外字段的 event_msg JSONL 行。
    /// </summary>
    /// <param name="timestamp">事件时间。</param>
    /// <param name="eventType">事件消息类型。</param>
    /// <returns>序列化后的单行 JSON。</returns>
    private static string EventMessageLine(DateTimeOffset timestamp, string eventType) =>
        JsonSerializer.Serialize(new { timestamp, type = "event_msg", payload = new { type = eventType } });

    /// <summary>
    /// 创建 info=null 的 token_count 心跳 JSONL 行。
    /// </summary>
    /// <param name="timestamp">事件时间。</param>
    /// <returns>序列化后的单行 JSON。</returns>
    private static string TokenHeartbeatLine(DateTimeOffset timestamp) =>
        JsonSerializer.Serialize(new { timestamp, type = "event_msg", payload = new { type = "token_count", info = (object?)null } });

    /// <summary>
    /// 创建测试用 token_count JSONL 行。
    /// </summary>
    /// <param name="timestamp">事件时间。</param>
    /// <param name="input">总输入 token。</param>
    /// <param name="cached">缓存读取 token。</param>
    /// <param name="write">缓存写入 token。</param>
    /// <param name="output">总输出 token。</param>
    /// <param name="reasoning">reasoning 输出 token。</param>
    /// <returns>序列化后的单行 JSON。</returns>
    private static string TokenCountLine(
        DateTimeOffset timestamp,
        long input,
        long cached,
        long write,
        long output,
        long reasoning) =>
        JsonSerializer.Serialize(new
        {
            timestamp,
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new
                {
                    last_token_usage = new
                    {
                        input_tokens = input,
                        cached_input_tokens = cached,
                        cache_write_input_tokens = write,
                        output_tokens = output,
                        reasoning_output_tokens = reasoning
                    }
                }
            }
        });

    /// <summary>
    /// 创建同时携带逐响应用量和 secondary 周额度快照的 token_count JSONL 行。
    /// </summary>
    /// <param name="timestamp">事件时间。</param>
    /// <param name="input">总输入 token。</param>
    /// <param name="cached">缓存读取 token。</param>
    /// <param name="write">缓存写入 token。</param>
    /// <param name="output">总输出 token。</param>
    /// <param name="reasoning">reasoning 输出 token。</param>
    /// <param name="usedPercent">周窗口已用百分比。</param>
    /// <param name="windowMinutes">周窗口分钟数。</param>
    /// <param name="resetsAt">周窗口重置时刻。</param>
    /// <returns>序列化后的单行 JSON。</returns>
    private static string TokenCountWithRateLimitsLine(
        DateTimeOffset timestamp,
        long input,
        long cached,
        long write,
        long output,
        long reasoning,
        decimal usedPercent,
        int windowMinutes,
        DateTimeOffset resetsAt) =>
        JsonSerializer.Serialize(new
        {
            timestamp,
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new
                {
                    last_token_usage = new
                    {
                        input_tokens = input,
                        cached_input_tokens = cached,
                        cache_write_input_tokens = write,
                        output_tokens = output,
                        reasoning_output_tokens = reasoning
                    }
                },
                rate_limits = new
                {
                    primary = new { used_percent = 0m, window_minutes = 300, resets_at = resetsAt.ToUnixTimeSeconds() },
                    secondary = new { used_percent = usedPercent, window_minutes = windowMinutes, resets_at = resetsAt.ToUnixTimeSeconds() }
                }
            }
        });

    /// <summary>
    /// 在系统临时目录下创建本测试独占的空目录。
    /// </summary>
    /// <returns>已创建临时目录的绝对路径。</returns>
    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CodexWeeklyQuotaMonitor.BusinessTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// 深度枚举 WinForms 控件树中指定类型的控件，用于验证用户实际可见的组合界面。
    /// </summary>
    /// <typeparam name="T">需要查找的 WinForms 控件类型。</typeparam>
    /// <param name="root">搜索起点。</param>
    /// <returns>按控件树顺序返回的所有匹配控件。</returns>
    private static IEnumerable<T> FindControls<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindControls<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// 断言两个普通值相等。
    /// </summary>
    /// <typeparam name="T">可进行默认相等比较的类型。</typeparam>
    /// <param name="expected">期望值。</param>
    /// <param name="actual">实际值。</param>
    /// <param name="name">业务断言名称。</param>
    private static void Equal<T>(T expected, T actual, string name) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{name}：期望 {expected}，实际 {actual}。");
        }
    }

    /// <summary>
    /// 断言两个 decimal 金额在允许误差内相等。
    /// </summary>
    /// <param name="expected">期望值。</param>
    /// <param name="actual">实际值。</param>
    /// <param name="tolerance">允许的绝对误差。</param>
    /// <param name="name">业务断言名称。</param>
    private static void Near(decimal expected, decimal actual, decimal tolerance, string name)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"{name}：期望 {expected}，实际 {actual}，误差上限 {tolerance}。");
        }
    }
}
