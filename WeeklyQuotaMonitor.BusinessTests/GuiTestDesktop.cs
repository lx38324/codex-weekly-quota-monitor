using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WeeklyQuotaMonitor.BusinessTests;

/// <summary>在不切换显示的独立 Windows 桌面执行真实 UI 测试；失败直接报告，禁止回退到工作桌面。</summary>
internal static class GuiTestDesktop
{
    private const string WorkerFlag = "--isolated-test-desktop";
    private const string DesktopPrefix = "QuotaMonitor.Tests.";
    private const uint DesktopAccess = 0xC7; // 读写对象、创建窗口/菜单、枚举；不申请切换桌面权限。

    /// <summary>普通入口创建私有桌面并等待工作进程；工作入口核实真实桌面后才交给原测试分派。</summary>
    public static string[]? Enter(string[] arguments)
    {
        if (arguments.Length >= 2 && arguments[0] == WorkerFlag)
        {
            var actual = CurrentDesktopName();
            if (!arguments[1].StartsWith(DesktopPrefix, StringComparison.Ordinal) || actual != arguments[1])
                throw new InvalidOperationException($"测试桌面不匹配，拒绝执行 UI 测试：{actual}");
            return arguments[2..];
        }

        var name = DesktopPrefix + Guid.NewGuid().ToString("N");
        using var desktop = CreateDesktop(name, null, IntPtr.Zero, 0, DesktopAccess, IntPtr.Zero);
        if (desktop.IsInvalid) throw NativeError("无法创建独立测试桌面");
        Environment.ExitCode = Run(name, arguments, 600000, hidden: false);
        return null;
    }

    /// <summary>隐藏启动子场景在同一测试桌面运行，并保留 SW_HIDE 语义和原始失败退出码。</summary>
    public static int RunHiddenChild(string[] arguments, uint timeoutMilliseconds) =>
        Run(CurrentDesktopName(), arguments, timeoutMilliseconds, hidden: true);

    /// <summary>验证工作进程与用户当前输入桌面分离；真实窗口可见性仍由既有业务场景检查。</summary>
    public static void VerifyIsolation()
    {
        using var input = OpenInputDesktop(0, false, 1);
        if (input.IsInvalid) throw NativeError("无法读取当前输入桌面");
        var current = CurrentDesktopName();
        var active = DesktopName(input.DangerousGetHandle());
        if (!current.StartsWith(DesktopPrefix, StringComparison.Ordinal) || current == active)
            throw new InvalidOperationException("UI 测试未与用户输入桌面隔离。");
        Console.WriteLine($"UI 测试桌面已隔离：输入桌面={active}；不切换显示。");
    }

    /// <summary>以明确桌面创建进程，继承标准输出；超时终止该测试进程，退出码和异常均不包装成成功。</summary>
    private static int Run(string desktop, string[] arguments, uint timeoutMilliseconds, bool hidden)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "WeeklyQuotaMonitor.BusinessTests.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("缺少业务测试 apphost。", executable);
        using var input = DuplicateStandardHandle(-10);
        using var output = DuplicateStandardHandle(-11);
        using var error = DuplicateStandardHandle(-12);
        var startup = new StartupInfo
        {
            Size = Marshal.SizeOf<StartupInfo>(), Desktop = desktop,
            Flags = 0x101, ShowWindow = hidden ? (short)0 : (short)5,
            StandardInput = input.DangerousGetHandle(), StandardOutput = output.DangerousGetHandle(),
            StandardError = error.DangerousGetHandle()
        };
        var command = new StringBuilder(string.Join(" ", new[] { executable, WorkerFlag, desktop }.Concat(arguments).Select(Quote)));
        if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08000000,
                IntPtr.Zero, AppContext.BaseDirectory, ref startup, out var info))
            throw NativeError("无法在独立桌面启动测试");
        using var process = new SafeWaitHandle(info.Process, true);
        using var thread = new SafeWaitHandle(info.Thread, true);
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (true)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                if (!TerminateProcess(process, 1)) throw NativeError("测试超时且无法终止测试进程");
                WaitForSingleObject(process, 5000);
                throw new TimeoutException("独立桌面业务测试超时。");
            }
            var result = WaitForSingleObject(process, (uint)Math.Min(remaining, 250));
            if (result == 0) break;
            if (result != 258) throw NativeError("等待独立桌面测试失败");
            // 仅拒绝本次子测试抢焦点；用户主动切换其他窗口不属于失败。
            GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundProcess);
            if (foregroundProcess == info.ProcessId && CurrentDesktopName() != desktop)
            {
                if (!TerminateProcess(process, 1)) throw NativeError("检测到测试抢占焦点且无法终止");
                throw new InvalidOperationException("独立桌面测试意外进入工作桌面前台。");
            }
        }
        if (!GetExitCodeProcess(process, out var exitCode)) throw NativeError("无法读取测试退出码");
        return unchecked((int)exitCode);
    }

    /// <summary>复制标准管道为可继承句柄，使原终端收到测试输出而无需打开测试控制台。</summary>
    private static SafeFileHandle DuplicateStandardHandle(int kind)
    {
        var handle = GetStdHandle(kind);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) throw NativeError("测试标准管道不可用");
        if (!DuplicateHandle(GetCurrentProcess(), handle, GetCurrentProcess(), out var copy, 0, true, 2))
            throw NativeError("无法继承测试标准管道");
        return copy;
    }

    /// <summary>按 Windows 命令行规则保留空参数、引号和末尾反斜杠，支持含空格的截图路径。</summary>
    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    /// <summary>读取当前线程桌面，必须在创建任何 WinForms 窗口之前核实隔离。</summary>
    private static string CurrentDesktopName() => DesktopName(GetThreadDesktop(GetCurrentThreadId()));

    /// <summary>只读取 Windows 桌面名称，不读取其他应用窗口内容。</summary>
    private static string DesktopName(IntPtr desktop)
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _))
            throw NativeError("无法读取测试桌面名称");
        return name.ToString();
    }

    /// <summary>把 Win32 原始错误码与操作上下文一起报告。</summary>
    private static Win32Exception NativeError(string operation)
    {
        var code = Marshal.GetLastWin32Error();
        return new(code, $"{operation}：{new Win32Exception(code).Message} ({code})");
    }

    private sealed class DesktopHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        /// <summary>由系统调用接管桌面句柄；最终释放，不改变输入桌面。</summary>
        public DesktopHandle() : base(true) { }
        /// <summary>工作进程退出后释放独立桌面，避免重复测试积累桌面对象。</summary>
        protected override bool ReleaseHandle() => CloseDesktop(handle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XCount, YCount, Fill, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    /// <summary>创建未切换显示的桌面。</summary>
    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern DesktopHandle CreateDesktop(string name, string? device, IntPtr mode, uint flags, uint access, IntPtr security);
    /// <summary>读取用户正在使用的输入桌面。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern DesktopHandle OpenInputDesktop(uint flags, bool inherit, uint access);
    /// <summary>释放独立桌面。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);
    /// <summary>查询当前线程所绑定的桌面。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetThreadDesktop(uint thread);
    /// <summary>读取桌面名称属性。</summary>
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int required);
    /// <summary>取得当前线程标识。</summary>
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    /// <summary>取得当前进程伪句柄，用于复制标准管道。</summary>
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    /// <summary>取得终端标准管道。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int kind);
    /// <summary>复制管道句柄并设置可继承属性。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess,
        out SafeFileHandle target, uint access, bool inherit, uint options);
    /// <summary>在指定桌面创建测试工作进程，标准管道沿用原终端。</summary>
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity,
        IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo info);
    /// <summary>等待测试完成或返回超时状态。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle process, uint milliseconds);
    /// <summary>取得业务测试原始退出码。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeWaitHandle process, out uint code);
    /// <summary>仅在超时或隔离失败时终止本次测试进程。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeWaitHandle process, uint code);
    /// <summary>读取当前桌面的前台窗口，仅用于检测测试自身是否抢焦点。</summary>
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    /// <summary>把前台窗口映射到进程，不读取窗口内容。</summary>
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
}
