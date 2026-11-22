namespace ArcadePcAgent.Core;

/// <summary>Cabinet health, published on the health topic and shown as diagnostic entities.</summary>
public sealed record HealthSnapshot
{
    /// <summary>Last real input (input watcher, or Windows' idle timer); null while a configured watcher is not running.</summary>
    public DateTimeOffset? LastInput { get; init; }

    public bool InputWatchRunning { get; init; }

    /// <summary>Inputs held past the stuck threshold, e.g. "joystick 2 button 11 held since 10:31".</summary>
    public IReadOnlyList<string> StuckInputs { get; init; } = [];

    public DateTimeOffset LastBoot { get; init; }

    public double DiskFreeGb { get; init; }

    public double DiskFreePercent { get; init; }

    public bool RebootPending { get; init; }

    /// <summary>Whether the NAS share answered a directory listing; null when not configured or not yet checked.</summary>
    public bool? NasReachable { get; init; }

    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(new
    {
        last_input = LastInput?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        input_watch = InputWatchRunning,
        stuck = StuckInputs.Count > 0,
        stuck_inputs = StuckInputs,
        last_boot = LastBoot.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        disk_free_gb = Math.Round(DiskFreeGb, 1),
        disk_free_pct = Math.Round(DiskFreePercent, 1),
        reboot_pending = RebootPending,
        nas = NasReachable,
    });
}

public interface IHealthProbe
{
    HealthSnapshot Read();
}

/// <summary>
/// Spots a button or stick direction that stays held: the cabinet once had an
/// encoder reporting button 11 permanently down, which silently defeated every
/// idle timer. Fed with each poll of each joystick; pure logic, no Windows.
/// </summary>
public sealed class StuckInputTracker
{
    private readonly Dictionary<string, DateTimeOffset> _heldSince = [];

    public StuckInputTracker(TimeSpan threshold) => Threshold = threshold;

    public TimeSpan Threshold { get; }

    /// <summary>One joystick's current state. <paramref name="xZone"/>/<paramref name="yZone"/> are -1, 0 or 1.</summary>
    public void Update(int joystick, uint buttons, int xZone, int yZone, DateTimeOffset now)
    {
        var held = new HashSet<string>();
        for (var b = 0; b < 32; b++)
        {
            if ((buttons & (1u << b)) != 0)
            {
                held.Add($"joystick {joystick + 1} button {b + 1}");
            }
        }

        if (xZone != 0)
        {
            held.Add($"joystick {joystick + 1} stick {(xZone < 0 ? "left" : "right")}");
        }

        if (yZone != 0)
        {
            held.Add($"joystick {joystick + 1} stick {(yZone < 0 ? "up" : "down")}");
        }

        var prefix = $"joystick {joystick + 1} ";
        foreach (var key in _heldSince.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !held.Contains(k)).ToList())
        {
            _heldSince.Remove(key);
        }

        foreach (var key in held)
        {
            _heldSince.TryAdd(key, now);
        }
    }

    /// <summary>A joystick that disappeared (unplugged) holds nothing.</summary>
    public void Gone(int joystick)
    {
        var prefix = $"joystick {joystick + 1} ";
        foreach (var key in _heldSince.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _heldSince.Remove(key);
        }
    }

    public IReadOnlyList<string> Stuck(DateTimeOffset now) =>
        _heldSince.Where(p => now - p.Value >= Threshold)
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key} held since {p.Value.ToLocalTime():yyyy-MM-dd HH:mm}")
            .ToList();
}
