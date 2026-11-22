using System.Text.Json.Nodes;

namespace CocktailAgent.Core;

/// <summary>Home Assistant MQTT discovery for the agent's entities.</summary>
public static class Discovery
{
    public sealed record DiscoveryMessage(string Topic, string Payload);

    /// <summary>
    /// Entity ids are suggested with default_entity_id (&lt;component&gt;.&lt;nodeId&gt;_&lt;object&gt;).
    /// Mute and volume can be pinned to other ids in the settings, e.g. to take
    /// over the ids of entities the agent replaces so dashboards keep working.
    /// </summary>
    public static IReadOnlyList<DiscoveryMessage> Build(AgentSettings settings, Topics topics, string version)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(topics);

        var messages = new List<DiscoveryMessage>
        {
            Entity(settings, topics, version, "switch", "mute", "Muted", settings.EffectiveMuteEntityId, new JsonObject
            {
                ["icon"] = "mdi:volume-off",
                ["state_topic"] = topics.State,
                ["value_template"] = "{{ 'ON' if value_json.muted else 'OFF' }}",
                ["command_topic"] = topics.MuteSet,
                ["payload_on"] = "ON",
                ["payload_off"] = "OFF",
            }),
            Entity(settings, topics, version, "switch", "screen", "Screen", $"switch.{settings.NodeId}_screen", new JsonObject
            {
                ["icon"] = "mdi:monitor",
                ["state_topic"] = topics.State,
                // screen is null until Windows reports the display state: unknown, not off.
                ["value_template"] = "{{ 'None' if value_json.screen is none else ('ON' if value_json.screen else 'OFF') }}",
                ["command_topic"] = topics.ScreenSet,
                ["payload_on"] = "ON",
                ["payload_off"] = "OFF",
            }),
            Entity(settings, topics, version, "number", "volume", "Volume", settings.EffectiveVolumeEntityId, new JsonObject
            {
                ["icon"] = "mdi:volume-high",
                ["state_topic"] = topics.State,
                ["value_template"] = "{{ value_json.volume }}",
                ["command_topic"] = topics.VolumeSet,
                ["min"] = 0,
                ["max"] = 100,
                ["step"] = 1,
                ["mode"] = "slider",
                ["unit_of_measurement"] = "%",
            }),
        };

        foreach (var (button, name, icon) in new[]
        {
            (AgentButton.CloseEmulators, "Close emulators", "mdi:close-box-outline"),
            (AgentButton.CloseFrontend, "Close BigBox and LaunchBox", "mdi:exit-to-app"),
            (AgentButton.RestartBigBox, "Restart BigBox", "mdi:application-cog-outline"),
            (AgentButton.Shutdown, "Shut down", "mdi:power"),
            (AgentButton.Reboot, "Restart PC", "mdi:restart"),
        })
        {
            var objectId = ButtonNames.Of(button);
            var extra = new JsonObject
            {
                ["icon"] = icon,
                ["command_topic"] = topics.Command(button),
                ["payload_press"] = CommandParser.PressPayload,
            };
            if (button == AgentButton.Reboot)
            {
                extra["device_class"] = "restart";
            }

            messages.Add(Entity(settings, topics, version, "button", objectId, name, $"button.{settings.NodeId}_{objectId}", extra));
        }

        var health = topics.Health;
        var diagnostics = new List<(string Component, string ObjectId, string Name, JsonObject Extra)>
        {
            ("sensor", "last_input", "Last input", new JsonObject
            {
                ["device_class"] = "timestamp",
                ["value_template"] = "{{ value_json.last_input or 'None' }}",
                ["icon"] = "mdi:gamepad-variant-outline",
            }),
            ("binary_sensor", "stuck_input", "Stuck input", new JsonObject
            {
                ["device_class"] = "problem",
                ["value_template"] = "{{ 'ON' if value_json.stuck else 'OFF' }}",
                ["json_attributes_topic"] = health,
                ["json_attributes_template"] = "{{ {'stuck_inputs': value_json.stuck_inputs} | tojson }}",
                ["icon"] = "mdi:gamepad-round-outline",
            }),
            ("sensor", "last_boot", "Last boot", new JsonObject
            {
                ["device_class"] = "timestamp",
                ["value_template"] = "{{ value_json.last_boot }}",
            }),
            ("sensor", "disk_free", "Disk free", new JsonObject
            {
                ["device_class"] = "data_size",
                ["unit_of_measurement"] = "GB",
                ["state_class"] = "measurement",
                ["suggested_display_precision"] = 1,
                ["value_template"] = "{{ value_json.disk_free_gb }}",
                ["json_attributes_topic"] = health,
                ["json_attributes_template"] = "{{ {'percent_free': value_json.disk_free_pct} | tojson }}",
                ["icon"] = "mdi:harddisk",
            }),
            ("binary_sensor", "reboot_pending", "Reboot pending", new JsonObject
            {
                ["device_class"] = "update",
                ["value_template"] = "{{ 'ON' if value_json.reboot_pending else 'OFF' }}",
                ["icon"] = "mdi:restart-alert",
            }),
        };
        if (settings.HasInputWatch)
        {
            diagnostics.Add(("binary_sensor", "input_watch", "Input watcher", new JsonObject
            {
                ["device_class"] = "running",
                ["value_template"] = "{{ 'ON' if value_json.input_watch else 'OFF' }}",
            }));
        }

        if (!string.IsNullOrEmpty(settings.NasShare))
        {
            diagnostics.Add(("binary_sensor", "nas_share", "NAS share", new JsonObject
            {
                ["device_class"] = "connectivity",
                ["value_template"] = "{{ 'None' if value_json.nas is none else ('ON' if value_json.nas else 'OFF') }}",
            }));
        }

        foreach (var (component, objectId, name, extra) in diagnostics)
        {
            extra["state_topic"] = health;
            extra["entity_category"] = "diagnostic";
            messages.Add(Entity(settings, topics, version, component, objectId, name, $"{component}.{settings.NodeId}_{objectId}", extra));
        }

        return messages;
    }

    /// <summary>
    /// Discovery topics to clear (empty retained payload) because the entity is not
    /// configured: Home Assistant removes an entity whose config is emptied.
    /// </summary>
    public static IReadOnlyList<string> Removals(AgentSettings settings, Topics topics)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(topics);
        var removals = new List<string>();
        if (string.IsNullOrEmpty(settings.NasShare))
        {
            removals.Add(topics.Discovery("binary_sensor", "nas_share"));
        }

        if (!settings.HasInputWatch)
        {
            removals.Add(topics.Discovery("binary_sensor", "input_watch"));
        }

        return removals;
    }

    private static DiscoveryMessage Entity(
        AgentSettings settings, Topics topics, string version, string component, string objectId, string name, string defaultEntityId, JsonObject extra)
    {
        var device = new JsonObject
        {
            ["identifiers"] = new JsonArray(settings.NodeId),
            ["name"] = settings.DeviceName,
            ["manufacturer"] = "ha-cocktail-agent",
            ["model"] = "Cocktail agent",
            ["sw_version"] = version,
        };
        if (!string.IsNullOrEmpty(settings.SuggestedArea))
        {
            device["suggested_area"] = settings.SuggestedArea;
        }

        var payload = new JsonObject
        {
            ["name"] = name,
            ["unique_id"] = $"{settings.NodeId}_{objectId}",
            ["default_entity_id"] = defaultEntityId,
            ["availability_topic"] = topics.Availability,
            ["payload_available"] = Availability.Online,
            ["payload_not_available"] = Availability.Offline,
            ["qos"] = 1,
            ["device"] = device,
        };
        foreach (var (key, value) in extra)
        {
            payload[key] = value?.DeepClone();
        }

        return new DiscoveryMessage(topics.Discovery(component, objectId), payload.ToJsonString());
    }
}

public static class Availability
{
    public const string Online = "online";
    public const string Offline = "offline";
}
