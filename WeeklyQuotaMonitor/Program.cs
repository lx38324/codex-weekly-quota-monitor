namespace WeeklyQuotaMonitor;

/// <summary>
/// 提供单实例 WinForms 托盘程序入口。
/// </summary>
internal static class Program
{
    /// <summary>
    /// 初始化高 DPI、单实例互斥量、WinForms 同步上下文和托盘消息循环。
    /// </summary>
    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(
            initiallyOwned: true,
            name: @"Local\CodexWeeklyQuotaMonitor.SingleInstance",
            createdNew: out var createdNew);
        if (!createdNew)
        {
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var uiContext = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(uiContext);

        using var context = new TrayApplicationContext(uiContext);
        context.BeginStart();
        Application.Run(context);
    }
}
