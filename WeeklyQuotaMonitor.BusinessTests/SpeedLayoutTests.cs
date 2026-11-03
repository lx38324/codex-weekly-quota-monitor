using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor.BusinessTests;

/// <summary>覆盖速度页真实三卡容器的缩放、换行和重新打开，不用可访问名称代替文字是否实际可见。</summary>
internal static class SpeedLayoutTests
{
    /// <summary>在受限客户区模拟 100%–200% 控件缩放，逐项检查三种指标、中英文及缩窄后恢复。</summary>
    public static void Run(string? outputDirectory = null)
    {
        if (outputDirectory is not null) Directory.CreateDirectory(outputDirectory);
        try
        {
            foreach (var language in new[] { UiLanguage.ChineseSimplified, UiLanguage.English })
            foreach (var scale in new[] { 1F, 1.25F, 1.5F, 2F })
            {
                UiText.SetLanguage(language);
                var now = DateTimeOffset.Now.AddMinutes(-1);
                var view = new MonitorViewSnapshot(now, "布局合成验证", null, null, 0, 0, 0, [], [])
                {
                    SpeedSamples = [new("layout", now.AddSeconds(-12), now, "gpt-6-astra", "standard", 240, 60)
                        { StreamingStartedAt = now.AddSeconds(-6), StreamingStatus = "available" }],
                    SpeedSampleCount = 1, SpeedHistoryHours = 24
                };
                var settings = new AppSettings { Language = language };
                using var form = new ChartForm(settings, _ => { });
                form.UpdateView(view, settings.Regression); form.ShowSpeedTab();
                form.Scale(new SizeF(scale, scale));
                // 必须限制缩放后的物理窗口宽度，不能让 MinimumSize 把窗口自动放大后声称不再裁切。
                form.MinimumSize = Size.Empty;
                var panel = Find<SpeedPanel>(form).Single();
                var metric = Find<ComboBox>(panel).Single(c => c.Name == "SpeedMetric");
                for (var selected = 0; selected < 3; selected++)
                {
                    metric.SelectedIndex = selected;
                    foreach (var width in new[] { 1150, 1040, 1260 })
                    {
                        form.ClientSize = new Size(width, 800);
                        Application.DoEvents();
                        AssertCards(panel, $"{language}/{scale:P0}/{width}px/metric={selected}");
                    }
                }
                form.Hide(); form.UpdateView(view with { UpdatedAt = now.AddMinutes(1) }, settings.Regression);
                form.ShowSpeedTab(); Application.DoEvents();
                AssertCards(panel, $"{language}/{scale:P0}/重开");
                if (metric.SelectedIndex != 2) throw new InvalidOperationException("重开速度页丢失已选指标。");
                if (outputDirectory is not null && (language == UiLanguage.ChineseSimplified && scale is 1.25F or 1.5F ||
                                                   language == UiLanguage.English && scale == 2F))
                {
                    form.ClientSize = new Size(1150, 800); Application.DoEvents();
                    using var image = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                    image.Save(Path.Combine(outputDirectory, $"speed-{language}-{scale * 100:0}.png"));
                }
            }
        }
        finally { UiText.SetLanguage(UiLanguage.ChineseSimplified); }
        Console.WriteLine("速度页文字可见性：中英文、100/125/150/200%模拟缩放、三种指标、窄窗与重开通过。");
    }

    /// <summary>同时验证卡片在容器内、卡片足够高、每个标签足够高且在卡片内；不能只检查控件 Visible。</summary>
    private static void AssertCards(SpeedPanel panel, string context)
    {
        var cards = Find<MetricCard>(panel).ToArray();
        if (cards.Length != 3) throw new InvalidOperationException("速度页三项指标未全部显示。");
        var rectangles = new List<Rectangle>();
        foreach (var card in cards)
        {
            var parent = card.Parent!;
            if (!parent.ClientRectangle.Contains(card.Bounds) || card.Height < card.GetPreferredSize(new Size(card.Width, 0)).Height)
                throw new InvalidOperationException($"{context}: 指标卡裁切，bounds={card.Bounds}，preferred={card.GetPreferredSize(new Size(card.Width, 0))}，parent={parent.ClientSize}。");
            var rectangle = card.RectangleToScreen(card.ClientRectangle);
            if (rectangles.Any(r => r.IntersectsWith(rectangle))) throw new InvalidOperationException($"{context}: 指标卡重叠。");
            rectangles.Add(rectangle);
            foreach (var label in Find<Label>(card))
            {
                var bounds = card.RectangleToClient(label.RectangleToScreen(label.ClientRectangle));
                var required = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
                if (!card.ClientRectangle.Contains(bounds) || label.Height < required.Height)
                    throw new InvalidOperationException($"{context}: 文字未完整显示：{label.Text}，bounds={bounds}，测量={required}，card={card.ClientSize}。");
            }
        }
    }

    /// <summary>遍历业务控件检查真实布局，不依赖私有字段或表格内部实现。</summary>
    private static IEnumerable<T> Find<T>(Control root) where T : Control => root.Controls.Cast<Control>().SelectMany(c =>
        (c is T match ? new[] { match } : Enumerable.Empty<T>()).Concat(Find<T>(c)));
}
