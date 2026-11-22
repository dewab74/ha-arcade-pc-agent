using System.Globalization;
using System.Runtime.InteropServices;

namespace CocktailAgent;

/// <summary>
/// Where "last real input at the machine" comes from.
/// <para>
/// By default it is Windows' own idle timer (GetLastInputInfo, for the
/// agent's own desktop session). Some cabinets reset that timer by
/// themselves (encoders or drivers that report phantom input); for those,
/// settings.inputWatchDir points at an external watcher that records only
/// real changes. The contract with such a watcher is two files in that folder:
/// <c>input-watch.heartbeat</c>, rewritten at least every 3 minutes while it
/// runs, and <c>last-input.txt</c>, holding the last real input as an
/// ISO 8601 timestamp.
/// </para>
/// </summary>
internal static partial class InputSource
{
    private static readonly TimeSpan HeartbeatMaxAge = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The last real input, and whether its source is running. With a watcher
    /// configured, a missing or stale heartbeat gives (null, false).
    /// </summary>
    public static (DateTimeOffset? LastInput, bool Running) Read(string? inputWatchDir)
    {
        if (string.IsNullOrEmpty(inputWatchDir))
        {
            return (FromWindowsIdleTimer(), true);
        }

        try
        {
            var heartbeat = new FileInfo(Path.Combine(inputWatchDir, "input-watch.heartbeat"));
            if (!heartbeat.Exists || DateTime.UtcNow - heartbeat.LastWriteTimeUtc > HeartbeatMaxAge)
            {
                return (null, false);
            }

            var text = File.ReadAllText(Path.Combine(inputWatchDir, "last-input.txt")).Trim();
            return (DateTimeOffset.Parse(text, CultureInfo.InvariantCulture), true);
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            return (null, false);
        }
    }

    private static DateTimeOffset? FromWindowsIdleTimer()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return null;
        }

        // Both are milliseconds since boot in 32 bits; unsigned subtraction survives the 49.7-day wrap.
        var idleMs = unchecked((uint)Environment.TickCount - info.Time);
        return DateTimeOffset.Now - TimeSpan.FromMilliseconds(idleMs);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);
}
