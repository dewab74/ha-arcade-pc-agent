using System.Diagnostics;
using System.Runtime.InteropServices;
using CocktailAgent.Core;

namespace CocktailAgent;

/// <summary>
/// A hidden window of the agent's own, on a thread with a message loop. It is
/// what turns the monitor off (SC_MONITORPOWER handled by its own
/// DefWindowProc, instead of a broadcast to every window on the desktop, BigBox
/// included) and where the front end is put back in front after the display
/// wakes: SetForegroundWindow from a thread attached to the current foreground
/// thread's input, which Windows allows.
/// </summary>
internal sealed partial class DesktopWindow : IDisposable
{
    private const string ClassName = "CocktailAgentDesktop";
    private const int ErrorClassAlreadyExists = 1410;

    private const uint WmSysCommand = 0x0112;
    private const uint WmDestroy = 0x0002;
    private const uint WmClose = 0x0010;
    private const uint WmRefocus = 0x8000 + 1; // WM_APP + 1
    private static readonly IntPtr ScMonitorPower = new(0xF170);

    private readonly IAgentLog _log;
    private readonly Func<Process?> _findTarget;
    private readonly WndProc _wndProc; // kept alive: the window class points at it
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private IntPtr _hwnd;

    public DesktopWindow(IAgentLog log, Func<Process?> findTarget)
    {
        _log = log;
        _findTarget = findTarget;
        _wndProc = WindowProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "desktop window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)) || _hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("The agent's hidden window could not be created.");
        }
    }

    public void MonitorOff()
    {
        if (!PostMessageW(_hwnd, WmSysCommand, ScMonitorPower, new IntPtr(2)))
        {
            throw new InvalidOperationException($"PostMessage(SC_MONITORPOWER) failed: {Marshal.GetLastPInvokeError()}");
        }
    }

    public void RequestRefocus() => PostMessageW(_hwnd, WmRefocus, IntPtr.Zero, IntPtr.Zero);

    /// <summary>The process whose window has focus, for the log ("BigBox", "explorer", "none").</summary>
    public static string ForegroundProcessName()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return "none";
        }

        _ = GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return $"pid {pid}";
        }
    }

    private void Run()
    {
        var instance = GetModuleHandleW(null);
        var wc = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            Instance = instance,
            ClassName = ClassName,
        };
        // The class points at this instance's WndProc, so each instance registers
        // it and unregisters it when its window is gone: the agent recreates this
        // window whenever saved settings restart it within the same process.
        if (RegisterClassExW(ref wc) == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorClassAlreadyExists || !UnregisterClassW(ClassName, instance) || RegisterClassExW(ref wc) == 0)
            {
                _log.Fail($"RegisterClassEx failed: {(error == ErrorClassAlreadyExists ? Marshal.GetLastPInvokeError() : error)}");
                _ready.Set();
                return;
            }
        }

        // Style 0 and never shown: no taskbar entry, never takes focus.
        _hwnd = CreateWindowExW(0, ClassName, "Cocktail agent", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        _ready.Set();
        if (_hwnd == IntPtr.Zero)
        {
            _log.Fail($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
            return;
        }

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        if (!UnregisterClassW(ClassName, instance))
        {
            _log.Warn($"UnregisterClass failed: {Marshal.GetLastPInvokeError()}");
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmRefocus:
                try
                {
                    Refocus();
                }
#pragma warning disable CA1031 // Focus is best effort; never take the message loop down.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _log.Warn($"Refocus failed: {ex.Message}");
                }

                return IntPtr.Zero;
            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    private void Refocus()
    {
        using var target = _findTarget();
        if (target is null || target.MainWindowHandle == IntPtr.Zero)
        {
            return;
        }

        var foreground = GetForegroundWindow();
        _ = GetWindowThreadProcessId(foreground, out var foregroundPid);
        if (foregroundPid == (uint)target.Id)
        {
            _log.Info($"Display woke; {target.ProcessName} still has focus");
            return;
        }

        var before = ForegroundProcessName();
        var thisThread = GetCurrentThreadId();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        var attached = foregroundThread != 0 && foregroundThread != thisThread && AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            _ = BringWindowToTop(target.MainWindowHandle);
            _ = SetForegroundWindow(target.MainWindowHandle);
        }
        finally
        {
            if (attached)
            {
                _ = AttachThreadInput(thisThread, foregroundThread, false);
            }
        }

        var after = ForegroundProcessName();
        _log.Info($"Display woke with focus on {before}; gave it back to {target.ProcessName}: {(after == target.ProcessName ? "ok" : "still " + after)}");
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _ready.Dispose();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

#pragma warning disable SYSLIB1054 // Classic interop: the window class struct carries a string.
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassEx wc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string className, IntPtr instance);
#pragma warning restore SYSLIB1054

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out Msg msg, IntPtr hwnd, uint min, uint max);

    [return: MarshalAs(UnmanagedType.Bool)]
    [LibraryImport("user32.dll")]
    private static partial bool TranslateMessage(ref Msg msg);

    [LibraryImport("user32.dll")]
    private static partial IntPtr DispatchMessageW(ref Msg msg);

    [LibraryImport("user32.dll")]
    private static partial IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int exitCode);

    [return: MarshalAs(UnmanagedType.Bool)]
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [return: MarshalAs(UnmanagedType.Bool)]
    [LibraryImport("user32.dll")]
    private static partial bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [return: MarshalAs(UnmanagedType.Bool)]
    [LibraryImport("user32.dll")]
    private static partial bool SetForegroundWindow(IntPtr hwnd);

    [return: MarshalAs(UnmanagedType.Bool)]
    [LibraryImport("user32.dll")]
    private static partial bool BringWindowToTop(IntPtr hwnd);
}
