namespace ArcadePcAgent.Core;

/// <summary>Master volume (0-100) and mute of the default playback device.</summary>
public readonly record struct AudioState(int Volume, bool Muted);

/// <summary>The Windows default playback device; implemented with Core Audio on the cabinet.</summary>
public interface IAudioControl
{
    /// <summary>Raised on any volume/mute change, from any source, and when the default device changes.</summary>
    event EventHandler? Changed;

    AudioState Read();

    void SetVolume(int volume);

    void SetMuted(bool muted);
}

/// <summary>Front-end, emulator and power actions; implemented with process calls on the cabinet.</summary>
public interface ISystemControl
{
    /// <summary>Asks emulators to close (so MAME writes hiscores/nvram), then kills what is left. Returns what it did.</summary>
    Task<string> CloseEmulatorsAsync(CancellationToken cancellationToken);

    /// <summary>Closes emulators first, then BigBox and LaunchBox, the same way.</summary>
    Task<string> CloseFrontendAsync(CancellationToken cancellationToken);

    /// <summary>Closes everything like <see cref="CloseFrontendAsync"/>, then starts BigBox again and checks it is running.</summary>
    Task<string> RestartBigBoxAsync(CancellationToken cancellationToken);

    /// <summary>The console display as Windows reports it (dimmed counts as on); null until known.</summary>
    bool? DisplayOn { get; }

    /// <summary>Raised when <see cref="DisplayOn"/> changes, whatever the cause (agent, idle timeout, input).</summary>
    event EventHandler? DisplayChanged;

    /// <summary>Blanks the monitor (the PC keeps running).</summary>
    void ScreenOff();

    /// <summary>Wakes the monitor.</summary>
    void ScreenOn();

    /// <summary>Last real input: from the input watcher when one is configured (null while it is not running), else Windows' idle timer.</summary>
    DateTimeOffset? LastRealInput { get; }

    void Shutdown();

    void Reboot();
}

public interface IAgentLog
{
    void Info(string message);

    void Warn(string message);

    void Fail(string message, Exception? exception = null);
}
