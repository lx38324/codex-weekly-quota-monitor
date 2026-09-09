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
            if (arguments.Length == 2 && string.Equals(arguments[0], "--capture-repricing", StringComparison.Ordinal))
            {
                CaptureDashboard(arguments[1], repricing: true);
                return;
            }

            if (arguments.Length == 2 && string.Equals(arguments[0], "--capture-pricing", StringComparison.Ordinal))
            {
                CapturePricingSettings(arguments[1]);
                return;
            }

            throw new InvalidDataException(
                "仅支持 --capture-dashboard <png-path> 或 --capture-pricing <png-path> 预览参数。");
        }

        var tests = new Action[]
        {
            TestAutomaticWindowsLanguageSelection,
            TestManualLanguagePersistsAndOverridesWindows,
            TestSettingsSchema2AddsAppearancePreferences,
            TestSettingsSchema4AddsBuiltInModelPrices,
            TestOfficialLongContextEstimateIsOptIn,
            TestThemeSelectionRespectsManualOverride,
            TestDashboardLayoutAtSupportedDpiScales,
            TestMetricCardWrapsAcrossFonts,
            TestDashboardFitsReportedViewport,
            TestDashboardFitsHighScaleScreenshotViewport,
            TestTrendControlsFitHighlightAndDefaultToThirtyDays,
            TestAstraShortContextStandardPricing,
            TestAstraLongContextStandardPricing,
            TestAstraFastCreditMultiplier,
            TestCustomAstraCachedPriceRepricesAndVersionsSamples,
            TestOfflineRepricingMixedResponsesAfterReload,
            TestRepricingRetainsUnrecoverableHistory,
            TestPriceSwitchKeepsVisibleCurveUntilReady,
            TestHistoricalScanCancellationAndProgress,
            TestSpeedWeightedBuckets,
            TestSpeedToolWaitAndPersistence,
            TestSpeedPanelAndSettings,
            TestCustomTimeWindows,
            TestSpeedWindowBackfill,
            TestHistoricalScanStaleFileTimeAcrossReset,
            TestReplayRejectsReducedCoverage,
            TestReplayCoverageAcrossSplitIntervals,
            TestPricingEditorRestoresBuiltInPrices,
            TestShortContextStandardPricing,
            TestLongContextStandardPricing,
            TestLongContextOfficialSurchargeCombinesWithFast,
            TestGpt56FastCreditMultiplier,
            TestGpt54FastCreditMultiplier,
            TestUnsupportedFastMultiplierFailsClosed,
            TestObservedGpt52StandardPricing,
            TestAutoReviewUsesLunaPricingEndToEnd,
            TestWeeklyWindowSelection,
            TestAutomaticWeeklyWindowPrefersCodexBucket,
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
            TestRegressionContributionScopes,
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
            TestConfiguredDefaultTierRecoversMissingHistoricalEvidence,
            TestConfiguredFastTierRecoversWithMultiplier,
            TestNewerConfigDoesNotRewriteOlderSessionTier,
            TestDelayedTierAppendTriggersRecoverableReplay,
            TestAuthoritativeCheckpointPreservesFirstObservation,
            TestHistoricalFactsMergeActiveAndArchivedRoots,
            TestHistoricalArchiveReplayBuildsMultipleWindows,
            TestHistoricalArchiveReplayApplyPreservesUncoveredSamples,
            TestHistoricalReplayExcludesResponsesAfterFirstObservation,
            TestHistoricalReplayRejectsSlidingZeroAndBuildsSamples,
            TestHistoricalReplayApplyIsIdempotent,
            TestHistoricalReplayApplyPreservesUncoveredReplaySamples,
            TestMalformedLineDoesNotBlockLaterResponse,
            TestPartialLineWaitsForNewline,
            TestTruncatedFileRestartsCursor,
            TestDeletedFileCursorIsPruned,
            TestTrayIconOpensOnlyOnLeftDoubleClick,
            TestDetailWindowCanReopenFromMenuAfterUserClose,
            TestChartGridUsesLocalSampleTime,
            TestLegacyRegressionLimitMigrationRestoresCurve,
            TestDashboardTabsSeriesAndEstimate,
            TestDashboardRangeUsesOneContributionScope
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
                FindControls<SidebarNavigationButton>(englishForm).Any(button => button.Text == "Overview & trends"),
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
        Equal(AppSettingsMigration.CurrentVersion, settings.SettingsSchemaVersion, "迁移后设置版本");
        Equal(false, settings.EnableOfficialLongContextEstimate, "旧配置迁移后不得默认开启 >272K 对比口径");
        Equal(UiLanguage.Auto, settings.Language, "旧设置默认使用自动系统语言");
        Equal(UiTheme.System, settings.Theme, "旧设置默认使用系统主题");
        Equal(
            AppSettingsMigration.DefaultMaximumSampleUsd,
            settings.Regression.MaximumSampleUsd,
            "schema 2 已迁移上限不得再次改变");
    }

    /// <summary>
    /// 验证不含模型价格字段的 schema 4 设置会显式补齐内置价格，并可直接构造当前计价配置。
    /// </summary>
    private static void TestSettingsSchema4AddsBuiltInModelPrices()
    {
        var settings = new AppSettings
        {
            SettingsSchemaVersion = 4,
            ModelPrices = []
        };
        var changed = AppSettingsMigration.Apply(settings, modelPricesPresent: false);
        var catalog = PublicApiPricing.CreateCatalog(settings.ModelPrices);

        Equal(true, changed, "schema 4 缺少价格字段时应执行迁移");
        Equal(AppSettingsMigration.CurrentVersion, settings.SettingsSchemaVersion, "价格设置迁移后的 schema");
        Equal(PublicApiPricing.GetBuiltInProfiles().Count, settings.ModelPrices.Count, "迁移后应包含全部内置模型");
        Equal(PublicApiPricing.PricingVersion, catalog.PricingVersion, "迁移后的价格应保持内置版本");
    }

    /// <summary>
    /// 验证官方 >272K 对比口径默认关闭，只有用户显式开启后才显示第二组曲线和表格列。
    /// </summary>
    private static void TestOfficialLongContextEstimateIsOptIn()
    {
        var settings = new AppSettings();
        using var form = new ChartForm(settings, _ => { });
        form.UpdateView(CreateDashboardPreviewView(), settings.Regression);
        form.ShowDashboardTab();
        Application.DoEvents();

        var officialSeries = FindControls<CheckBox>(form)
            .Where(checkBox => checkBox.Text.Contains("官方长", StringComparison.Ordinal))
            .ToArray();
        Equal(2, officialSeries.Length, "图表应保留两项可选的官方长上下文系列");
        Equal(true, officialSeries.All(checkBox => !checkBox.Visible), "默认不得显示官方长上下文系列");
        var officialColumns = FindControls<DataGridView>(form).Single().Columns
            .Cast<DataGridViewColumn>()
            .Where(column => column.DataPropertyName.Contains("OfficialLongContext", StringComparison.Ordinal))
            .ToArray();
        Equal(2, officialColumns.Length, "表格应保留两列官方长上下文字段");
        Equal(true, officialColumns.All(column => !column.Visible), "默认不得显示官方长上下文表格列");
        var estimate = FindControls<Label>(form)
            .Single(label => label.Name == "RegressionEstimateLabel");
        Equal(false, estimate.Text.Contains("272K", StringComparison.Ordinal), "默认额度横幅不得出现 >272K 对比");

        settings.EnableOfficialLongContextEstimate = true;
        form.ApplyPreferences(settings);
        Application.DoEvents();
        Equal(true, officialSeries.All(checkBox => checkBox.Visible), "显式启用后应显示官方长上下文系列");
        Equal(true, officialColumns.All(column => column.Visible), "显式启用后应显示官方长上下文表格列");
        Equal(true, estimate.Text.Contains("272K", StringComparison.Ordinal), "显式启用后额度横幅应显示 >272K 对比");
        form.Hide();
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
            form.ClientSize = new Size(1160, 730);
            form.UpdateView(
                CreateDashboardPreviewView(),
                new RegressionOptions { Mode = RegressionMode.GaussianAggregation });
            form.Scale(new SizeF(scale, scale));
            form.ClientSize = new Size(
                (int)Math.Round(1160 * scale),
                (int)Math.Round(730 * scale));
            form.PerformLayout();
            Equal(1, FindControls<ApplicationSidebar>(form).Count(), $"{scale:P0} 缩放下应保留唯一科技侧栏");
            Equal(4, FindControls<SidebarNavigationButton>(form).Count(), $"{scale:P0} 缩放下应保留包含速度监测的四个业务入口");
            form.ShowDashboardTab();
            Application.DoEvents();
            Equal(DashboardSection.Dashboard, form.SelectedSection, $"{scale:P0} 缩放下总览可达");
            AssertMetricCardsFullyVisible(form, $"{scale:P0} 缩放");
            var grid = FindControls<DataGridView>(form).Single();
            var headerTextHeight = TextRenderer.MeasureText("本地时间", grid.Font).Height;
            var rowTextHeight = TextRenderer.MeasureText("2026-08-27 17:03:00", grid.Font).Height;
            Equal(
                true,
                grid.ColumnHeadersHeight >= headerTextHeight,
                $"{scale:P0} 缩放下表头高度应随字体增长");
            Equal(
                true,
                grid.Rows.Cast<DataGridViewRow>().All(row => row.Height >= rowTextHeight),
                $"{scale:P0} 缩放下数据行高度应随字体增长");
            form.ShowChartTab();
            Equal(DashboardSection.Dashboard, form.SelectedSection, $"{scale:P0} 缩放下旧图表入口应落到合并工作台");
            form.ShowSettingsTab(new AppSettings());
            Equal(DashboardSection.Settings, form.SelectedSection, $"{scale:P0} 缩放下设置可达");
            form.ShowDiagnosticsTab();
            Equal(DashboardSection.Diagnostics, form.SelectedSection, $"{scale:P0} 缩放下诊断可达");
            form.Hide();
        }
    }

    /// <summary>
    /// 验证每张指标卡的首选高度和所有文字矩形均落在卡片客户区内。
    /// </summary>
    /// <param name="form">已经显示并完成布局的主窗口。</param>
    /// <param name="context">用于失败信息区分 DPI 档位的上下文。</param>
    private static void AssertMetricCardsFullyVisible(ChartForm form, string context)
    {
        var cards = FindControls<MetricCard>(form).ToArray();
        Equal(5, cards.Length, $"{context}应显示五张核心指标卡");
        foreach (var card in cards)
        {
            Equal(
                true,
                card.Height >= card.GetPreferredSize(new Size(card.Width, 0)).Height,
                $"{context}指标卡 {card.AccessibleName} 高度不得低于字体首选高度");
            foreach (var label in FindControls<Label>(card))
            {
                var labelRectangle = card.RectangleToClient(label.RectangleToScreen(label.ClientRectangle));
                Equal(
                    true,
                    card.ClientRectangle.Contains(labelRectangle),
                    $"{context}指标卡 {card.AccessibleName} 的文字 {label.Text} 不得被裁切");
            }
        }
    }

    /// <summary>复现服务器默认字体及英文长说明下的窄卡片，验证按约束宽度换行且高度可完整容纳文字。</summary>
    private static void TestMetricCardWrapsAcrossFonts()
    {
        foreach (var family in new[] { "Segoe UI", "Microsoft Sans Serif" })
        foreach (var text in new[] { "当前权威周额度窗口", "Current authoritative weekly allowance" })
        {
            using var card = new MetricCard();
            using var font = new Font(family, 9F);
            card.Font = font;
            card.SetContent("Used", "3.00%", text);
            card.Size = card.GetPreferredSize(new Size(145, 0));
            card.CreateControl();
            card.PerformLayout();
            foreach (var label in FindControls<Label>(card))
            {
                var bounds = card.RectangleToClient(label.RectangleToScreen(label.ClientRectangle));
                Equal(true, card.ClientRectangle.Contains(bounds), $"{family} 字体说明必须完整位于卡片内");
                Equal(true, label.Height >= label.GetPreferredSize(new Size(label.Width, 0)).Height,
                    $"{family} 字体必须为换行保留足够高度");
            }
        }
    }

    /// <summary>
    /// 验证用户截图对应的 1160×730 客户区内五张指标卡、趋势图和连接徽标完整可见。
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
        Equal(5, cards.Length, "合并工作台默认应显示五张核心指标卡");
        AssertMetricCardsFullyVisible(form, "1160×730 视口");
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
                FindControls<Label>(card).Any(label => label.Font.Size >=
                    (card.Style == MetricCardStyle.Hero ? 20F : 16F)),
                $"指标卡 {card.AccessibleName} 应保留醒目的大号主值");
        }

        var badge = FindControls<ConnectionBadge>(form).Single();
        var chart = FindControls<QuotaChartControl>(form).Single();
        var visibleForm = form.RectangleToScreen(form.ClientRectangle);
        Equal(true, visibleForm.Contains(badge.RectangleToScreen(badge.ClientRectangle)), "连接状态徽标不得被裁切");
        Equal(true, visibleForm.Contains(chart.RectangleToScreen(chart.ClientRectangle)), "趋势图必须与指标卡同屏且不得被裁切");
        form.Hide();
    }

    /// <summary>
    /// 验证 125% 内容缩放后品牌、状态徽标和样本说明完整显示；时间预期转换为测试主机本地时区。
    /// </summary>
    private static void TestDashboardFitsHighScaleScreenshotViewport()
    {
        using var form = new ChartForm(new AppSettings(), _ => { });
        form.ClientSize = new Size(1261, 773);
        form.UpdateView(CreateDashboardPreviewView(), new RegressionOptions { Mode = RegressionMode.GaussianAggregation });
        form.Scale(new SizeF(1.25F, 1.25F));
        form.ClientSize = new Size(1261, 773);
        form.ShowDashboardTab();
        Application.DoEvents();

        var sidebar = FindControls<ApplicationSidebar>(form).Single();
        var product = FindControls<Label>(sidebar).Single(label => label.Name == "SidebarProductLabel");
        var productRectangle = sidebar.RectangleToClient(product.RectangleToScreen(product.ClientRectangle));
        Equal(true, sidebar.ClientRectangle.Contains(productRectangle), "125% 缩放下侧栏产品名不得横向裁切");

        var badge = FindControls<ConnectionBadge>(form).Single();
        var localTime = new DateTimeOffset(2026, 9, 1, 12, 34, 56, TimeSpan.FromHours(8)).LocalDateTime;
        Equal($"已连接 · {localTime:HH:mm:ss}", badge.Text, "连接徽标应使用本地时区、不含上午/下午前缀的紧凑 24 小时文本");
        var badgePreferredSize = badge.GetPreferredSize(Size.Empty);
        Equal(true, badge.Width >= badgePreferredSize.Width, "连接徽标实际宽度不得小于完整文本首选宽度");

        AssertMetricCardsFullyVisible(form, "1261×773 客户区的 125% 内容缩放");
        var sampleCard = FindControls<MetricCard>(form)
            .Single(card => card.AccessibleName?.StartsWith(UiText.Get("CardSamples"), StringComparison.Ordinal) == true);
        var sampleCaption = FindControls<Label>(sampleCard)
            .Single(label => label.Text == UiText.Format("DashboardSamplesCaption", 6, 2));
        var sampleCaptionRectangle = sampleCard.RectangleToClient(
            sampleCaption.RectangleToScreen(sampleCaption.ClientRectangle));
        Equal(true, sampleCard.ClientRectangle.Contains(sampleCaptionRectangle), "有效样本说明不得在卡片右侧被截断");
        form.Hide();
    }

    /// <summary>
    /// 验证高缩放下系列按钮完整容纳末字、选中状态具有实色层级，且首次时间范围为最近 30 天。
    /// </summary>
    private static void TestTrendControlsFitHighlightAndDefaultToThirtyDays()
    {
        using var form = new ChartForm(new AppSettings(), _ => { });
        form.ClientSize = new Size(1261, 773);
        form.UpdateView(CreateDashboardPreviewView(), new RegressionOptions { Mode = RegressionMode.GaussianAggregation });
        form.Scale(new SizeF(1.25F, 1.25F));
        form.ClientSize = new Size(1261, 773);
        form.ShowDashboardTab();
        Application.DoEvents();

        var timeRange = FindControls<ComboBox>(form)
            .Single(comboBox => comboBox.Name == "HistoryRangeComboBox");
        Equal(UiText.Get("TimeRange30Days"), timeRange.Text, "首次打开趋势图应默认显示最近 30 天");

        var visibleSeriesOptions = FindControls<CheckBox>(form)
            .Where(option => option.Appearance == Appearance.Button && option.Visible)
            .ToArray();
        Equal(2, visibleSeriesOptions.Length, "默认应显示基础样本和基础回归两个系列按钮");
        foreach (var option in visibleSeriesOptions)
        {
            var textSize = TextRenderer.MeasureText(
                option.Text,
                option.Font,
                Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            Equal(
                true,
                option.ClientSize.Width >= textSize.Width + option.Padding.Horizontal,
                $"系列按钮“{option.Text}”必须完整容纳全部文字");
            Equal(Color.White.ToArgb(), option.ForeColor.ToArgb(), $"选中的“{option.Text}”应使用白色高对比文字");
            Equal(2, option.FlatAppearance.BorderSize, $"选中的“{option.Text}”应使用 2 像素强调边框");
            var selectedBackground = option.BackColor;

            option.Checked = false;
            Application.DoEvents();
            Equal(
                false,
                option.BackColor.ToArgb() == selectedBackground.ToArgb(),
                $"取消选择“{option.Text}”后背景必须明显变化");
            Equal(
                false,
                option.ForeColor.ToArgb() == Color.White.ToArgb(),
                $"取消选择“{option.Text}”后文字应弱化");
            Equal(1, option.FlatAppearance.BorderSize, $"取消选择“{option.Text}”后应恢复 1 像素中性边框");
            option.Checked = true;
        }

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
        var samples = Enumerable.Range(0, 24)
            .Select(index => new QuotaSample(
                timestamp.AddMinutes(index),
                "codex",
                index + 1,
                1m,
                18m + index * 0.03m,
                1800m + index * 3m,
                new TokenUsage(120_000 + index * 4_000, 80_000, 0, 12_000, 0),
                1,
                "gpt-5.6-sol")
            {
                AmountDefinition = PublicApiPricing.AmountDefinition,
                OfficialLongContextIntervalApiEquivalentUsd = 22.5m + index * 0.03m,
                OfficialLongContextEstimatedWeeklyQuotaUsd = 2250m + index * 3m,
                OfficialLongContextAmountDefinition = PublicApiPricing.OfficialLongContextAmountDefinition,
                PricingVersion = PublicApiPricing.PricingVersion,
                ServiceTiers = "standard",
                CreditMultipliers = "1x",
                SampleSource = HistoricalReplayCalculator.HistoricalSampleSource
            })
            .ToArray();
        return new MonitorViewSnapshot(
            timestamp,
            "preview",
            new RateLimitSnapshot(timestamp, "codex", "Codex", 3m, 10080, timestamp.AddDays(1).AddHours(19).AddMinutes(47)),
            1873.68m,
            24,
            0,
            0,
            samples,
            baseCurve)
        {
            OfficialLongContextEstimatedWeeklyQuotaUsd = 2317.14m,
            OfficialLongContextRegressionCurve = officialCurve,
            ArchivedSampleCount = 6,
            HistoricalReplayUnattributedUsedPercents = [21m, 22m],
            AppServerConnected = true
        };
    }

    /// <summary>
    /// 在真实 WinForms 句柄和当前 Windows DPI 下渲染总览页 PNG，供发布前人工检查布局与视觉层级。
    /// </summary>
    /// <param name="outputPath">需要写入的 PNG 绝对或相对路径。</param>
    private static void CaptureDashboard(string outputPath, bool repricing = false)
    {
        using var form = new ChartForm(new AppSettings(), _ => { });
        var view = CreateDashboardPreviewView();
        if (repricing) view = view with
        {
            ShowingPreviousPrices = true,
            RepricingProgressPercent = 23,
            Status = UiText.Get("RepricingShowingPrevious") + " " + UiText.Get("RepricingArchive") + " 423/1810"
        };
        form.UpdateView(view, new RegressionOptions { Mode = RegressionMode.GaussianAggregation });
        form.ShowDashboardTab();
        Application.DoEvents();

        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        var absolutePath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        bitmap.Save(absolutePath, System.Drawing.Imaging.ImageFormat.Png);
        form.Hide();
        Console.WriteLine(
            $"总览预览已写入：{absolutePath}；窗口 DPI={form.DeviceDpi}，尺寸={form.Width}×{form.Height}");
    }

    /// <summary>
    /// 在真实 WinForms 句柄和当前 Windows DPI 下渲染模型价格设置页，供发布前检查滚动、间距和文字完整性。
    /// </summary>
    /// <param name="outputPath">需要写入的 PNG 绝对或相对路径。</param>
    private static void CapturePricingSettings(string outputPath)
    {
        var settings = new AppSettings();
        using var form = new ChartForm(settings, _ => { });
        form.ShowSettingsTab(settings);
        Application.DoEvents();
        var pricingTabs = FindControls<TabControl>(form)
            .Single(control => control.TabPages.Cast<TabPage>()
                .Any(page => page.Text == UiText.Get("SettingsPricing")));
        pricingTabs.SelectedTab = pricingTabs.TabPages.Cast<TabPage>()
            .Single(page => page.Text == UiText.Get("SettingsPricing"));
        Application.DoEvents();

        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        var absolutePath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        bitmap.Save(absolutePath, System.Drawing.Imaging.ImageFormat.Png);
        form.Hide();
        Console.WriteLine(
            $"价格设置预览已写入：{absolutePath}；窗口 DPI={form.DeviceDpi}，尺寸={form.Width}×{form.Height}");
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
            EnableOfficialLongContextEstimate = true,
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
            AmountDefinition = PublicApiPricing.AmountDefinition,
            OfficialLongContextIntervalApiEquivalentUsd = 26m,
            OfficialLongContextEstimatedWeeklyQuotaUsd = 2600m,
            OfficialLongContextAmountDefinition = PublicApiPricing.OfficialLongContextAmountDefinition,
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
        Equal(DashboardSection.Dashboard, form.SelectedSection, "托盘图表入口应直达合并后的概览与趋势工作台");

        var estimate = FindControls<Label>(form)
            .Single(label => label.Name == "RegressionEstimateLabel");
        Equal(true, estimate.Text.Contains("Standard 约 $2,100.00", StringComparison.Ordinal), "图外应显示 Standard 当前回归额度");
        Equal(true, estimate.Text.Contains(">272K 约 $2,600.00", StringComparison.Ordinal), "图外应显示官方长上下文当前回归额度");
        var summary = FindControls<Label>(form)
            .Single(label => label.Name == "HistorySummaryLabel");
        Equal(true, summary.Text.Contains("权威已用 18.00%", StringComparison.Ordinal), "图表外应显示当前权威百分比");
        Equal(true, summary.Text.Contains("最新有效点 16.00%", StringComparison.Ordinal), "图表外应区分最新有效金额点");
        Equal(true, summary.Text.Contains("待归因 1", StringComparison.Ordinal), "图表外应显示待恢复额度点数量");

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
        Equal(4, FindControls<SidebarNavigationButton>(form).Count(), "科技侧栏应提供包含速度监测的四个一级业务入口");
        form.Hide();
    }

    /// <summary>
    /// 验证默认 30 天和全部时间会分别重算同一份估值、贡献计数与曲线高亮，并保持卡片和横幅口径一致。
    /// </summary>
    private static void TestDashboardRangeUsesOneContributionScope()
    {
        var now = DateTimeOffset.Now;
        var oldSamples = Enumerable.Range(0, 10)
            .Select(index => CurrentSample(now.AddDays(-60 + index), 500m + index * 10m));
        var recentSamples = Enumerable.Range(0, 21)
            .Select(index => CurrentSample(now.AddDays(-20 + index), 2000m + index * 10m))
            .ToArray();
        var allSamples = oldSamples.Concat(recentSamples).ToArray();
        var options = new RegressionOptions
        {
            Mode = RegressionMode.Linear,
            LinearLookbackPoints = 100,
            MaximumSampleUsd = AppSettingsMigration.DefaultMaximumSampleUsd
        };
        var allAnalysis = RegressionCalculator.Analyze(allSamples, options);
        var recentAnalysis = RegressionCalculator.Analyze(recentSamples, options);
        var view = new MonitorViewSnapshot(
            now,
            "测试",
            new RateLimitSnapshot(now, "codex", "Codex", 8m, 10080, now.AddDays(7)),
            allAnalysis.CurrentEstimate,
            allSamples.Length,
            0,
            0,
            allSamples,
            allAnalysis.Curve);
        var settings = new AppSettings { Regression = options };

        using var form = new ChartForm(settings, _ => { });
        form.UpdateView(view, options);
        var estimateCard = FindControls<MetricCard>(form)
            .Single(card => card.Tone == MetricCardTone.Primary && card.Style == MetricCardStyle.Hero);
        var estimateLabel = FindControls<Label>(form)
            .Single(label => label.Name == "RegressionEstimateLabel");
        var chart = FindControls<QuotaChartControl>(form).Single();
        Equal(21, chart.CurrentContributions.Count, "默认 30 天图表应接收 21 个贡献样本");
        Equal(
            true,
            estimateCard.AccessibleName!.Contains(UiText.Format("DashboardEstimateCaption", 21), StringComparison.Ordinal),
            "默认 30 天额度卡应显示 21 个当前估值贡献样本");
        Equal(
            true,
            estimateLabel.Text.Contains(
                UiText.Format("EstimateWithPoints", recentAnalysis.CurrentEstimate!.Value, 21),
                StringComparison.Ordinal),
            "默认 30 天横幅应显示同一金额和 21 个贡献样本");

        using (var renderedChart = new QuotaChartControl { Size = new Size(900, 360) })
        using (var bitmap = new Bitmap(renderedChart.Size.Width, renderedChart.Size.Height))
        {
            renderedChart.SetSeriesVisibility(new(true, true, false, false));
            renderedChart.SetData(recentSamples, recentAnalysis.Curve, [], recentAnalysis.CurrentContributions);
            renderedChart.CreateControl();
            renderedChart.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            var amberPixels = 0;
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (pixel.R > 170 && pixel.G > 75 && pixel.G < 220 && pixel.B < 130 &&
                        pixel.R > pixel.G + 20 && pixel.G > pixel.B + 20)
                    {
                        amberPixels++;
                    }
                }
            }

            Equal(true, amberPixels > 20, "图表应以琥珀色圆环标出当前估值贡献样本");
        }

        var range = FindControls<ComboBox>(form)
            .Single(comboBox => comboBox.Name == "HistoryRangeComboBox");
        range.SelectedIndex = 0;
        Equal(allSamples.Length, chart.CurrentContributions.Count, "切换全部后图表贡献样本数应同步扩大");
        Equal(
            true,
            estimateCard.AccessibleName!.Contains(
                UiText.Format("DashboardEstimateCaption", allSamples.Length),
                StringComparison.Ordinal),
            "切换全部后额度卡贡献计数应同步更新");
        Equal(
            true,
            estimateLabel.Text.Contains(
                UiText.Format("EstimateWithPoints", allAnalysis.CurrentEstimate!.Value, allSamples.Length),
                StringComparison.Ordinal),
            "切换全部后横幅应显示同一全历史金额和贡献计数");
        Equal(false, recentAnalysis.CurrentEstimate == allAnalysis.CurrentEstimate, "切换时间范围必须重新计算金额而非裁切旧曲线");
    }

    /// <summary>
    /// 验证 Astra 短上下文按官方 Standard 价分别计算未缓存输入、缓存读取、缓存写入和输出。
    /// </summary>
    /// <remarks>输入由方法内构造；无返回值，任一价格分项或口径不符合官方价格表时由断言报告失败。</remarks>
    private static void TestAstraShortContextStandardPricing()
    {
        var usage = new TokenUsage(100_000, 50_000, 10_000, 10_000, 2_000);
        var result = PublicApiPricing.Calculate("gpt-6-astra", "standard", usage);
        Equal(true, result.Success, "Astra 短上下文应可定价");
        Near(1.075m, result.StandardCostUsd, 0.000001m, "Astra Standard 短上下文金额");
        Near(1.075m, result.CostUsd, 0.000001m, "Astra Standard 等价金额");
        Near(1.075m, result.OfficialLongContextCostUsd, 0.000001m, "Astra 短上下文两种口径应一致");
        Near(1m, result.CreditMultiplier, 0.000001m, "Astra Standard 额度倍率");
    }

    /// <summary>
    /// 验证 Astra 输入超过 272K 后整次请求使用官方长上下文输入、缓存读取、缓存写入和输出价格。
    /// </summary>
    /// <remarks>输入由方法内构造；无返回值，基础口径、官方长上下文口径或阈值判断错误时由断言报告失败。</remarks>
    private static void TestAstraLongContextStandardPricing()
    {
        var usage = new TokenUsage(300_000, 200_000, 50_000, 10_000, 2_000);
        var result = PublicApiPricing.Calculate("gpt-6-astra", "default", usage);
        Equal(true, result.Success, "Astra 长上下文应可定价");
        Near(1.825m, result.CostUsd, 0.000001m, "Astra 不含长上下文加价金额");
        Near(3.4m, result.OfficialLongContextCostUsd, 0.000001m, "Astra 官方长上下文加价金额");
        Equal(true, result.IsLongContext, "Astra 超过 272K 应标记为长上下文");
    }

    /// <summary>
    /// 验证 Astra Fast 使用 Standard API 成本乘以官方 ChatGPT 2.5 倍额度倍率。
    /// </summary>
    /// <remarks>输入由方法内构造；无返回值，Standard 基价、Fast 倍率或最终等价金额错误时由断言报告失败。</remarks>
    private static void TestAstraFastCreditMultiplier()
    {
        var usage = new TokenUsage(100_000, 50_000, 10_000, 10_000, 2_000);
        var result = PublicApiPricing.Calculate("gpt-6-astra", "priority", usage);
        Equal(true, result.Success, "Astra Fast 应可定价");
        Near(1.075m, result.StandardCostUsd, 0.000001m, "Astra Fast 响应的 Standard API 成本");
        Near(2.5m, result.CreditMultiplier, 0.000001m, "Astra Fast 额度倍率");
        Near(2.6875m, result.CostUsd, 0.000001m, "Astra Standard API 等价金额");
        Equal("fast", result.NormalizedServiceTier, "Astra priority 应规范化为 fast");
    }

    /// <summary>
    /// 验证用户提高 Astra 缓存读取价格后，逐响应金额、持久化往返、价格版本和历史重放条件同步变化。
    /// </summary>
    private static void TestCustomAstraCachedPriceRepricesAndVersionsSamples()
    {
        var customProfiles = PublicApiPricing.GetBuiltInProfiles()
            .Select(profile => string.Equals(profile.Model, "gpt-6-astra", StringComparison.OrdinalIgnoreCase)
                ? profile with
                {
                    ShortContext = profile.ShortContext with { CachedInputPerMillion = 2m }
                }
                : profile)
            .Reverse()
            .ToList();
        var firstCatalog = PublicApiPricing.CreateCatalog(customProfiles);
        var json = JsonSerializer.Serialize(new AppSettings { ModelPrices = customProfiles });
        var restoredSettings = JsonSerializer.Deserialize<AppSettings>(json)
            ?? throw new InvalidOperationException("自定义模型价格设置反序列化结果为空。");
        var restoredCatalog = PublicApiPricing.CreateCatalog(restoredSettings.ModelPrices);
        var result = restoredCatalog.Calculate(
            "gpt-6-astra",
            "standard",
            new TokenUsage(100_000, 100_000, 0, 0, 0));
        var directory = CreateTemporaryDirectory();
        var timestamp = DateTimeOffset.UtcNow;
        File.WriteAllLines(Path.Combine(directory, "rollout-custom-astra.jsonl"),
        [
            TurnContextLine(timestamp, "gpt-6-astra", "standard"),
            ResponseItemLine(timestamp.AddSeconds(1)),
            TokenCountLine(timestamp.AddSeconds(2), 100_000, 100_000, 0, 0, 0)
        ]);
        var scan = new RolloutLogReader(restoredCatalog).ScanNew(
            directory,
            new MonitorState { RolloutFilesPrimed = true });
        var estimatorState = new MonitorState();
        var resetAt = timestamp.AddDays(7);
        QuotaEstimator.Process(
            estimatorState,
            new RateLimitSnapshot(timestamp, "codex", "Codex", 4m, 10080, resetAt),
            RolloutScanResult.Empty,
            0.1m,
            restoredCatalog.PricingVersion);
        var update = QuotaEstimator.Process(
            estimatorState,
            new RateLimitSnapshot(timestamp.AddSeconds(3), "codex", "Codex", 5m, 10080, resetAt),
            scan,
            0.1m,
            restoredCatalog.PricingVersion);
        var historicalReader = new RolloutLogReader(restoredCatalog);
        var historicalFacts = historicalReader.ReadHistoricalFacts(
            directory,
            timestamp,
            timestamp.AddSeconds(3),
            10080);
        var historicalSnapshot = new RateLimitSnapshot(
            timestamp.AddSeconds(3),
            "codex",
            "Codex",
            5m,
            10080,
            resetAt);
        var replay = HistoricalReplayCalculator.Build(
            historicalSnapshot,
            historicalFacts,
            [
                new(timestamp, "codex", 4m, 10080, resetAt),
                new(timestamp.AddSeconds(3), "codex", 5m, 10080, resetAt)
            ]);
        var sample = new QuotaSample(
            DateTimeOffset.Now,
            "codex",
            1m,
            1m,
            result.CostUsd,
            result.CostUsd * 100,
            new TokenUsage(100_000, 100_000, 0, 0, 0),
            1,
            "gpt-6-astra")
        {
            AmountDefinition = PublicApiPricing.AmountDefinition,
            OfficialLongContextAmountDefinition = PublicApiPricing.OfficialLongContextAmountDefinition,
            PricingVersion = restoredCatalog.PricingVersion
        };
        var state = new MonitorState
        {
            HistoricalReplayVersion = HistoricalReplayCalculator.CurrentReplayVersion,
            HistoricalReplayPricingVersion = PublicApiPricing.PricingVersion
        };

        Equal(true, result.Success, "自定义 Astra 缓存读取价格应可计价");
        Near(0.2m, result.CostUsd, 0.000001m, "自定义 Astra 缓存读取金额");
        Near(0.2m, scan.ApiEquivalentUsd, 0.000001m, "增量日志扫描必须使用自定义价格");
        Equal(restoredCatalog.PricingVersion, scan.PricingVersion, "扫描结果应携带自定义价格版本");
        Equal(true, update.SampleCreated, "自定义价格扫描应形成额度样本");
        Equal(true, SampleRepricer.HasCompleteUsage(update.Sample!), "实时采样必须持久化完整逐响应计价明细");
        Equal(restoredCatalog.PricingVersion, update.Sample!.PricingVersion, "实时样本应保存自定义价格版本");
        Equal(restoredCatalog.PricingVersion, historicalFacts.PricingVersion, "历史事实应携带自定义价格版本");
        Equal(1, replay.Samples.Count, "自定义价格历史事实应重建一个样本");
        Equal(true, SampleRepricer.HasCompleteUsage(replay.Samples[0]), "历史重放必须补齐可离线重算明细");
        Equal(restoredCatalog.PricingVersion, replay.Samples[0].PricingVersion, "历史重放样本应保存自定义价格版本");
        Equal(false, firstCatalog.PricingVersion == PublicApiPricing.PricingVersion, "自定义价格必须生成独立版本");
        Equal(firstCatalog.PricingVersion, restoredCatalog.PricingVersion, "价格版本不得受列表顺序或 JSON 往返影响");
        Equal(true, restoredCatalog.IsCurrentSample(sample), "自定义版本样本应进入对应回归口径");
        Equal(false, PublicApiPricing.IsCurrentSample(sample), "自定义版本样本不得混入内置价格回归");
        Equal(
            true,
            HistoricalReplayCalculator.IsReplayRequired(state, restoredCatalog.PricingVersion),
            "切换自定义价格后必须触发历史重算");
    }

    /// <summary>
    /// 验证价格页专用复位按钮会恢复全部内置模型价格，并保留为等待全局保存的编辑值。
    /// </summary>
    private static void TestPricingEditorRestoresBuiltInPrices()
    {
        var customProfiles = PublicApiPricing.GetBuiltInProfiles()
            .Select(profile => string.Equals(profile.Model, "gpt-6-astra", StringComparison.OrdinalIgnoreCase)
                ? profile with
                {
                    ShortContext = profile.ShortContext with { CachedInputPerMillion = 2m }
                }
                : profile)
            .ToArray();
        using var editor = new PricingEditorPanel();
        editor.LoadProfiles(customProfiles);
        var resetButton = FindControls<Button>(editor)
            .Single(button => button.Name == "RestoreBuiltInPricesButton");
        resetButton.PerformClick();
        var restoredProfiles = editor.GetProfiles();
        var astra = restoredProfiles.Single(profile => profile.Model == "gpt-6-astra");

        Near(1m, astra.ShortContext.CachedInputPerMillion, 0.000001m, "复位后的 Astra 内置缓存读取价格");
        Equal(
            PublicApiPricing.PricingVersion,
            PublicApiPricing.CreateCatalog(restoredProfiles).PricingVersion,
            "价格专用复位应完整恢复内置价格版本");
        Equal(
            UiText.Get("PricingRestoredPending"),
            FindControls<Label>(editor).Single(label => label.Text == UiText.Get("PricingRestoredPending")).Text,
            "复位后应明确提示保存后生效");
    }

    /// <summary>验证混合模型、速度和上下文长度经 JSON 往返后，无原始日志也能按新价格及阈值重算。</summary>
    private static void TestOfflineRepricingMixedResponsesAfterReload()
    {
        var timestamp = DateTimeOffset.Now;
        ResponsePricingUsage[] details =
        [
            new("gpt-6-astra", "standard", new(100_000, 80_000, 1_000, 500, 100)),
            new("gpt-6-astra", "priority", new(300_000, 200_000, 20_000, 1_000, 300)),
            new("gpt-5.6-sol", "standard", new(200_000, 100_000, 10_000, 2_000, 500))
        ];
        var responses = details.Select((item, index) =>
        {
            var pricing = PublicApiPricing.BuiltInCatalog.Calculate(item.Model, item.ServiceTier, item.Usage);
            return new HistoricalResponseFact(timestamp.AddSeconds(index + 1), "test", item.Model,
                item.ServiceTier, item.Usage, pricing.Success, pricing.CostUsd,
                pricing.OfficialLongContextCostUsd, pricing.NormalizedServiceTier, pricing.CreditMultiplier);
        }).ToArray();
        var reset = timestamp.AddDays(7);
        var snapshot = new RateLimitSnapshot(timestamp.AddSeconds(4), "codex", null, 11m, 10080, reset);
        var replay = HistoricalReplayCalculator.Build(snapshot,
            new HistoricalRolloutFacts(timestamp, snapshot.SampledAt, responses, [], 1, 0),
            [new(timestamp, "codex", 10m, 10080, reset), new(snapshot.SampledAt, "codex", 11m, 10080, reset)]);
        Equal(1, replay.Samples.Count, "混合响应应形成一个区间");
        var state = JsonSerializer.Deserialize<MonitorState>(JsonSerializer.Serialize(new MonitorState
        {
            Samples = replay.Samples.ToList()
        }))!;
        var custom = PublicApiPricing.CreateCatalog(PublicApiPricing.GetBuiltInProfiles().Select(profile =>
            profile.Model == "gpt-6-astra" ? profile with
            {
                ShortContext = profile.ShortContext with { CachedInputPerMillion = 3 },
                LongContextThresholdTokens = 150_000
            } : profile).ToArray());
        var result = SampleRepricer.Apply(state, custom);
        Equal(1, result.PricedIntervals, "无需日志即可重算一个混合区间");
        Equal(0, result.MissingUsageIntervals, "JSON 往返不得丢明细");
        Equal(0, result.Errors.Count, "新价格应可完整计算");
        var sample = state.Samples.Single(custom.IsCurrentSample);
        var expected = details.Select(item => custom.Calculate(item.Model, item.ServiceTier, item.Usage)).ToArray();
        Near(expected.Sum(item => item.CostUsd), sample.IntervalApiEquivalentUsd, 0.00000001m, "混合速度和模型按逐响应计价");
        Near(expected.Sum(item => item.OfficialLongContextCostUsd), sample.OfficialLongContextIntervalApiEquivalentUsd,
            0.00000001m, "长上下文以每个响应的输入长度判断，不能用区间总长度");
        SampleRepricer.Apply(state, custom);
        Equal(2, state.Samples.Count, "重复重算保留一份旧版本和一份新版本");
        SampleRepricer.Apply(state, PublicApiPricing.BuiltInCatalog);
        Equal(2, state.Samples.Count, "复位价格不得重复追加原版本");
        Near(replay.Samples[0].IntervalApiEquivalentUsd,
            state.Samples.Single(PublicApiPricing.IsCurrentSample).IntervalApiEquivalentUsd, 0.00000001m, "复位应精确还原原金额");
    }

    /// <summary>验证明细缺失或不完整时保留旧样本，不利用区间总 token 猜测新成本。</summary>
    private static void TestRepricingRetainsUnrecoverableHistory()
    {
        var old = CreateDashboardPreviewView().Samples[0];
        var partial = old with { Timestamp = old.Timestamp.AddSeconds(1), ModelResponseCount = 2, PricingUsages = [new("gpt-6-astra", "standard", old.Usage)] };
        var state = new MonitorState { Samples = [old, partial] };
        var custom = PublicApiPricing.CreateCatalog([new("gpt-6-astra", new(10, 2, 12.5m, 50), new(20, 4, 25, 75), 272_000)]);
        var result = SampleRepricer.Apply(state, custom);
        Equal(2, result.MissingUsageIntervals, "缺失或部分明细必须明确报告");
        Equal(2, state.Samples.Count, "历史不得因为无法重算而被删除");
        Equal(false, state.Samples.Any(custom.IsCurrentSample), "不可把旧价格金额贴上新版本");
        var fact = new ResponsePricingUsage("unsupported-model", "standard", new(100, 0, 0, 10, 0));
        state.Samples = [old with { Usage = fact.Usage, ModelResponseCount = 1, PricingUsages = [fact] }];
        var failed = SampleRepricer.Apply(state, custom);
        Equal(1, failed.Errors.Count, "不支持的模型必须保留原始计价失败");
        Equal(1, state.Samples.Count, "失败不得污染现有金额");
    }

    /// <summary>通过实际展示构建器验证等待、部分完成和完成后的曲线版本；测试不访问真实配置或自启。</summary>
    private static void TestPriceSwitchKeepsVisibleCurveUntilReady()
    {
        var coordinator = (MonitorCoordinator)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MonitorCoordinator));
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = typeof(MonitorCoordinator);
        var previous = CreateDashboardPreviewView();
        var catalog = PublicApiPricing.CreateCatalog([new("gpt-6-astra", new(10, 2, 12.5m, 50), new(20, 4, 25, 75), 272_000)]);
        var state = new MonitorState { Samples = previous.Samples.ToList(), HistoricalReplayPricingVersion = previous.PricingVersion };
        type.GetField("_state", flags)!.SetValue(coordinator, state);
        type.GetField("_pricingCatalog", flags)!.SetValue(coordinator, catalog);
        type.GetField("<Settings>k__BackingField", flags)!.SetValue(coordinator, new AppSettings());
        var build = type.GetMethod("BuildView", flags)!;
        var restarted = (MonitorViewSnapshot)build.Invoke(coordinator, ["waiting"] )!;
        Equal(true, restarted.ShowingPreviousPrices, "改价中重启仍应标注并展示旧曲线");
        Equal(previous.SampleCount, restarted.SampleCount, "等待期间历史数量不得归零");
        Equal(true, restarted.RegressionCurve.Count > 0, "等待期间曲线应保留");
        type.GetField("_retainedPricingView", flags)!.SetValue(coordinator, restarted);
        type.GetField("_priceRebuildPending", flags)!.SetValue(coordinator, true);
        state.Samples.Add(previous.Samples[0] with { PricingVersion = catalog.PricingVersion });
        var partial = (MonitorViewSnapshot)build.Invoke(coordinator, ["replaying"] )!;
        Equal(previous.SampleCount, partial.SampleCount, "部分新结果不得提前替换完整旧曲线");
        type.GetField("_priceRebuildPending", flags)!.SetValue(coordinator, false);
        var completed = (MonitorViewSnapshot)build.Invoke(coordinator, ["completed"] )!;
        Equal(false, completed.ShowingPreviousPrices, "完成后切换新价格结果");
        Equal(1, completed.SampleCount, "新旧版本不得混合回归");
        type.GetField("_polling", flags)!.SetValue(coordinator, true);
        coordinator.RefreshNowAsync().GetAwaiter().GetResult();
        Equal(true, (bool)type.GetField("_refreshQueued", flags)!.GetValue(coordinator)!, "繁忙期间最新请求应排队而非丢失");
        using var form = new ChartForm(new AppSettings(), _ => { });
        form.UpdateView(partial, new RegressionOptions());
        Equal(true, FindControls<StatusStrip>(form).Single().Items[0].Text!.Contains("旧价格"), "各标签页均可看到旧价格状态");
    }

    /// <summary>验证取消后新扫描可完整提取并报告进度；固定文件时间，避免主机时钟与文件系统时间精度竞争。</summary>
    private static void TestHistoricalScanCancellationAndProgress()
    {
        var directory = CreateTemporaryDirectory();
        var timestamp = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var path = Path.Combine(directory, "rollout-progress.jsonl");
        File.WriteAllLines(path,
        [TurnContextLine(timestamp, "gpt-6-astra", "standard"), ResponseItemLine(timestamp.AddSeconds(1)),
            TokenCountLine(timestamp.AddSeconds(2), 1000, 500, 0, 100, 0)]);
        File.SetLastWriteTimeUtc(path, timestamp.AddSeconds(3).UtcDateTime);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = false;
        try
        {
            new RolloutLogReader().ReadHistoricalFacts(directory, timestamp, timestamp.AddSeconds(3), 10080, cancellation.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Equal(true, cancelled, "过时扫描必须取消，不能返回伪成功空结果");
        var progress = new ScanProgress();
        var facts = new RolloutLogReader().ReadHistoricalFacts(directory, timestamp, timestamp.AddSeconds(3), 10080, default, progress);
        Equal(1, facts.Responses.Count, "取消后最新扫描应正常完成");
        Equal((0, 1), progress.Values[0], "开始时报告文件总数");
        Equal((1, 1), progress.Values[^1], "结束时报告全部完成");
    }

    /// <summary>验证按总 token/总耗时聚合、推理输出扣除、模型分组及任意分钟桶跨小时边界。</summary>
    private static void TestSpeedWeightedBuckets()
    {
        var time = new DateTimeOffset(2026, 9, 8, 10, 59, 0, TimeSpan.Zero);
        SpeedSample[] samples = [
            new("a", time.AddSeconds(-100), time, "gpt-6-astra", "fast", 1000, 400),
            new("b", time, time.AddSeconds(1), "gpt-6-astra", "fast", 100, 20),
            new("c", time, time.AddMilliseconds(1), "gpt-6-astra", "fast", 500, 0),
            new("d", time, time.AddSeconds(2), "gpt-5.6-sol", "standard", 20, 0)
        ];
        var options = new SpeedOptions { Mode = SpeedAggregationMode.ResponseCount, ResponsesPerBucket = 2 };
        var buckets = SpeedMonitoring.Aggregate(samples, options, time.AddMinutes(10));
        Equal(2, buckets.Count, "模型与层级必须分组，短于一秒的极端值不得计入");
        var astra = buckets.Single(bucket => bucket.Model == "gpt-6-astra");
        Near(1100m / 101m, (decimal)astra.TotalTps, 0.000001m, "总 TPS 必须按响应耗时加权，而非平均每次 TPS");
        Near(680m / 101m, (decimal)astra.VisibleTps, 0.000001m, "可见输出必须扣除 reasoning token");
        Equal(true, astra.Complete, "满足 N 次响应后完成一组");
        Equal(false, buckets.Single(bucket => bucket.Model == "gpt-5.6-sol").Complete, "未满 N 次标记进行中");
        options.Mode = SpeedAggregationMode.TimeWindow; options.BucketMinutes = 90;
        var period = SpeedMonitoring.Aggregate(samples, options, time.AddMinutes(10)).First();
        Equal(90d, (period.End - period.Start).TotalMinutes, "超过六十分钟的时间桶也应保持准确长度");
        Equal(false, period.Complete, "自然时间桶尚未结束不能标记已完成");
    }

    /// <summary>复现工具长时间运行、心跳、重复读取和跨重启半行；独立速度状态不得推进或修改旧输入。</summary>
    private static void TestSpeedToolWaitAndPersistence()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "rollout-speed.jsonl");
        var time = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);
        File.WriteAllLines(path, [
            EventMessageLine(time, "task_started"), TurnContextLine(time, "gpt-6-astra", "priority"),
            JsonSerializer.Serialize(new { timestamp = time.AddSeconds(5), type = "response_item",
                payload = new { type = "function_call", call_id = "tool-1" } }),
            TokenCountLine(time.AddSeconds(6), 1000, 500, 0, 500, 100),
            TokenHeartbeatLine(time.AddSeconds(7)),
            JsonSerializer.Serialize(new { timestamp = time.AddSeconds(100), type = "response_item",
                payload = new { type = "function_call_output", call_id = "tool-1" } }),
            ResponseItemLine(time.AddSeconds(110)),
            TokenCountLine(time.AddSeconds(111), 1000, 500, 0, 400, 0)
        ]);
        File.SetLastWriteTimeUtc(path, time.AddDays(-1).UtcDateTime);
        var original = new SpeedState();
        var state = SpeedMonitoring.Scan(original, [root], time.AddHours(1), 24);
        Equal(0, original.Cursors.Count, "速度扫描失败前或成功后都不得原地修改输入游标");
        Equal(2, state.Samples.Count, "心跳不得重复采样，旧 mtime 不得漏掉响应");
        Equal(5d, state.Samples[0].DurationSeconds, "首个响应耗时五秒");
        Equal(10d, state.Samples[1].DurationSeconds, "第二次响应必须排除九十五秒工具等待");
        Equal("fast", state.Samples[1].ServiceTier, "保留日志明确的 Fast 层级");
        state = JsonSerializer.Deserialize<SpeedState>(JsonSerializer.Serialize(state))!;
        Equal(2, SpeedMonitoring.Scan(state, [root], time.AddHours(1), 24).Samples.Count, "重启后不重复既有样本");
        File.AppendAllText(path, ResponseItemLine(time.AddSeconds(120)) + "\n" +
            TokenCountLine(time.AddSeconds(121), 1000, 500, 0, 90, 0));
        state = SpeedMonitoring.Scan(state, [root], time.AddHours(1), 24);
        Equal(2, state.Samples.Count, "半行 token 不得提前计入");
        File.AppendAllText(path, "\n");
        state = SpeedMonitoring.Scan(state, [root], time.AddHours(1), 24);
        Equal(3, state.Samples.Count, "补齐换行后恢复速度采样");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelled = false;
        try { SpeedMonitoring.Scan(state, [root], time.AddHours(1), 24, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Equal(true, cancelled, "取消应显式报告，不返回伪成功空记录");
    }

    /// <summary>验证速度参数序列化后保留，速度页可在高缩放下访问，且明确显示无 TTFT 证据。</summary>
    private static void TestSpeedPanelAndSettings()
    {
        var settings = new AppSettings { Speed = new SpeedOptions { Mode = SpeedAggregationMode.ResponseCount, ResponsesPerBucket = 7 } };
        var loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Equal(7, loaded.Speed.ResponsesPerBucket, "速度设置跨重启保留");
        Equal(0, loaded.Validate().Count, "有效速度设置可保存");
        using var form = new ChartForm(settings, _ => { });
        var time = DateTimeOffset.Now;
        SpeedBucket[] buckets = [
            new(time.AddMinutes(-5), time, "gpt-6-astra", "fast", 7, 700, 400, 10, true),
            new(time.AddMinutes(-10), time.AddMinutes(-5), "gpt-6-astra", "fast", 7, 600, 300, 10, true),
            new(time.AddMinutes(-5), time, "gpt-6-astra", "standard", 7, 300, 200, 10, true),
            new(time.AddMinutes(-5), time, "gpt-5.6-sol", "standard", 7, 200, 100, 10, true)
        ];
        var view = CreateDashboardPreviewView() with { SpeedSamples = buckets.SelectMany(bucket => Enumerable.Range(0, 7)
            .Select(i => new SpeedSample($"source-{bucket.Model}-{bucket.ServiceTier}", bucket.End.AddSeconds(-14 + i),
                bucket.End.AddSeconds(-7 + i), bucket.Model, bucket.ServiceTier, 100, 30))).ToArray(), SpeedSampleCount = 28 };
        form.UpdateView(view, settings.Regression); form.ShowSpeedTab();
        form.Scale(new SizeF(1.5F, 1.5F)); Application.DoEvents();
        Equal(DashboardSection.Speed, form.SelectedSection, "速度页入口可达");
        var panel = FindControls<SpeedPanel>(form).Single();
        Equal(4, FindControls<DataGridView>(panel).Single().Rows.Count, "速度页显示每组聚合结果");
        var chart = FindControls<SpeedChartControl>(panel).Single();
        Equal(3, chart.DisplayedSeriesCount, "不同模型和层级应独立绘制三条线");
        FindControls<ComboBox>(panel).Single(box => box.Name == "SpeedModelFilter").SelectedItem = "gpt-6-astra";
        Equal(2, chart.DisplayedSeriesCount, "筛选 Astra 后只保留它的两种速度层级");
        Equal(3, FindControls<DataGridView>(panel).Single().Rows.Count, "明细必须与图表使用相同模型筛选");
        FindControls<ComboBox>(panel).Single(box => box.Name == "SpeedMetric").SelectedIndex = 1;
        Equal(2, chart.DisplayedSeriesCount, "切换可见输出 TPS 不改变模型分组");
        Equal(true, FindControls<Label>(panel).Any(label => label.Text.Contains("TTFT：不可用")), "不得把代理耗时伪装成 TTFT");
        form.Hide();
    }

    /// <summary>验证额度双边窗口实际改变回归贡献，速度先过滤再聚合，逆序输入不破坏已应用结果。</summary>
    private static void TestCustomTimeWindows()
    {
        var now = DateTimeOffset.Now;
        var options = new RegressionOptions { Mode = RegressionMode.Linear, LinearLookbackPoints = 100 };
        var points = Enumerable.Range(0, 6).Select(i => CurrentSample(now.AddDays(-10 + i), 1000 + i * 100)).ToArray();
        var view = CreateDashboardPreviewView() with { Samples = points };
        using var form = new ChartForm(new AppSettings { Regression = options }, _ => { });
        form.UpdateView(view, options);
        FindControls<ComboBox>(form).Single(box => box.Name == "HistoryRangeComboBox").SelectedIndex = 4;
        var quotaWindow = FindControls<CustomTimeWindowControl>(form).Single();
        FindControls<DateTimePicker>(quotaWindow).Single(p => p.Name == "WindowStart").Value = now.AddDays(-9).AddSeconds(-1).LocalDateTime;
        FindControls<DateTimePicker>(quotaWindow).Single(p => p.Name == "WindowEnd").Value = now.AddDays(-7).AddSeconds(1).LocalDateTime;
        Equal(true, quotaWindow.ApplyWindow(), "有效窗口可应用");
        Equal(3, FindControls<QuotaChartControl>(form).Single().CurrentContributions.Count, "额度回归必须同时限制起止时间");
        var previousStart = quotaWindow.Start;
        FindControls<DateTimePicker>(quotaWindow).Single(p => p.Name == "WindowStart").Value = now.LocalDateTime;
        Equal(false, quotaWindow.ApplyWindow(), "逆序窗口拒绝提交");
        Equal(previousStart, quotaWindow.Start, "失败保留已应用窗口");
        FindControls<DateTimePicker>(quotaWindow).Single(p => p.Name == "WindowStart").Value = previousStart.LocalDateTime;
        quotaWindow.ApplyWindow();
        form.Show(); form.Scale(new SizeF(1.5F, 1.5F));
        form.MinimumSize = new Size(900, 600); form.ClientSize = new Size(1260, 860); Application.DoEvents();
        Equal(true, FindControls<QuotaChartControl>(form).Single().Height >= 160, "150%受限视口下自定义日期区不得挤掉额度图表");
        form.Hide();

        SpeedSample[] responses = Enumerable.Range(0, 5).Select(i => new SpeedSample($"s{i}", now.AddDays(-8).AddMinutes(i).AddSeconds(-10),
            now.AddDays(-8).AddMinutes(i), "gpt-6-astra", "fast", (i + 1) * 100, 50)).ToArray();
        var start = responses[1].EndedAt;
        var end = responses[3].EndedAt;
        var grouped = SpeedMonitoring.Aggregate(responses, new SpeedOptions { HistoryHours = 24, Mode = SpeedAggregationMode.ResponseCount, ResponsesPerBucket = 2 }, now, start, end);
        Equal(3, grouped.Sum(bucket => bucket.Count), "自定义窗口可越过默认24h，且两个端点均包含");
        Equal(900L, grouped.Sum(bucket => bucket.OutputTokens), "先筛选响应再分桶，不可沿用整桶token");
        Equal(2, grouped.Count, "N响应分组须从窗口内第一条重新开始");
        Equal(false, grouped.Single(bucket => bucket.Count == 1).Complete, "尾部不足N标记部分");
        using var panel = new SpeedPanel();
        panel.UpdateView(view with { SpeedSamples = responses, SpeedSampleCount = 5 }, new SpeedOptions());
        DateTimeOffset? requested = null;
        panel.HistoryRequested += value => requested = value;
        FindControls<ComboBox>(panel).Single(box => box.Name == "SpeedRange").SelectedIndex = 4;
        var speedWindow = FindControls<CustomTimeWindowControl>(panel).Single();
        FindControls<DateTimePicker>(speedWindow).Single(p => p.Name == "WindowStart").Value = start.AddSeconds(-1).LocalDateTime;
        FindControls<DateTimePicker>(speedWindow).Single(p => p.Name == "WindowEnd").Value = end.AddSeconds(1).LocalDateTime;
        speedWindow.ApplyWindow();
        Equal(speedWindow.Start, requested!.Value, "更早时间必须通知协调器补读");
        Equal(true, FindControls<MetricCard>(panel).Single(card => card.Tone == MetricCardTone.Purple).AccessibleName!.Contains(": 3."), "窗口指标卡显示3次响应");
        Equal(3, FindControls<DataGridView>(panel).Single().Rows.Cast<DataGridViewRow>().Sum(row => Convert.ToInt32(row.Cells[4].Value)), "明细与指标卡一致");
    }

    /// <summary>扩大历史窗口后必须重读旧日志，恢复先前被默认保留时间过滤的响应且不重复近期摘要。</summary>
    private static void TestSpeedWindowBackfill()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var now = DateTimeOffset.Now;
            var old = now.AddDays(-8);
            var path = Path.Combine(directory, "rollout-speed-backfill.jsonl");
            File.WriteAllLines(path, [TurnContextLine(old, "gpt-6-astra", "priority"), ResponseItemLine(old.AddSeconds(10)),
                TokenCountLine(old.AddSeconds(11), 1000, 0, 0, 100, 0)]);
            var recent = SpeedMonitoring.Scan(new SpeedState(), [directory], now, 24);
            Equal(0, recent.Samples.Count, "默认24h不含八天前响应");
            var expanded = SpeedMonitoring.Scan(recent, [directory], now, 240);
            Equal(1, expanded.Samples.Count, "扩窗后从日志恢复旧响应");
            Equal(1, SpeedMonitoring.Scan(expanded, [directory], now, 240).Samples.Count, "扩窗后增量读取不重复");
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>复现跨重置旧文件持续追加但修改时间不前进；同一读取器须发现新增完整记录，截短后不得复用旧索引。</summary>
    private static void TestHistoricalScanStaleFileTimeAcrossReset()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "rollout-stale.jsonl");
        var start = new DateTimeOffset(2026, 9, 8, 1, 26, 12, TimeSpan.Zero);
        var stale = start.AddHours(-2).UtcDateTime;
        File.WriteAllLines(path, [TurnContextLine(start.AddHours(-1), "gpt-6-astra", "priority")]);
        File.SetLastWriteTimeUtc(path, stale);
        var reader = new RolloutLogReader();
        Equal(0, reader.ReadHistoricalFacts(directory, start, start.AddHours(1), 10080).Responses.Count,
            "只有旧上下文时没有新周响应");
        File.AppendAllLines(path, [ResponseItemLine(start.AddMinutes(1)),
            TokenCountLine(start.AddMinutes(2), 1000, 500, 0, 100, 0)]);
        File.SetLastWriteTimeUtc(path, stale);
        var first = reader.ReadHistoricalFacts(directory, start, start.AddHours(1), 10080);
        Equal(1, first.Responses.Count, "mtime 早于重置但新周追加响应必须计入");
        Equal(2.5m, first.Responses[0].CreditMultiplier, "跨重置上下文的 Fast 层级必须保留");
        Equal(1, reader.ReadHistoricalFacts(directory, start, start.AddHours(1), 10080).Responses.Count,
            "重复查询不得重复响应");
        File.AppendAllText(path, ResponseItemLine(start.AddMinutes(3)) + "\n" +
            TokenCountLine(start.AddMinutes(4), 2000, 1000, 0, 200, 0));
        File.SetLastWriteTimeUtc(path, stale);
        Equal(1, reader.ReadHistoricalFacts(directory, start, start.AddHours(1), 10080).Responses.Count,
            "尾部半行不得提前计量");
        File.AppendAllText(path, "\n");
        File.SetLastWriteTimeUtc(path, stale);
        Equal(2, reader.ReadHistoricalFacts(directory, start, start.AddHours(1), 10080).Responses.Count,
            "补齐换行后必须增量发现第二次响应");
        File.WriteAllLines(path, [TurnContextLine(start.AddHours(-1), "gpt-6-astra", "priority")]);
        File.SetLastWriteTimeUtc(path, stale);
        Equal(0, reader.ReadHistoricalFacts(directory, start, start.AddHours(1), 10080).Responses.Count,
            "文件截短后不能复用旧响应时间索引");
        File.AppendAllText(path, "[]\n");
        File.SetLastWriteTimeUtc(path, stale);
        Equal(1, reader.ReadHistoricalFacts(directory, start, start.AddHours(1), 10080).MalformedLineCount,
            "无法分类的 JSON 根结构交给正常扫描报告，时间索引不得吞掉坏行");
    }

    /// <summary>复现新重放只含部分响应仍覆盖完整实时点；验证当前周和归档都拒绝退化且允许同等用量降价。</summary>
    private static void TestReplayRejectsReducedCoverage()
    {
        var start = new DateTimeOffset(2026, 9, 8, 1, 26, 12, TimeSpan.Zero);
        var complete = CurrentSample(start.AddMinutes(3), 1500m) with
        { UsedPercent = 1, ModelResponseCount = 4, Usage = new(4000, 2000, 0, 400, 0) };
        var partial = complete with { IntervalApiEquivalentUsd = 3m, EstimatedWeeklyQuotaUsd = 300m,
            ModelResponseCount = 1, Usage = new(1000, 500, 0, 100, 0), SampleSource = "historical-replay" };
        var result = new HistoricalReplayResult(start, start.AddHours(1), "codex", [partial],
            2, 2, 0, 1, 0, 0, 0, [], [], 0);
        var state = new MonitorState { Samples = [complete] };
        HistoricalReplayCalculator.ApplyToState(state, result);
        HistoricalReplayCalculator.ApplyToState(state, result);
        Equal(complete, state.Samples.Single(), "部分响应不得覆盖完整点，也不能追加重复点");
        Equal(1, state.HistoricalReplayCoverageRejectedIntervals, "拒绝覆盖必须持久化可见诊断");
        var archive = new HistoricalArchiveReplayResult(start, start.AddDays(8), "codex", [result], 1, 0);
        HistoricalArchiveReplayCalculator.ApplyToState(state, archive, 90, ["sessions"]);
        Equal(complete, state.Samples.Single(), "归档重放也必须保留完整响应");
        Equal(1, state.HistoricalArchiveReplayCoverageRejectedIntervals, "归档覆盖退化必须可见");
        var cheaper = complete with { IntervalApiEquivalentUsd = 3m, EstimatedWeeklyQuotaUsd = 300m };
        HistoricalReplayCalculator.ApplyToState(state, result with { Samples = [cheaper] });
        Equal(cheaper, state.Samples.Single(), "用量完整时金额下降本身不得阻止合法重算");
        Equal(0, state.HistoricalReplayCoverageRejectedIntervals, "覆盖恢复后清除本周拒绝提示");
        HistoricalReplayCalculator.ApplyToState(state, result with
        { Samples = [partial with { Timestamp = complete.Timestamp.AddSeconds(-30) }] });
        Equal(1, state.Samples.Single().ModelResponseCount, "更早首次观察边界允许排除迟到响应");
        var unit = new ResponsePricingUsage("gpt-6-astra", "priority", new(1000, 500, 0, 100, 0));
        var detailed = complete with { PricingUsages = [unit, unit, unit, unit] };
        var swapped = detailed with { PricingUsages = [unit, unit,
            unit with { Usage = new(500, 250, 0, 50, 0) },
            unit with { Usage = new(1500, 750, 0, 150, 0) }] };
        state.Samples = [detailed];
        HistoricalReplayCalculator.ApplyToState(state, result with { Samples = [swapped] });
        Equal(detailed, state.Samples.Single(), "总 token 相等但逐响应缺失也不得冒充完整覆盖");
    }

    /// <summary>验证一个完整百分比区间拆成多个点时整体检查覆盖，不能用部分分段误删剩余区间。</summary>
    private static void TestReplayCoverageAcrossSplitIntervals()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var old = CurrentSample(start.AddMinutes(4), 1500m) with
        { UsedPercent = 2, DeltaPercent = 2, ModelResponseCount = 4, Usage = new(4000, 2000, 0, 400, 0) };
        var first = old with { Timestamp = start.AddMinutes(2), UsedPercent = 1, DeltaPercent = 1,
            ModelResponseCount = 2, Usage = new(2000, 1000, 0, 200, 0) };
        var second = first with { Timestamp = old.Timestamp, UsedPercent = 2 };
        var result = new HistoricalReplayResult(start, start.AddHours(1), "codex", [first],
            2, 2, 0, 2, 0, 0, 0, [], [], 0);
        var state = new MonitorState { Samples = [old] };
        HistoricalReplayCalculator.ApplyToState(state, result);
        Equal(old, state.Samples.Single(), "不完整拆分必须保留整个旧区间");
        HistoricalReplayCalculator.ApplyToState(state, result with { Samples = [first, second] });
        Equal(2, state.Samples.Count, "完整拆分可替换旧区间");
        Equal(4, state.Samples.Sum(sample => sample.ModelResponseCount), "拆分前后响应覆盖不减少");
        HistoricalReplayCalculator.ApplyToState(state, result with { Samples = [old] });
        Equal(old, state.Samples.Single(), "完整合并也可替换两个分段");
    }

    /// <summary>同步记录测试扫描进度，避免消息循环调度影响取消与完成断言。</summary>
    private sealed class ScanProgress : IProgress<(int Completed, int Total)>
    {
        public List<(int Completed, int Total)> Values { get; } = [];
        /// <summary>收集扫描器的业务进度通知。</summary>
        public void Report((int Completed, int Total) value) => Values.Add(value);
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
    /// 使用包含主 Codex、保留模型和 Spark 的真实多桶形态，验证自动选择不会在等长窗口下误选其他额度桶。
    /// </summary>
    /// <remarks>输入由方法内构造；无返回值，默认选择或显式覆盖不符合预期时由断言报告失败。</remarks>
    private static void TestAutomaticWeeklyWindowPrefersCodexBucket()
    {
        using var document = JsonDocument.Parse("""
        {
          "rateLimitsByLimitId": {
            "base_model_inference": {
              "limitId": "base_model_inference",
              "limitName": "gpt-reserve",
              "primary": { "usedPercent": 0, "windowDurationMins": 10080, "resetsAt": 1789001514 }
            },
            "codex": {
              "limitId": "codex",
              "primary": { "usedPercent": 69, "windowDurationMins": 10080, "resetsAt": 1788748179 }
            },
            "codex_bengalfox": {
              "limitId": "codex_bengalfox",
              "limitName": "GPT-5.3-Codex-Spark",
              "primary": { "usedPercent": 0, "windowDurationMins": 300, "resetsAt": 1788414714 },
              "secondary": { "usedPercent": 0, "windowDurationMins": 10080, "resetsAt": 1789001514 }
            }
          },
          "rateLimits": {
            "limitId": "codex",
            "primary": { "usedPercent": 69, "windowDurationMins": 10080, "resetsAt": 1788748179 }
          }
        }
        """);

        var automatic = RateLimitParser.Parse(document.RootElement, string.Empty, 1440, DateTimeOffset.Now);
        Equal("codex", automatic!.LimitId, "空配置时应优先选择 Codex 主额度桶");
        Near(69m, automatic.UsedPercent, 0.000001m, "自动选择应返回 Codex UI 对应的周额度百分比");
        Equal(10080, automatic.WindowDurationMinutes, "自动选择仍应使用周窗口");

        var explicitReserve = RateLimitParser.Parse(
            document.RootElement,
            "base_model_inference",
            1440,
            DateTimeOffset.Now);
        Equal("base_model_inference", explicitReserve!.LimitId, "显式额度桶设置应继续覆盖默认优先级");
        Near(0m, explicitReserve.UsedPercent, 0.000001m, "显式额度桶应返回其自身百分比");
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
    /// 验证三种算法会返回各自真正用于当前估值的样本范围，高斯模式同时保留递增的相对权重。
    /// </summary>
    private static void TestRegressionContributionScopes()
    {
        var start = DateTimeOffset.Now.AddHours(-5);
        var samples = Enumerable.Range(0, 6)
            .Select(index => CurrentSample(start.AddHours(index), 100m + index * 10m))
            .ToArray();

        var linear = RegressionCalculator.Analyze(
            samples,
            new RegressionOptions
            {
                Mode = RegressionMode.Linear,
                LinearLookbackPoints = 3
            });
        Equal(3, linear.CurrentContributions.Count, "线性当前估值只应使用最后 N 个样本");
        Equal(samples[3].Timestamp, linear.CurrentContributions[0].Timestamp, "线性贡献范围起点");
        Equal(true, linear.CurrentContributions.All(point => point.RelativeWeight == 1), "线性贡献样本应统一高亮");

        var segmented = RegressionCalculator.Analyze(
            samples,
            new RegressionOptions
            {
                Mode = RegressionMode.TimeWindowSegmented,
                SegmentWindowHours = 2.1
            });
        Equal(3, segmented.CurrentContributions.Count, "分段当前估值只应使用最后时间窗口内样本");
        Equal(samples[3].Timestamp, segmented.CurrentContributions[0].Timestamp, "分段贡献范围起点");

        var gaussian = RegressionCalculator.Analyze(
            samples,
            new RegressionOptions
            {
                Mode = RegressionMode.GaussianAggregation,
                GaussianBandwidthHours = 2
            });
        Equal(samples.Length, gaussian.CurrentContributions.Count, "高斯当前估值应返回全部有效样本");
        Near(1m, (decimal)gaussian.CurrentContributions[^1].RelativeWeight, 0.000001m, "最新高斯样本权重");
        Equal(
            true,
            gaussian.CurrentContributions
                .Zip(gaussian.CurrentContributions.Skip(1), (left, right) => left.RelativeWeight < right.RelativeWeight)
                .All(increases => increases),
            "越接近当前时刻的高斯样本权重应越大");
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
    /// 验证会话创建前已生效的 default 配置能恢复断网日志的 Standard 层级并形成可见样本。
    /// </summary>
    private static void TestConfiguredDefaultTierRecoversMissingHistoricalEvidence()
    {
        var sessionAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var fixture = CreateConfiguredTierFixture("default", sessionAt.AddMinutes(-1), sessionAt, "gpt-5.6-sol");
        var windowStart = sessionAt.AddMinutes(-1);
        var windowEnd = sessionAt.AddMinutes(1);
        var facts = new RolloutLogReader().ReadHistoricalFacts(fixture.SessionRoot, windowStart, windowEnd, 10080);
        var resetAt = windowStart.AddMinutes(10080);
        var snapshot = new RateLimitSnapshot(windowEnd, "codex", "Codex", 1m, 10080, resetAt);
        var checkpoints = new[]
        {
            new AuthoritativeRateLimitCheckpoint(sessionAt.AddSeconds(-1), "codex", 0m, 10080, resetAt),
            new AuthoritativeRateLimitCheckpoint(sessionAt.AddSeconds(3), "codex", 1m, 10080, resetAt)
        };
        var replay = HistoricalReplayCalculator.Build(snapshot, facts, checkpoints);

        Equal(1, facts.Responses.Count, "配置回填场景应提取一条响应");
        Equal(true, facts.Responses[0].PricingSucceeded, "会话创建前的 default 配置应恢复 Standard 定价");
        Equal(true, facts.Responses[0].ServiceTierRecoveredFromConfig, "响应应记录 config 回填来源");
        Equal(1, replay.Samples.Count, "恢复层级后 0% 到 1% 区间应形成样本");
        Equal("standard(config)", replay.Samples[0].ServiceTiers, "样本必须显式标记配置回填来源");
        Equal(0, replay.UnattributedIntervalCount, "已恢复区间不应继续显示为待归因点");
        Directory.Delete(fixture.Root, true);
    }

    /// <summary>
    /// 验证会话创建前已生效的 fast 配置恢复为 priority，并应用 Sol 的 2.5 倍额度倍率。
    /// </summary>
    private static void TestConfiguredFastTierRecoversWithMultiplier()
    {
        var sessionAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var fixture = CreateConfiguredTierFixture("fast", sessionAt.AddMinutes(-1), sessionAt, "gpt-5.6-sol");
        var facts = new RolloutLogReader().ReadHistoricalFacts(
            fixture.SessionRoot,
            sessionAt.AddMinutes(-1),
            sessionAt.AddMinutes(1),
            10080);

        Equal(1, facts.Responses.Count, "Fast 配置回填场景应提取一条响应");
        Equal(true, facts.Responses[0].PricingSucceeded, "fast 配置应恢复可定价响应");
        Equal("fast", facts.Responses[0].NormalizedServiceTier, "fast 配置应规范化为 Fast 层级");
        Near(2.5m, facts.Responses[0].CreditMultiplier, 0.000001m, "Sol Fast 配置回填倍率");
        Equal(true, facts.Responses[0].ServiceTierRecoveredFromConfig, "Fast 响应应记录 config 回填来源");
        Directory.Delete(fixture.Root, true);
    }

    /// <summary>
    /// 验证会话创建后才修改的配置不能反向改写旧响应层级，避免跨时间猜测。
    /// </summary>
    private static void TestNewerConfigDoesNotRewriteOlderSessionTier()
    {
        var sessionAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var fixture = CreateConfiguredTierFixture("default", sessionAt.AddMinutes(1), sessionAt, "gpt-5.6-sol");
        var facts = new RolloutLogReader().ReadHistoricalFacts(
            fixture.SessionRoot,
            sessionAt.AddMinutes(-1),
            sessionAt.AddMinutes(2),
            10080);

        Equal(1, facts.Responses.Count, "配置晚于会话时仍应保留响应事实");
        Equal(false, facts.Responses[0].PricingSucceeded, "较新的配置不得反向回填旧会话");
        Equal("unknown", facts.Responses[0].ServiceTier, "无法建立时间证据时必须继续失败关闭");
        Equal(false, facts.Responses[0].ServiceTierRecoveredFromConfig, "失败关闭响应不得标记为配置恢复");
        Directory.Delete(fixture.Root, true);
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
    /// 验证活动与归档目录可自动发现并在一次扫描中合并响应和额度点。
    /// </summary>
    private static void TestHistoricalFactsMergeActiveAndArchivedRoots()
    {
        var root = CreateTemporaryDirectory();
        var sessions = Path.Combine(root, "sessions");
        var archived = Path.Combine(root, "archived_sessions");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archived);
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-10);
        var resetAt = timestamp.AddDays(7);
        File.WriteAllLines(Path.Combine(sessions, "rollout-active.jsonl"),
        [
            TurnContextLine(timestamp, "gpt-5.6-sol", "default"),
            ResponseItemLine(timestamp.AddSeconds(1)),
            TokenCountWithRateLimitsLine(timestamp.AddSeconds(2), 100_000, 80_000, 0, 5_000, 2_000, 1m, 10080, resetAt)
        ]);
        File.WriteAllLines(Path.Combine(archived, "rollout-archived.jsonl"),
        [
            TurnContextLine(timestamp.AddMinutes(1), "gpt-5.6-sol", "priority"),
            ResponseItemLine(timestamp.AddMinutes(1).AddSeconds(1)),
            TokenCountWithRateLimitsLine(
                timestamp.AddMinutes(1).AddSeconds(2),
                200_000,
                150_000,
                0,
                8_000,
                3_000,
                2m,
                10080,
                resetAt)
        ]);

        var roots = RolloutLogReader.DiscoverHistoricalSessionRoots(sessions);
        var facts = new RolloutLogReader().ReadHistoricalFacts(
            roots,
            timestamp.AddMinutes(-1),
            timestamp.AddMinutes(3),
            10080);

        Equal(2, roots.Count, "应自动发现 sessions 与 archived_sessions 两个根目录");
        Equal(2, facts.FilesScanned, "活动与归档各一个 rollout 应合并扫描");
        Equal(2, facts.Responses.Count, "两个目录中的模型响应都应进入历史事实");
        Equal(2, facts.RateLimitCheckpoints.Count, "两个目录中的百分比首次观察点都应保留");
        Equal(true, facts.Responses.All(response => response.PricingSucceeded), "活动与归档响应都应按显式层级定价");
        Directory.Delete(root, true);
    }

    /// <summary>
    /// 验证宽时间事实可重建多个旧周，并排除重置后仍重复出现的陈旧额度快照。
    /// </summary>
    private static void TestHistoricalArchiveReplayBuildsMultipleWindows()
    {
        var historyStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var firstReset = historyStart.AddDays(7);
        var secondStart = historyStart.AddDays(8);
        var secondReset = secondStart.AddDays(7);
        var currentWindowStart = historyStart.AddDays(18);
        var responses = new[]
        {
            HistoricalPricedResponse(historyStart.AddHours(2), 1m),
            HistoricalPricedResponse(secondStart.AddHours(2), 2m)
        };
        var checkpoints = new[]
        {
            new HistoricalRateLimitCheckpoint(historyStart.AddHours(1), 0m, 10080, firstReset),
            new HistoricalRateLimitCheckpoint(historyStart.AddHours(3), 1m, 10080, firstReset.AddSeconds(20)),
            new HistoricalRateLimitCheckpoint(firstReset.AddDays(1), 2m, 10080, firstReset),
            new HistoricalRateLimitCheckpoint(secondStart.AddHours(1), 0m, 10080, secondReset),
            new HistoricalRateLimitCheckpoint(secondStart.AddHours(3), 1m, 10080, secondReset.AddSeconds(-20))
        };
        var facts = new HistoricalRolloutFacts(
            historyStart,
            currentWindowStart,
            responses,
            checkpoints,
            2,
            0);

        var replay = HistoricalArchiveReplayCalculator.Build("codex", 10080, facts, currentWindowStart);

        Equal(2, replay.Windows.Count, "两个稳定重置簇应重建为两个旧周窗口");
        Equal(2, replay.Windows.Sum(window => window.Samples.Count), "每个旧周的 0% 到 1% 区间都应形成样本");
        Near(100m, replay.Windows[0].Samples[0].EstimatedWeeklyQuotaUsd, 0.000001m, "第一旧周反推额度");
        Near(200m, replay.Windows[1].Samples[0].EstimatedWeeklyQuotaUsd, 0.000001m, "第二旧周反推额度");
        Equal(
            true,
            replay.Windows.SelectMany(window => window.Samples).All(sample => sample.SampleSource == "historical-replay"),
            "旧周样本必须保留历史重放来源标记");
    }

    /// <summary>
    /// 验证旧周回填只替换成功覆盖的时间段，保留没有可靠旧窗口证据的实时样本。
    /// </summary>
    private static void TestHistoricalArchiveReplayApplyPreservesUncoveredSamples()
    {
        var historyStart = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var resetAt = historyStart.AddDays(7);
        var currentWindowStart = historyStart.AddDays(10);
        var facts = new HistoricalRolloutFacts(
            historyStart,
            currentWindowStart,
            [HistoricalPricedResponse(historyStart.AddHours(2), 3m), HistoricalPricedResponse(historyStart.AddHours(5), 2m)],
            [
                new HistoricalRateLimitCheckpoint(historyStart.AddHours(1), 0m, 10080, resetAt),
                new HistoricalRateLimitCheckpoint(historyStart.AddHours(3), 1m, 10080, resetAt),
                new HistoricalRateLimitCheckpoint(historyStart.AddHours(4), 2m, 10080, resetAt),
                new HistoricalRateLimitCheckpoint(historyStart.AddHours(6), 3m, 10080, resetAt)
            ],
            1,
            0);
        var replay = HistoricalArchiveReplayCalculator.Build("codex", 10080, facts, currentWindowStart);
        var uncovered = CurrentSample(historyStart.AddMinutes(30), 900m);
        var covered = CurrentSample(historyStart.AddHours(3), 800m) with { UsedPercent = 1m };
        var gap = CurrentSample(historyStart.AddHours(4), 850m) with { UsedPercent = 2m };
        var state = new MonitorState { Samples = [uncovered, covered, gap] };
        var roots = new[] { @"C:\fixture\.codex\sessions", @"C:\fixture\.codex\archived_sessions" };

        HistoricalArchiveReplayCalculator.ApplyToState(state, replay, 90, roots);
        HistoricalArchiveReplayCalculator.ApplyToState(state, replay, 90, roots);

        Equal(4, state.Samples.Count, "重复重放应保留窗口外和窗口内部未覆盖样本，并更新两个成功区间");
        Equal(true, state.Samples.Contains(gap), "位于成功重放时间范围内部的未覆盖百分比样本不得被删除");
        Equal(true, state.Samples.Contains(uncovered), "首个可信百分比之前的实时样本不得被删除");
        Equal(false, state.Samples.Contains(covered), "成功重建时间段内的旧实时样本应被替换");
        Equal(1, state.HistoricalArchiveReplayWindowCount, "应持久化一个已重建旧窗口");
        Equal(2, state.HistoricalArchiveReplaySampleCount, "应持久化两个成功重建样本");
        Equal(
            false,
            HistoricalArchiveReplayCalculator.IsReplayRequired(state, 90, roots),
            "相同算法、价格、保留期和目录布局不应重复全盘扫描");
        Equal(
            true,
            HistoricalArchiveReplayCalculator.IsReplayRequired(state, 120, roots),
            "扩大历史保留期后应触发旧窗口重建");
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
    /// 验证当前周重放可重复合入：替换成功覆盖区间的实时点和旧重放点，并保留未覆盖实时样本和旧价格归档样本。
    /// </summary>
    private static void TestHistoricalReplayApplyIsIdempotent()
    {
        var timestamp = DateTimeOffset.Now;
        var coveredLive = CurrentSample(timestamp, 900m) with
        {
            UsedPercent = 18m,
            DeltaPercent = 1m
        };
        var uncoveredLive = CurrentSample(timestamp.AddMinutes(30), 1200m) with
        {
            UsedPercent = 25m,
            DeltaPercent = 1m
        };
        var coveredReplay = CurrentSample(timestamp.AddMinutes(-30), 700m) with
        {
            UsedPercent = 18m,
            DeltaPercent = 1m,
            SampleSource = HistoricalReplayCalculator.HistoricalSampleSource
        };
        var archived = LegacySample(timestamp.AddMinutes(-1), 700m);
        var replayed = CurrentSample(timestamp, 42m) with
        {
            UsedPercent = 18m,
            DeltaPercent = 1m,
            SampleSource = HistoricalReplayCalculator.HistoricalSampleSource
        };
        var state = new MonitorState { Samples = [archived, coveredReplay, coveredLive, uncoveredLive] };
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
        Equal(3, state.Samples.Count, "重复合入后应保留归档点、重建点和未覆盖实时点");
        Equal(2, state.Samples.Count(PublicApiPricing.IsCurrentSample), "当前价格版本应保留一个重建点和一个未覆盖实时点");
        Equal(true, state.Samples.Contains(uncoveredLive), "历史无法重建的 25% 实时样本不得被删除");
        Equal(false, state.Samples.Contains(coveredLive), "18% 成功重建区间应替换原实时样本");
        Equal(false, state.Samples.Contains(coveredReplay), "成功覆盖区间的旧重放点应由本轮结果刷新");
        Near(
            42m,
            state.Samples.Single(sample => sample.UsedPercent == 18m).EstimatedWeeklyQuotaUsd,
            0.000001m,
            "重建点应替换成功覆盖区间的旧当前点");
        Equal(HistoricalReplayCalculator.CurrentReplayVersion, state.HistoricalReplayVersion, "历史重放版本应持久化");
        Equal(false, HistoricalReplayCalculator.IsReplayRequired(state), "相同重放和价格版本不应再次扫描");
    }

    /// <summary>
    /// 模拟当前周历史重放从 17 个可用区间退化为 7 个区间，验证未被新结果覆盖的旧重放点不会丢失。
    /// </summary>
    /// <remarks>输入由方法内构造；无返回值，合并数量、替换结果或幂等性不符合预期时由断言报告失败。</remarks>
    private static void TestHistoricalReplayApplyPreservesUncoveredReplaySamples()
    {
        var windowStart = new DateTimeOffset(2026, 8, 31, 8, 0, 0, TimeSpan.FromHours(8));
        var oldReplaySamples = Enumerable.Range(1, 17)
            .Select(index => CurrentSample(windowStart.AddMinutes(index), 1000m + index) with
            {
                UsedPercent = index,
                DeltaPercent = 1m,
                SampleSource = HistoricalReplayCalculator.HistoricalSampleSource
            })
            .ToArray();
        var replacementSamples = Enumerable.Range(1, 7)
            .Select(index => CurrentSample(windowStart.AddMinutes(index).AddSeconds(30), 2000m + index) with
            {
                UsedPercent = index,
                DeltaPercent = 1m,
                SampleSource = HistoricalReplayCalculator.HistoricalSampleSource
            })
            .ToArray();
        var state = new MonitorState { Samples = oldReplaySamples.ToList() };
        var result = new HistoricalReplayResult(
            windowStart,
            windowStart.AddHours(1),
            "codex",
            replacementSamples,
            18,
            8,
            10,
            7,
            0,
            10,
            0,
            Enumerable.Range(8, 10).Select(value => (decimal)value).ToArray(),
            [],
            0);

        HistoricalReplayCalculator.ApplyToState(state, result);
        HistoricalReplayCalculator.ApplyToState(state, result);

        Equal(17, state.Samples.Count, "质量退化且重复合入后仍应保留 17 个历史样本");
        Equal(17, state.Samples.Count(sample =>
            string.Equals(
                sample.SampleSource,
                HistoricalReplayCalculator.HistoricalSampleSource,
                StringComparison.Ordinal)), "全部样本仍应保持历史重放来源");
        foreach (var replacement in replacementSamples)
        {
            Equal(1, state.Samples.Count(sample => sample.UsedPercent == replacement.UsedPercent),
                $"{replacement.UsedPercent}% 成功覆盖区间只应保留一个新结果");
            Near(
                replacement.EstimatedWeeklyQuotaUsd,
                state.Samples.Single(sample => sample.UsedPercent == replacement.UsedPercent).EstimatedWeeklyQuotaUsd,
                0.000001m,
                $"{replacement.UsedPercent}% 成功覆盖区间应使用本轮重放结果");
        }

        foreach (var oldReplay in oldReplaySamples.Where(sample => sample.UsedPercent >= 8m))
        {
            Equal(true, state.Samples.Contains(oldReplay), $"{oldReplay.UsedPercent}% 未覆盖旧重放点不得被删除");
        }
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
    /// 创建带显式 config.toml 层级和无层级 rollout 的测试目录，用于验证配置证据时间边界。
    /// </summary>
    /// <param name="configuredTier">写入 config.toml 的 default 或 fast。</param>
    /// <param name="configWrittenAt">配置文件最后写入时刻。</param>
    /// <param name="sessionAt">session_meta 记录的会话创建时刻。</param>
    /// <param name="model">无层级响应使用的模型。</param>
    /// <returns>测试根目录和其中的 sessions 根目录。</returns>
    private static (string Root, string SessionRoot) CreateConfiguredTierFixture(
        string configuredTier,
        DateTimeOffset configWrittenAt,
        DateTimeOffset sessionAt,
        string model)
    {
        var root = CreateTemporaryDirectory();
        var sessionRoot = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessionRoot);
        var configPath = Path.Combine(root, "config.toml");
        File.WriteAllText(configPath, $"service_tier = \"{configuredTier}\"{Environment.NewLine}");
        File.SetLastWriteTimeUtc(configPath, configWrittenAt.UtcDateTime);
        File.WriteAllLines(Path.Combine(sessionRoot, "rollout-config-tier.jsonl"),
        [
            SessionMetaLine(sessionAt),
            TurnContextWithoutTierLine(sessionAt.AddSeconds(1), model),
            ResponseItemLine(sessionAt.AddSeconds(2)),
            TokenCountLine(sessionAt.AddSeconds(3), 100_000, 80_000, 0, 5_000, 2_000)
        ]);
        return (root, sessionRoot);
    }

    /// <summary>
    /// 创建包含会话创建时间的最小 session_meta JSONL 行。
    /// </summary>
    /// <param name="timestamp">会话创建时间。</param>
    /// <returns>序列化后的单行 JSON。</returns>
    private static string SessionMetaLine(DateTimeOffset timestamp) =>
        JsonSerializer.Serialize(new { timestamp, type = "session_meta", payload = new { } });

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
