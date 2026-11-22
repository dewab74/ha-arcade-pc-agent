using System.Runtime.InteropServices;
using ArcadePcAgent.Core;

namespace ArcadePcAgent;

/// <summary>
/// Master volume and mute of the default playback device through Core Audio
/// (the same interfaces cabinet-audio.ps1 uses). Change notifications come
/// from IAudioEndpointVolumeCallback; a new default device is picked up via
/// IMMNotificationClient.
/// </summary>
internal sealed class CoreAudio : IAudioControl, IDisposable
{
    private const int ERender = 0;
    private const int EMultimedia = 1;
    private static readonly Guid AudioEndpointVolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private readonly Lock _gate = new();
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly VolumeCallback _volumeCallback;
    private readonly DeviceCallback _deviceCallback;
    private readonly IAgentLog _log;
    private IAudioEndpointVolume? _endpoint;
    private Guid _context = Guid.NewGuid();

    public CoreAudio(IAgentLog log)
    {
        _log = log;
        _volumeCallback = new VolumeCallback(this);
        _deviceCallback = new DeviceCallback(this);
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        Marshal.ThrowExceptionForHR(_enumerator.RegisterEndpointNotificationCallback(_deviceCallback));
        Bind();
    }

    public event EventHandler? Changed;

    public AudioState Read()
    {
        lock (_gate)
        {
            var endpoint = _endpoint ?? throw new InvalidOperationException("No default playback device.");
            Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out var level));
            Marshal.ThrowExceptionForHR(endpoint.GetMute(out var muted));
            return new AudioState((int)Math.Round(level * 100, MidpointRounding.AwayFromZero), muted);
        }
    }

    public void SetVolume(int volume)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(volume, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(volume, 100);
        lock (_gate)
        {
            var endpoint = _endpoint ?? throw new InvalidOperationException("No default playback device.");
            Marshal.ThrowExceptionForHR(endpoint.SetMasterVolumeLevelScalar(volume / 100f, ref _context));
        }

        var readBack = Read().Volume;
        if (readBack != volume)
        {
            throw new InvalidOperationException($"Volume read back as {readBack}, expected {volume}.");
        }
    }

    public void SetMuted(bool muted)
    {
        lock (_gate)
        {
            var endpoint = _endpoint ?? throw new InvalidOperationException("No default playback device.");
            Marshal.ThrowExceptionForHR(endpoint.SetMute(muted, ref _context));
        }

        if (Read().Muted != muted)
        {
            throw new InvalidOperationException($"Mute read back as {!muted}, expected {muted}.");
        }
    }

    private void Bind()
    {
        lock (_gate)
        {
            if (_endpoint is not null)
            {
                _ = _endpoint.UnregisterControlChangeNotify(_volumeCallback);
                Marshal.ReleaseComObject(_endpoint);
                _endpoint = null;
            }

            var hr = _enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out var device);
            if (hr != 0 || device is null)
            {
                _log.Warn($"No default playback device (0x{hr:X8}); waiting for one");
                return;
            }

            try
            {
                var iid = AudioEndpointVolumeIid;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var activated));
                _endpoint = (IAudioEndpointVolume)activated;
                Marshal.ThrowExceptionForHR(_endpoint.RegisterControlChangeNotify(_volumeCallback));
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }

        _log.Info("Bound to the default playback device");
    }

    // Core Audio forbids calling back into it from inside a notification, so rebinding runs elsewhere.
    private void OnDefaultDeviceChanged() => Task.Run(() =>
    {
        try
        {
            Bind();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Fail("Rebinding to the new default playback device failed", ex);
        }
    });

    private void OnVolumeNotify() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_endpoint is not null)
            {
                _ = _endpoint.UnregisterControlChangeNotify(_volumeCallback);
                Marshal.ReleaseComObject(_endpoint);
                _endpoint = null;
            }
        }

        _ = _enumerator.UnregisterEndpointNotificationCallback(_deviceCallback);
        Marshal.ReleaseComObject(_enumerator);
    }

    private sealed class VolumeCallback(CoreAudio owner) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr notify)
        {
            owner.OnVolumeNotify();
            return 0;
        }
    }

    private sealed class DeviceCallback(CoreAudio owner) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, int newState) => 0;

        public int OnDeviceAdded(string deviceId) => 0;

        public int OnDeviceRemoved(string deviceId) => 0;

        public int OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId)
        {
            if (flow == ERender && role == EMultimedia)
            {
                owner.OnDefaultDeviceChanged();
            }

            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public int PropertyId;
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IMMNotificationClient client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
}

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig]
    int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);

    [PreserveSig]
    int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);

    [PreserveSig]
    int GetChannelCount(out uint count);

    [PreserveSig]
    int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

    [PreserveSig]
    int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

    [PreserveSig]
    int GetMasterVolumeLevel(out float levelDb);

    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);

    [PreserveSig]
    int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);

    [PreserveSig]
    int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

    [PreserveSig]
    int GetChannelVolumeLevel(uint channel, out float levelDb);

    [PreserveSig]
    int GetChannelVolumeLevelScalar(uint channel, out float level);

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport]
[Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolumeCallback
{
    [PreserveSig]
    int OnNotify(IntPtr notify);
}

[ComImport]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    [PreserveSig]
    int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);

    [PreserveSig]
    int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);

    [PreserveSig]
    int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
}
