using System.Text.Json;

namespace ArcadePcAgent.Core.Tests;

public class DiscoveryTests
{
    private static readonly AgentSettings S = new() { Host = "broker", Username = "u", SuggestedArea = "Game Room" };
    private static readonly Topics T = new(S.DiscoveryPrefix, S.NodeId);

    private static Dictionary<string, JsonElement> Build() =>
        Discovery.Build(S, T, "1.2.3").ToDictionary(m => m.Topic, m => JsonElement.Parse(m.Payload));

    [Fact]
    public void Mute_and_volume_default_to_node_based_entity_ids()
    {
        var all = Build();
        Assert.Equal("switch.arcade_pc_mute", all["homeassistant/switch/arcade_pc/mute/config"].GetProperty("default_entity_id").GetString());
        Assert.Equal("number.arcade_pc_volume", all["homeassistant/number/arcade_pc/volume/config"].GetProperty("default_entity_id").GetString());
    }

    [Fact]
    public void Mute_and_volume_entity_ids_can_be_pinned()
    {
        var settings = S with { MuteEntityId = "switch.game_room_muted", VolumeEntityId = "number.game_room_volume" };
        var all = Discovery.Build(settings, T, "1").ToDictionary(m => m.Topic, m => JsonElement.Parse(m.Payload));
        Assert.Equal("switch.game_room_muted", all["homeassistant/switch/arcade_pc/mute/config"].GetProperty("default_entity_id").GetString());
        Assert.Equal("number.game_room_volume", all["homeassistant/number/arcade_pc/volume/config"].GetProperty("default_entity_id").GetString());
        Assert.Equal("arcade_pc_mute", all["homeassistant/switch/arcade_pc/mute/config"].GetProperty("unique_id").GetString());
    }

    [Fact]
    public void Input_watcher_sensor_appears_only_when_a_watcher_is_configured()
    {
        Assert.False(Build().ContainsKey("homeassistant/binary_sensor/arcade_pc/input_watch/config"));
        var settings = S with { InputWatchDir = @"C:\ProgramData\InputWatch" };
        var all = Discovery.Build(settings, T, "1").ToDictionary(m => m.Topic, m => JsonElement.Parse(m.Payload));
        Assert.Equal("running", all["homeassistant/binary_sensor/arcade_pc/input_watch/config"].GetProperty("device_class").GetString());
        Assert.DoesNotContain("homeassistant/binary_sensor/arcade_pc/input_watch/config", Discovery.Removals(settings, T));
    }

    [Fact]
    public void Every_entity_is_on_one_device_with_availability_and_unique_ids()
    {
        var all = Build();
        Assert.Equal(13, all.Count);
        Assert.Equal(all.Count, all.Values.Select(v => v.GetProperty("unique_id").GetString()).Distinct().Count());
        foreach (var config in all.Values)
        {
            Assert.Equal(T.Availability, config.GetProperty("availability_topic").GetString());
            var device = config.GetProperty("device");
            Assert.Equal("arcade_pc", device.GetProperty("identifiers")[0].GetString());
            Assert.Equal("1.2.3", device.GetProperty("sw_version").GetString());
            Assert.Equal("Game Room", device.GetProperty("suggested_area").GetString());
        }
    }

    [Fact]
    public void Buttons_point_at_their_command_topics()
    {
        var all = Build();
        foreach (var button in Enum.GetValues<AgentButton>())
        {
            var name = ButtonNames.Of(button);
            var config = all[$"homeassistant/button/arcade_pc/{name}/config"];
            Assert.Equal(T.Command(button), config.GetProperty("command_topic").GetString());
            Assert.Equal("PRESS", config.GetProperty("payload_press").GetString());
            Assert.Equal($"button.arcade_pc_{name}", config.GetProperty("default_entity_id").GetString());
            Assert.Equal(new PressCommand(button), CommandParser.Parse(T, config.GetProperty("command_topic").GetString()!, "PRESS", out _));
        }
    }

    [Fact]
    public void Screen_switch_reads_the_state_and_parses_its_commands()
    {
        var screen = Build()["homeassistant/switch/arcade_pc/screen/config"];
        Assert.Equal("switch.arcade_pc_screen", screen.GetProperty("default_entity_id").GetString());
        Assert.Equal(T.State, screen.GetProperty("state_topic").GetString());
        Assert.Equal(new SetScreenCommand(false), CommandParser.Parse(T, screen.GetProperty("command_topic").GetString()!, "OFF", out _));
    }

    [Fact]
    public void Switch_and_number_round_trip_through_the_parser()
    {
        var all = Build();
        var sw = all["homeassistant/switch/arcade_pc/mute/config"];
        Assert.Equal(new SetMutedCommand(true), CommandParser.Parse(T, sw.GetProperty("command_topic").GetString()!, sw.GetProperty("payload_on").GetString()!, out _));
        var number = all["homeassistant/number/arcade_pc/volume/config"];
        Assert.Equal(T.State, number.GetProperty("state_topic").GetString());
        Assert.Equal(new SetVolumeCommand(55), CommandParser.Parse(T, number.GetProperty("command_topic").GetString()!, "55", out _));
    }

    [Fact]
    public void Health_entities_are_diagnostics_on_the_health_topic()
    {
        var all = Build();
        foreach (var id in new[] { "sensor/arcade_pc/last_input", "binary_sensor/arcade_pc/stuck_input",
            "sensor/arcade_pc/last_boot", "sensor/arcade_pc/disk_free", "binary_sensor/arcade_pc/reboot_pending" })
        {
            var config = all[$"homeassistant/{id}/config"];
            Assert.Equal(T.Health, config.GetProperty("state_topic").GetString());
            Assert.Equal("diagnostic", config.GetProperty("entity_category").GetString());
        }

        Assert.Equal("problem", all["homeassistant/binary_sensor/arcade_pc/stuck_input/config"].GetProperty("device_class").GetString());
        Assert.False(all.ContainsKey("homeassistant/binary_sensor/arcade_pc/nas_share/config"));
    }

    [Fact]
    public void Nas_sensor_appears_only_when_a_share_is_configured()
    {
        var settings = S with { NasShare = @"\\nas\games" };
        var all = Discovery.Build(settings, T, "1").ToDictionary(m => m.Topic, m => JsonElement.Parse(m.Payload));
        Assert.Equal("connectivity", all["homeassistant/binary_sensor/arcade_pc/nas_share/config"].GetProperty("device_class").GetString());
        Assert.DoesNotContain("homeassistant/binary_sensor/arcade_pc/nas_share/config", Discovery.Removals(settings, T));
    }

    [Fact]
    public void Unconfigured_optional_sensors_are_removed() =>
        Assert.Equal(
            ["homeassistant/binary_sensor/arcade_pc/nas_share/config", "homeassistant/binary_sensor/arcade_pc/input_watch/config"],
            Discovery.Removals(S, T));
}
