using System.Collections.Concurrent;

namespace ArcadePcAgent.Core.Tests;

internal sealed class FakeAudio : IAudioControl
{
    private AudioState _state = new(40, false);

    public event EventHandler? Changed;

    public AudioState Read() => _state;

    public void SetVolume(int volume) => Set(_state with { Volume = volume });

    public void SetMuted(bool muted) => Set(_state with { Muted = muted });

    /// <summary>A change made outside the agent (the cabinet's own controls, a game).</summary>
    public void Set(AudioState state)
    {
        _state = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class FakeSystem : ISystemControl
{
    public ConcurrentQueue<string> Calls { get; } = new();

    public Task<string> CloseEmulatorsAsync(CancellationToken cancellationToken)
    {
        Calls.Enqueue("close_emulators");
        return Task.FromResult("closed fake emulators");
    }

    public bool FailClose { get; set; }

    public Task<string> CloseFrontendAsync(CancellationToken cancellationToken)
    {
        Calls.Enqueue("close_frontend");
        return FailClose
            ? Task.FromException<string>(new InvalidOperationException("Access is denied."))
            : Task.FromResult("closed fake front ends");
    }

    public Task<string> RestartBigBoxAsync(CancellationToken cancellationToken)
    {
        Calls.Enqueue("restart_bigbox");
        return Task.FromResult("restarted fake BigBox");
    }

    public bool? DisplayOn { get; private set; } = true;

    public event EventHandler? DisplayChanged;

    public DateTimeOffset? LastRealInput { get; set; } = DateTimeOffset.UtcNow.AddHours(-1);

    public void ScreenOff()
    {
        Calls.Enqueue("screen_off");
        SetDisplay(false);
    }

    public void ScreenOn()
    {
        Calls.Enqueue("screen_on");
        SetDisplay(true);
    }

    /// <summary>The display changing on its own (idle timeout, input, a phantom wake).</summary>
    public void SetDisplay(bool on)
    {
        DisplayOn = on;
        DisplayChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Shutdown() => Calls.Enqueue("shutdown");

    public void Reboot() => Calls.Enqueue("reboot");
}

internal sealed class FakeHealth : IHealthProbe
{
    public HealthSnapshot Snapshot { get; set; } = new()
    {
        LastInput = new DateTimeOffset(2026, 11, 21, 9, 55, 46, TimeSpan.FromHours(-6)),
        InputWatchRunning = true,
        LastBoot = new DateTimeOffset(2026, 11, 21, 10, 47, 10, TimeSpan.FromHours(-6)),
        DiskFreeGb = 301.84,
        DiskFreePercent = 63.55,
    };

    public HealthSnapshot Read() => Snapshot;
}

internal sealed class RecordingLog : IAgentLog
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public void Info(string message) => Lines.Enqueue("INFO " + message);

    public void Warn(string message) => Lines.Enqueue("WARN " + message);

    public void Fail(string message, Exception? exception = null) => Lines.Enqueue("ERROR " + message + " " + exception);
}

internal static class Eventually
{
    public static async Task True(Func<bool> condition, string because, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting: {because}");
            }

            await Task.Delay(10);
        }
    }
}
