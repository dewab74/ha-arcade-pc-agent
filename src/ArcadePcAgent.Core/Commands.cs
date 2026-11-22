using System.Globalization;

namespace ArcadePcAgent.Core;

/// <summary>The buttons the agent exposes. Each runs as one serialized job.</summary>
public enum AgentButton
{
    CloseEmulators,
    CloseFrontend,
    RestartBigBox,
    Shutdown,
    Reboot,
}

public static class ButtonNames
{
    public static string Of(AgentButton button) => button switch
    {
        AgentButton.CloseEmulators => "close_emulators",
        AgentButton.CloseFrontend => "close_frontend",
        AgentButton.RestartBigBox => "restart_bigbox",
        AgentButton.Shutdown => "shutdown",
        AgentButton.Reboot => "reboot",
        _ => throw new ArgumentOutOfRangeException(nameof(button)),
    };

    public static bool TryParse(string name, out AgentButton button)
    {
        foreach (var candidate in Enum.GetValues<AgentButton>())
        {
            if (Of(candidate) == name)
            {
                button = candidate;
                return true;
            }
        }

        button = default;
        return false;
    }
}

public abstract record AgentCommand;
public sealed record SetMutedCommand(bool Muted) : AgentCommand;
public sealed record SetVolumeCommand(int Volume) : AgentCommand;
public sealed record SetScreenCommand(bool On) : AgentCommand;
public sealed record PressCommand(AgentButton Button) : AgentCommand;

public static class CommandParser
{
    /// <summary>Payload Home Assistant's MQTT button sends by default.</summary>
    public const string PressPayload = "PRESS";

    /// <summary>The command for a message, or null with a reason when it is not one.</summary>
    public static AgentCommand? Parse(Topics topics, string topic, string payload, out string? rejection)
    {
        ArgumentNullException.ThrowIfNull(topics);
        rejection = null;
        payload = payload.Trim();

        if (topic == topics.MuteSet || topic == topics.ScreenSet)
        {
            bool? on = payload.Equals("ON", StringComparison.OrdinalIgnoreCase) ? true
                : payload.Equals("OFF", StringComparison.OrdinalIgnoreCase) ? false
                : null;
            if (on is null)
            {
                rejection = $"{(topic == topics.MuteSet ? "mute" : "screen")} payload must be ON or OFF, got '{payload}'";
                return null;
            }

            return topic == topics.MuteSet ? new SetMutedCommand(on.Value) : new SetScreenCommand(on.Value);
        }

        if (topic == topics.VolumeSet)
        {
            if (double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && !double.IsNaN(value) && value is >= 0 and <= 100)
            {
                return new SetVolumeCommand((int)Math.Round(value, MidpointRounding.AwayFromZero));
            }

            rejection = $"volume payload must be a number 0-100, got '{payload}'";
            return null;
        }

        var prefix = topics.Base + "/command/";
        if (topic.StartsWith(prefix, StringComparison.Ordinal))
        {
            var name = topic[prefix.Length..];
            if (!ButtonNames.TryParse(name, out var button))
            {
                rejection = $"unknown command '{name}'";
                return null;
            }

            if (payload != PressPayload)
            {
                rejection = $"command '{name}' needs payload {PressPayload}, got '{payload}'";
                return null;
            }

            return new PressCommand(button);
        }

        rejection = $"not a command topic: {topic}";
        return null;
    }
}
