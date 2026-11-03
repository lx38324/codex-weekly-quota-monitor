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
    private static void Main(string[] args)
    {
        var show = !args.Contains("--autostart", StringComparer.OrdinalIgnoreCase);
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CodexWeeklyQuotaMonitor.Show");
        using var instance = new Mutex(
            initiallyOwned: true,
            name: @"Local\CodexWeeklyQuotaMonitor.SingleInstance",
            createdNew: out var createdNew);
        if (!createdNew)
        {
            if (show) activation.Set();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var uiContext = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(uiContext);

        using var context = new TrayApplicationContext(uiContext);
        var stopping = 0;
        var registration = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) =>
            uiContext.Post(_ => { if (Volatile.Read(ref stopping) == 0) context.ShowDashboard(); }, null),
            null, Timeout.Infinite, executeOnlyOnce: false);
        context.BeginStart();
        if (show) activation.Set();
        try { Application.Run(context); }
        finally { Interlocked.Exchange(ref stopping, 1); registration.Unregister(null); }
    }
}
