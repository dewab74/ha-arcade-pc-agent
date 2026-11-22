using System.Runtime.InteropServices;

namespace ArcadePcAgent;

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    [LibraryImport("kernel32.dll")]
    internal static partial uint SetThreadExecutionState(uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate uint DeviceNotifyCallbackRoutine(IntPtr context, int type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceNotifySubscribeParameters
    {
        public DeviceNotifyCallbackRoutine Callback;
        public IntPtr Context;
    }

    [DllImport("powrprof.dll")]
    internal static extern uint PowerSettingRegisterNotification(ref Guid settingGuid, uint flags, ref DeviceNotifySubscribeParameters recipient, out IntPtr registration);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerSettingUnregisterNotification(IntPtr registration);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    internal static Input MouseMove(int dx, int dy) =>
        new() { Type = 0 /* INPUT_MOUSE */, Mouse = new MouseInput { Dx = dx, Dy = dy, Flags = 0x0001 /* MOUSEEVENTF_MOVE */ } };
}
