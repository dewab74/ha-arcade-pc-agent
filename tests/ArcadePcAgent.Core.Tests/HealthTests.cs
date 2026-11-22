using System.Text.Json;

namespace ArcadePcAgent.Core.Tests;

public class HealthTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_button_held_past_the_threshold_is_stuck()
    {
        var tracker = new StuckInputTracker(TimeSpan.FromMinutes(10));
        tracker.Update(1, 0x400, 0, 0, T0); // button 11, the cabinet's old fault
        Assert.Empty(tracker.Stuck(T0.AddMinutes(9)));
        tracker.Update(1, 0x400, 0, 0, T0.AddMinutes(9));
        var stuck = Assert.Single(tracker.Stuck(T0.AddMinutes(10)));
        Assert.StartsWith("joystick 2 button 11 held since", stuck, StringComparison.Ordinal);
    }

    [Fact]
    public void Releasing_resets_the_clock()
    {
        var tracker = new StuckInputTracker(TimeSpan.FromMinutes(10));
        tracker.Update(0, 0x1, 0, 0, T0);
        tracker.Update(0, 0x0, 0, 0, T0.AddMinutes(5));
        tracker.Update(0, 0x1, 0, 0, T0.AddMinutes(6));
        Assert.Empty(tracker.Stuck(T0.AddMinutes(12)));
        Assert.Single(tracker.Stuck(T0.AddMinutes(16)));
    }

    [Fact]
    public void A_held_stick_direction_counts_and_an_unplugged_joystick_clears()
    {
        var tracker = new StuckInputTracker(TimeSpan.FromMinutes(10));
        tracker.Update(0, 0, -1, 0, T0);
        Assert.Equal(["joystick 1 stick left"], tracker.Stuck(T0.AddMinutes(11)).Select(s => s[..s.IndexOf(" held", StringComparison.Ordinal)]));
        tracker.Gone(0);
        Assert.Empty(tracker.Stuck(T0.AddMinutes(30)));
    }

    [Fact]
    public void Joysticks_are_tracked_separately()
    {
        var tracker = new StuckInputTracker(TimeSpan.FromMinutes(10));
        tracker.Update(0, 0x1, 0, 0, T0);
        tracker.Update(1, 0x0, 0, 0, T0.AddMinutes(1)); // joystick 2 idle must not clear joystick 1
        Assert.Single(tracker.Stuck(T0.AddMinutes(10)));
    }

    [Fact]
    public void Json_matches_the_discovery_templates()
    {
        var json = JsonElement.Parse(new HealthSnapshot
        {
            LastInput = null,
            InputWatchRunning = false,
            StuckInputs = ["joystick 2 button 11 held since 2026-11-21 10:31"],
            LastBoot = T0,
            DiskFreeGb = 301.84,
            DiskFreePercent = 63.56,
            RebootPending = true,
            NasReachable = null,
        }.ToJson());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("last_input").ValueKind);
        Assert.False(json.GetProperty("input_watch").GetBoolean());
        Assert.True(json.GetProperty("stuck").GetBoolean());
        Assert.Equal(1, json.GetProperty("stuck_inputs").GetArrayLength());
        Assert.Equal(301.8, json.GetProperty("disk_free_gb").GetDouble());
        Assert.Equal(63.6, json.GetProperty("disk_free_pct").GetDouble());
        Assert.True(json.GetProperty("reboot_pending").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("nas").ValueKind);
        Assert.Equal("2026-11-21T10:00:00.0000000+00:00", json.GetProperty("last_boot").GetString());
    }
}
