using System.Text.Json;
using WeeklyQuotaMonitor;
using WeeklyQuotaMonitor.Core;

namespace WeeklyQuotaMonitor.BusinessTests;

internal static class StartupTests
{
    /// <summary>使用独立数据目录验证零字节缓存恢复、原始证据保全、重启幂等及未知版本拒绝，不访问用户注册表写入。</summary>
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quota-startup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var speed = Path.Combine(directory, "speed.json");
        var quota = Path.Combine(directory, "state.json");
        File.WriteAllText(quota, "quota-history-must-stay");
        File.WriteAllBytes(speed, new byte[4096]);
        var recovered = JsonStorage.LoadSpeedState(speed);
        var archived = Directory.GetFiles(directory, "speed.json.corrupt-*").Single();
        Check(File.ReadAllBytes(archived).SequenceEqual(new byte[4096]), "Damaged cache evidence changed");
        Check(recovered.Samples.Count == 0 && recovered.Cursors.Count == 0 && recovered.LastIssue.Contains("JsonException"), "Recovery did not request a visible rescan");
        Check(JsonStorage.LoadSpeedState(speed).LastIssue == recovered.LastIssue, "Restart lost recovery notice");
        Check(Directory.GetFiles(directory, "speed.json.corrupt-*").Length == 1, "Repeated startup archived valid cache");
        Check(File.ReadAllText(quota) == "quota-history-must-stay", "Speed recovery touched quota history");
        File.WriteAllText(speed, JsonSerializer.Serialize(new SpeedState { Version = 999 }));
        try { JsonStorage.LoadSpeedState(speed); throw new Exception("Unknown version was reset"); }
        catch (InvalidDataException) { }
        Check(JsonSerializer.Deserialize<SpeedState>(File.ReadAllText(speed))!.Version == 999, "Unknown version was overwritten");
        var started = DateTimeOffset.UtcNow.AddSeconds(-8);
        var sample = new SpeedSample("rollout-test.jsonl", started, started.AddSeconds(5), "gpt-6-astra", "fast", 123, 12);
        File.WriteAllText(speed, JsonSerializer.Serialize(new SpeedState { Samples = [sample] }));
        Check(JsonStorage.LoadSpeedState(speed).Samples.Single() == sample, "Valid speed history changed");
        var previousContext = SynchronizationContext.Current;
        try
        {
            using var panel = new SettingsPanel(new AppSettings { StartWithWindows = false });
            var checkbox = panel.Controls.Find("StartWithWindows", true).OfType<CheckBox>().Single();
            Check(!checkbox.Checked, "Disabled startup preference not shown");
            panel.LoadSettings(new AppSettings { StartWithWindows = true });
            Check(checkbox.Checked && panel.Controls.Find("RepairShortcut", true).Length == 1, "Startup controls not accessible");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    /// <summary>以明确业务失败中止测试，不将恢复流程误标成成功。</summary>
    private static void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); }

    /// <summary>以高缩放主窗口验证启动设置区域可见、可滚动，截图只含界面和安装位置。</summary>
    public static void Capture(string output)
    {
        Application.EnableVisualStyles();
        var settings = new AppSettings();
        using var form = new ChartForm(settings, _ => { });
        form.ShowSettingsTab(settings);
        form.Scale(new SizeF(1.5f, 1.5f)); form.ClientSize = new Size(1260, 820);
        Application.DoEvents(); form.Refresh(); Application.DoEvents();
        var checkbox = form.Controls.Find("StartWithWindows", true).OfType<CheckBox>().Single();
        var repair = form.Controls.Find("RepairShortcut", true).Single();
        Check(checkbox.Visible && repair.Visible && repair.Height >= repair.PreferredSize.Height, "Startup controls clipped or hidden");
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(System.Drawing.Point.Empty, form.Size));
        bitmap.Save(output);
    }
}
