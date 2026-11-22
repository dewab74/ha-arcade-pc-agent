using System.Diagnostics;
using System.Runtime.InteropServices;
using ArcadePcAgent.Core;

namespace ArcadePcAgent;

/// <summary>
/// Closes emulators and the LaunchBox front ends the way the idle-shutdown
/// task does: a normal close request first (MAME writes hiscores and nvram,
/// BigBox saves its settings), a force kill only after a timeout.
/// </summary>
internal sealed partial class SystemControl : ISystemControl, IDisposable
{
    private static readonly string[] FrontEnds = ["BigBox", "LaunchBox"];
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(30);

    private readonly IAudioControl _audio;
    private readonly IAgentLog _log;
    private readonly string _emulatorRoot;
    private readonly string _bigBoxExe;
    private readonly string? _inputWatchDir;
    private readonly DisplayStateWatcher _display;
    private readonly DesktopWindow _desktop;
    private bool? _lastDisplayOn;

    public SystemControl(IAudioControl audio, IAgentLog log, string launchBoxDir, string? inputWatchDir)
    {
        _audio = audio;
        _log = log;
        _emulatorRoot = Path.Combine(launchBoxDir, "Emulators") + Path.DirectorySeparatorChar;
        _bigBoxExe = Path.Combine(launchBoxDir, "Core", "BigBox.exe");
        _inputWatchDir = inputWatchDir;
        _desktop = new DesktopWindow(log, FindFocusTarget);
        _display = new DisplayStateWatcher(log, OnDisplayChanged);
    }

    private void OnDisplayChanged()
    {
        var on = _display.On;
        _log.Info($"Display {(on == true ? "on" : on == false ? "off" : "unknown")}; focus on {DesktopWindow.ForegroundProcessName()}");
        var woke = _lastDisplayOn == false && on == true;
        _lastDisplayOn = on;
        if (woke)
        {
            // Give the wake a moment to settle, then make sure the game or BigBox has focus.
            _ = Task.Delay(TimeSpan.FromSeconds(1.5)).ContinueWith(_ => _desktop.RequestRefocus(), TaskScheduler.Default);
        }

        DisplayChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What should have focus: a running emulator, else BigBox, else LaunchBox.</summary>
    private System.Diagnostics.Process? FindFocusTarget()
    {
        foreach (var emulator in FindEmulators())
        {
            if (emulator.MainWindowHandle != IntPtr.Zero)
            {
                return emulator;
            }

            emulator.Dispose();
        }

        foreach (var name in FrontEnds)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    return process;
                }

                process.Dispose();
            }
        }

        return null;
    }

    public event EventHandler? DisplayChanged;

    public bool? DisplayOn => _display.On;

    // SC_MONITORPOWER through the agent's own hidden window. A broadcast to every
    // window (the usual recipe) also reaches BigBox, and BigBox lost focus with it.
    public void ScreenOff() => _desktop.MonitorOff();

    public void ScreenOn()
    {
        // A 1-pixel nudge there and back wakes the monitor without moving the cursor;
        // ES_DISPLAY_REQUIRED alone does not undo SC_MONITORPOWER.
        _ = NativeMethods.SetThreadExecutionState(0x00000002);
        var inputs = new[] { NativeMethods.MouseMove(1, 0), NativeMethods.MouseMove(-1, 0) };
        if (NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>()) != inputs.Length)
        {
            throw new InvalidOperationException($"SendInput failed: {Marshal.GetLastPInvokeError()}");
        }
    }

    public DateTimeOffset? LastRealInput => InputSource.Read(_inputWatchDir).LastInput;

    public async Task<string> RestartBigBoxAsync(CancellationToken cancellationToken)
    {
        var closed = await CloseFrontendAsync(cancellationToken).ConfigureAwait(false);
        if (Process.GetProcessesByName("BigBox").Length > 0)
        {
            return $"{closed}; BigBox is still running, not starting a second one";
        }

        using (Process.Start(new ProcessStartInfo(_bigBoxExe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(_bigBoxExe) }))
        {
        }

        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            var running = Process.GetProcessesByName("BigBox");
            if (running.Length > 0)
            {
                var pid = running[0].Id;
                foreach (var p in running)
                {
                    p.Dispose();
                }

                return $"{closed}; started BigBox ({pid})";
            }
        }

        throw new InvalidOperationException($"{closed}; BigBox did not start within 30 s");
    }

    public void Dispose()
    {
        _display.Dispose();
        _desktop.Dispose();
    }

    public async Task<string> CloseEmulatorsAsync(CancellationToken cancellationToken) =>
        Summarize("emulators", await CloseAsync(FindEmulators(), cancellationToken).ConfigureAwait(false));

    public async Task<string> CloseFrontendAsync(CancellationToken cancellationToken)
    {
        var emulators = await CloseAsync(FindEmulators(), cancellationToken).ConfigureAwait(false);
        var frontEnds = await CloseAsync(FrontEnds.SelectMany(Process.GetProcessesByName).ToList(), cancellationToken).ConfigureAwait(false);
        return $"{Summarize("emulators", emulators)}; {Summarize("front ends", frontEnds)}";
    }

    public void Shutdown() => RunShutdown("/s /t 5 /c \"Arcade PC agent: shutdown requested from Home Assistant\"");

    public void Reboot() => RunShutdown("/r /t 5 /c \"Arcade PC agent: restart requested from Home Assistant\"");

    private void RunShutdown(string arguments)
    {
        // The amplifier stays powered: without this the shutdown sound (and, for a
        // shutdown, the plug cut) would be heard. The logon task restores audio.
        try
        {
            _audio.SetMuted(true);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not mute before shutdown: {ex.Message}");
        }

        using var process = Process.Start(new ProcessStartInfo("shutdown.exe", arguments) { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("shutdown.exe did not start.");
        process.WaitForExit(10_000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"shutdown.exe {arguments} exited {process.ExitCode}.");
        }
    }

    private List<Process> FindEmulators()
    {
        var found = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainModule?.FileName is { } path && path.StartsWith(_emulatorRoot, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(process);
                    continue;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Another user's or an elevated process: not ours to close.
            }

            process.Dispose();
        }

        return found;
    }

    private async Task<List<string>> CloseAsync(List<Process> processes, CancellationToken cancellationToken)
    {
        var results = new List<string>();
        foreach (var process in processes)
        {
            using (process)
            {
                string name;
                try
                {
                    name = $"{process.ProcessName} ({process.Id})";
                    if (process.HasExited)
                    {
                        continue;
                    }
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(CloseTimeout);
                try
                {
                    process.CloseMainWindow();
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    results.Add($"closed {name}");
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _log.Warn($"{name} did not close in {CloseTimeout.TotalSeconds:0}s; killing it");
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                        results.Add($"killed {name}");
                    }
                    catch (System.ComponentModel.Win32Exception ex)
                    {
                        results.Add($"could not kill {name}: {ex.Message}");
                    }
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    // Typically an elevated process seen from a non-elevated agent.
                    results.Add($"could not close {name}: {ex.Message}");
                }
            }
        }

        return results;
    }

    private static string Summarize(string what, List<string> results) =>
        results.Count == 0 ? $"no {what} running" : string.Join(", ", results);
}
