using System.Runtime.InteropServices;
using CocktailAgent.Core;

namespace CocktailAgent;

/// <summary>
/// Tracks GUID_CONSOLE_DISPLAY_STATE through a power-setting callback (no window
/// needed). Windows reports the current state right after registering, then every
/// change: our own screen off/on, the display idle timeout, input waking it.
/// </summary>
internal sealed class DisplayStateWatcher : IDisposable
{
    private const uint DeviceNotifyCallback = 2;
    private const int PowerSettingChange = 0x8013;
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    private readonly IAgentLog _log;
    private readonly Action _changed;
    private readonly NativeMethods.DeviceNotifyCallbackRoutine _callback;
    private readonly IntPtr _registration;
    private volatile int _state = -1;

    public DisplayStateWatcher(IAgentLog log, Action changed)
    {
        _log = log;
        _changed = changed;
        _callback = OnNotify; // kept in a field: the native side holds this delegate
        var parameters = new NativeMethods.DeviceNotifySubscribeParameters { Callback = _callback, Context = IntPtr.Zero };
        var guid = ConsoleDisplayState;
        var error = NativeMethods.PowerSettingRegisterNotification(ref guid, DeviceNotifyCallback, ref parameters, out _registration);
        if (error != 0)
        {
            log.Warn($"Display state notifications unavailable (error {error}); the screen switch will show unknown");
        }
    }

    /// <summary>On (or dimmed), off, or null before the first report.</summary>
    public bool? On => _state switch
    {
        0 => false,
        1 or 2 => true,
        _ => null,
    };

    private uint OnNotify(IntPtr context, int type, IntPtr setting)
    {
        if (type == PowerSettingChange && setting != IntPtr.Zero)
        {
            // POWERBROADCAST_SETTING: GUID (16 bytes), DWORD DataLength, then the data.
            var value = Marshal.ReadInt32(setting, 20);
            var previous = Interlocked.Exchange(ref _state, value);
            if (previous != value)
            {
                _changed();
            }
        }

        return 0;
    }

    public void Dispose()
    {
        if (_registration != IntPtr.Zero)
        {
            _ = NativeMethods.PowerSettingUnregisterNotification(_registration);
        }
    }
}
