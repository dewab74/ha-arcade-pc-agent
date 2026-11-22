namespace CocktailAgent.Core;

/// <summary>Every MQTT topic the agent uses, derived from the node id.</summary>
public sealed class Topics
{
    public Topics(string discoveryPrefix, string nodeId)
    {
        if (!IsValidNodeId(nodeId))
        {
            throw new ArgumentException($"Invalid node id '{nodeId}'.", nameof(nodeId));
        }

        DiscoveryPrefix = discoveryPrefix;
        NodeId = nodeId;
        Base = $"cocktail-agent/{nodeId}";
    }

    public string DiscoveryPrefix { get; }
    public string NodeId { get; }
    public string Base { get; }

    public string Availability => $"{Base}/availability";
    public string State => $"{Base}/state";
    public string Health => $"{Base}/health";
    public string MuteSet => $"{Base}/mute/set";
    public string VolumeSet => $"{Base}/volume/set";
    public string ScreenSet => $"{Base}/screen/set";
    public string CommandFilter => $"{Base}/command/+";
    public string HomeAssistantStatus => $"{DiscoveryPrefix}/status";

    public string Command(AgentButton button) => $"{Base}/command/{ButtonNames.Of(button)}";

    public string Discovery(string component, string objectId) => $"{DiscoveryPrefix}/{component}/{NodeId}/{objectId}/config";

    public static bool IsValidNodeId(string? nodeId) =>
        !string.IsNullOrEmpty(nodeId) && nodeId.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');
}
