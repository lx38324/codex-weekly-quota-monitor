using System.Reflection;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor.BusinessTests;

/// <summary>从用户任务验证指标可比性、空状态恢复、范围复位和设置草稿；截图使用真实DPI的隔离桌面。</summary>
internal static class UsabilityTests
{
    /// <summary>在中英文、深浅主题完成同一组可操作场景；可选保存界面证据，不触碰正式账号数据。</summary>
    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        try
        {
            foreach (var language in new[] { UiLanguage.ChineseSimplified, UiLanguage.English })
            foreach (var theme in new[] { UiTheme.Light, UiTheme.Dark })
            {
                UiText.SetLanguage(language); AppTheme.Set(theme);
                var now = DateTimeOffset.Now.AddMinutes(-1);
                var settings = new AppSettings { Language = language, Theme = theme };
                var quota = Enumerable.Range(1, 6).Select(i => new QuotaSample(now.AddMinutes(i - 6), "codex", i, 1,
                    12, 1200 + i * 8, TokenUsage.Zero, 1, "gpt-6.1-sol") { PricingVersion = PublicApiPricing.PricingVersion,
                    AmountDefinition = PublicApiPricing.AmountDefinition,
                    OfficialLongContextAmountDefinition = PublicApiPricing.OfficialLongContextAmountDefinition }).ToArray();
                var rate = new RateLimitSnapshot(now, "codex", null, 6, 10080, now.AddDays(3));
                var view = new MonitorViewSnapshot(now, "合成验收，不代表账号用量", rate, 1230, 6, 0, 0, quota, [])
                {
                    AppServerConnected = true, SpeedHistoryHours = 24, SpeedSampleCount = 3,
                    SpeedSamples = [new("a", now.AddSeconds(-10), now, "gpt-6-astra", "standard", 200, 50)
                        { StreamingStartedAt = now.AddSeconds(-5), StreamingStatus = "available" },
                        new("b", now.AddSeconds(-10), now, "gpt-6-astra", "standard", 1500, 0),
                        new("c", now.AddSeconds(-10), now, "gpt-6.1-sol", "fast", 100, 0)]
                };
                using var form = new ChartForm(settings, _ => { });
                form.UpdateView(view, settings.Regression); form.ShowDashboardTab(); Application.DoEvents();
                Capture(form, directory, $"overview-{language}-{theme}");
                Check(Find<Label>(form).Any(l => l.Text.Contains(UiText.Format("UxEstimateReady", now.LocalDateTime, 6))),
                    "首页没有说明数据已追平及估算截至时间：" + string.Join(" | ", Find<Label>(form).Where(l => l.Text.Contains("数据") || l.Text.Contains("估算")).Select(l => l.Text)));
                form.UpdateView(view with { RateLimit = rate with { UsedPercent = 7 } }, settings.Regression);
                Check(Find<Label>(form).Any(l => l.Text.Contains(UiText.Format("UxEstimateBehind", 6, 7, now.LocalDateTime))),
                    "用量领先于计算点时没有说明未完全归因。");
                form.ShowSpeedTab(); Application.DoEvents();
                var panel = Find<SpeedPanel>(form).Single();
                var metric = Named<ComboBox>(panel, "SpeedMetric");
                var before = Find<MetricCard>(panel).Select(c => c.AccessibleName).ToArray();
                Check(before.Any(text => text!.StartsWith(UiText.Get("SpeedTotalTps") + ": " + 60d.ToString("N2"))),
                    "总体速度没有纳入未通过流式验证的快速响应。");
                metric.SelectedIndex = 2;
                Check(before.SequenceEqual(Find<MetricCard>(panel).Select(c => c.AccessibleName)), "切换图表指标改变了卡片口径。");
                Check(Find<MetricCard>(panel).Any(c => c.AccessibleName!.StartsWith(UiText.Get("SpeedMatchedEffectiveTps") + ": " + 20d.ToString("N2"))),
                    "同批对照没有使用流式合格响应。");
                Check(Named<Label>(panel, "SpeedCoverageSummary").Text.Contains("1/3"), "流式样本覆盖没有紧邻指标说明。");
                Check(Named<ComboBox>(panel, "SpeedModelFilter").AccessibilityObject.Name == UiText.Get("UxModel"), "模型筛选缺少可访问名称。");
                var grid = Named<DataGridView>(panel, "SpeedGrid"); var binding = grid.DataSource;
                var chart = Find<SpeedChartControl>(panel).Single();
                var keys = typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
                keys.Invoke(chart, [new KeyEventArgs(Keys.Add)]);
                Check(ReferenceEquals(binding, grid.DataSource), "图表导航重绑了独立明细表。");
                Named<Button>(panel, "SpeedResetView").PerformClick();
                Capture(form, directory, $"speed-{language}-{theme}");
                Named<ComboBox>(panel, "SpeedTierFilter").SelectedIndex = 2;
                Check(Named<Label>(panel, "SpeedEmptyState").Visible && Named<LinkLabel>(panel, "SpeedUseTotal").Visible,
                    "有请求但无流式证据时没有恢复入口。");
                Capture(form, directory, $"speed-empty-{language}-{theme}");
                typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(Named<LinkLabel>(panel, "SpeedUseTotal"), [new LinkLabelLinkClickedEventArgs(new LinkLabel.Link())]);
                Check(metric.SelectedIndex == 0 && chart.DisplayedSeriesCount == 1, "切换总输出没有恢复可用曲线。");
                Named<Button>(panel, "SpeedClearFilters").PerformClick();
                Check(Named<ComboBox>(panel, "SpeedTierFilter").SelectedIndex == 0, "清除筛选没有恢复全部模式。");
                form.ShowSettingsTab(settings); Application.DoEvents();
                var editor = Find<SettingsPanel>(form).Single();
                var autostart = Named<CheckBox>(editor, "StartWithWindows");
                var original = autostart.Checked; autostart.Checked = !original;
                Check(editor.HasUnsavedChanges, "编辑没有标记草稿。");
                form.Hide(); form.ShowSettingsTab(settings); Application.DoEvents();
                Check(autostart.Checked != original && editor.HasUnsavedChanges, "重新进入设置丢失未保存草稿。");
                var tabs = Find<TabControl>(editor).Single();
                Check(tabs.TabPages.Count == 6, "默认设置仍混入内部采样或连接细节。");
                Capture(form, directory, $"settings-{language}-{theme}");
                Named<CheckBox>(editor, "ShowAdvancedSettings").Checked = true;
                Check(tabs.TabPages.Count == 9 && editor.HasUnsavedChanges, "高级设置不可找回或覆盖了编辑草稿。");
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(p => p.Text == UiText.Get("SettingsEstimation"));
                var mode = Named<ComboBox>(editor, "RegressionMode"); mode.SelectedIndex = 0;
                Check(Named<NumericUpDown>(editor, "RegressionLinearPoints").Visible &&
                    !Named<NumericUpDown>(editor, "RegressionGaussianHours").Visible, "估算设置仍显示不适用的算法参数。");
                mode.SelectedIndex = 2;
                Capture(form, directory, $"estimation-{language}-{theme}");
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(p => p.Text == UiText.Get("SettingsPricing"));
                var prices = Find<PricingEditorPanel>(editor).Single();
                var model = Named<ComboBox>(prices, "PricingModelSelector").SelectedItem!.ToString()!;
                var input = Find<NumericUpDown>(prices).First(); var oldInput = input.Value;
                input.Value = oldInput + 1;
                form.Hide(); form.ShowSettingsTab(settings); Application.DoEvents();
                Check(input.Value == oldInput + 1 && editor.HasUnsavedChanges &&
                    prices.GetProfiles().Single(p => p.Model == model).ShortContext.InputPerMillion == oldInput + 1,
                    "重新打开丢失了模型价格草稿。");
                Capture(form, directory, $"pricing-{language}-{theme}");
                var sidebar = Find<SidebarNavigationButton>(form).Single(c => c.AccessibleDescription == "selected");
                Check(sidebar.AccessibilityObject.State.HasFlag(AccessibleStates.Selected), "导航没有暴露当前页状态。");
                Console.WriteLine($"易用性任务通过：{language}/{theme}，实际DPI={form.DeviceDpi}，{form.ClientSize}。");
            }
        }
        finally { UiText.SetLanguage(UiLanguage.ChineseSimplified); AppTheme.Set(UiTheme.Light); }
    }

    /// <summary>仅保存当前独立桌面的业务窗口，供同一版本中英深浅主题目视复评。</summary>
    private static void Capture(Form form, string? directory, string name)
    {
        if (directory is null) return;
        Application.DoEvents(); using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(directory, name + ".png"));
    }

    /// <summary>断言用户任务结果；保留具体失败，禁止在审计中把缺证据当作通过。</summary>
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    /// <summary>按语义控件名称找到真实操作入口。</summary>
    private static T Named<T>(Control root, string name) where T : Control => Find<T>(root).Single(c => c.Name == name);

    /// <summary>枚举窗口中的业务控件，包括暂时隐藏的页。</summary>
    private static IEnumerable<T> Find<T>(Control root) where T : Control => root.Controls.Cast<Control>().SelectMany(c =>
        (c is T item ? new[] { item } : Enumerable.Empty<T>()).Concat(Find<T>(c)));
}
