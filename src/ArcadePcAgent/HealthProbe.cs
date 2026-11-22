using System.Runtime.InteropServices;
using ArcadePcAgent.Core;
using Microsoft.Win32;

namespace ArcadePcAgent;

/// <summary>
/// Reads machine health for the diagnostic entities. Joysticks are polled every
/// second on a background thread (winmm joyGetPosEx); the NAS
/// share is listed every 5 minutes with a timeout, so a dead share never stalls a read.
/// </summary>
internal sealed partial class HealthProbe : IHealthProbe, IDisposable
{
    private static readonly TimeSpan StuckThreshold = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NasInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NasTimeout = TimeSpan.FromSeconds(15);

    private readonly IAgentLog _log;
    private readonly string? _nasShare;
    private readonly string? _inputWatchDir;
    private readonly StuckInputTracker _stuck = new(StuckThreshold);
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _joystickThread;
    private readonly Task _nasLoop;
    private readonly DateTimeOffset _boot;
    private bool? _nas;

    public HealthProbe(IAgentLog log, string? nasShare, string? inputWatchDir)
    {
        _log = log;
        _inputWatchDir = inputWatchDir;
        _nasShare = string.IsNullOrEmpty(nasShare) ? null : nasShare;
        var boot = DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        _boot = new DateTimeOffset(boot.Year, boot.Month, boot.Day, boot.Hour, boot.Minute, boot.Second, boot.Offset);
        _joystickThread = new Thread(PollJoysticks) { IsBackground = true, Name = "joystick poll" };
        _joystickThread.Start();
        _nasLoop = _nasShare is null ? Task.CompletedTask : Task.Run(CheckNasLoopAsync);
    }

    public HealthSnapshot Read()
    {
        var (lastInput, watching) = InputSource.Read(_inputWatchDir);
        var drive = new DriveInfo("C");
        IReadOnlyList<string> stuck;
        bool? nas;
        lock (_gate)
        {
            stuck = _stuck.Stuck(DateTimeOffset.Now);
            nas = _nas;
        }

        return new HealthSnapshot
        {
            LastInput = lastInput,
            InputWatchRunning = watching,
            StuckInputs = stuck,
            LastBoot = _boot,
            DiskFreeGb = drive.AvailableFreeSpace / 1e9,
            DiskFreePercent = 100.0 * drive.AvailableFreeSpace / drive.TotalSize,
            RebootPending = RegistryKeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")
                || RegistryKeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"),
            NasReachable = nas,
        };
    }

    private static bool RegistryKeyExists(string path)
    {
        using var key = Registry.LocalMachine.OpenSubKey(path);
        return key is not null;
    }

    private void PollJoysticks()
    {
        var seen = new bool[16];
        while (!_stop.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
            for (var id = 0; id < seen.Length; id++)
            {
                var info = new JoyInfoEx { Size = (uint)Marshal.SizeOf<JoyInfoEx>(), Flags = 0xFF /* JOY_RETURNALL */ };
                var ok = JoyGetPosEx((uint)id, ref info) == 0;
                lock (_gate)
                {
                    if (ok)
                    {
                        _stuck.Update(id, info.Buttons, Zone(info.X), Zone(info.Y), now);
                    }
                    else if (seen[id])
                    {
                        _stuck.Gone(id);
                    }
                }

                seen[id] = ok;
            }

            _stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
        }
    }

    // A stick counts as pushed in the outer quarters of the 0-65535 range.
    private static int Zone(uint value) => value < 16384 ? -1 : value > 49151 ? 1 : 0;

    private async Task CheckNasLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var listing = Task.Run(() => Directory.EnumerateFileSystemEntries(_nasShare!).Take(1).ToList());
            bool reachable;
            try
            {
                reachable = await listing.WaitAsync(NasTimeout, _stop.Token).ConfigureAwait(false) is not null;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                reachable = false;
            }

            bool? previous;
            lock (_gate)
            {
                previous = _nas;
                _nas = reachable;
            }

            if (previous != reachable)
            {
                _log.Info($"NAS share {_nasShare} {(reachable ? "reachable" : "NOT reachable")}");
            }

            try
            {
                await Task.Delay(NasInterval, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _joystickThread.Join(TimeSpan.FromSeconds(3));
        _stop.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JoyInfoEx
    {
        public uint Size;
        public uint Flags;
        public uint X;
        public uint Y;
        public uint Z;
        public uint R;
        public uint U;
        public uint V;
        public uint Buttons;
        public uint ButtonNumber;
        public uint Pov;
        public uint Reserved1;
        public uint Reserved2;
    }

    [LibraryImport("winmm.dll", EntryPoint = "joyGetPosEx")]
    private static partial uint JoyGetPosEx(uint id, ref JoyInfoEx info);
}
