using System.Text.Json;
using System.Text.Json.Serialization;

namespace CocktailAgent.Core;

/// <summary>
/// The agent's configuration (agent.json). The MQTT password is not part of
/// the file: the Windows host supplies it from its DPAPI-protected store.
/// Unknown keys are an error, so a typo fails loudly instead of being ignored.
/// </summary>
public sealed record AgentSettings
{
    public required string Host { get; init; }
    public int Port { get; init; } = 8883;
    public bool UseTls { get; init; } = true;
    public required string Username { get; init; }

    [JsonIgnore]
    public string Password { get; init; } = string.Empty;

    /// <summary>PEM file holding the private CA the broker certificate must chain to.</summary>
    public string? CaFile { get; init; }

    /// <summary>MQTT node id: topic segment, HA device identifier and unique_id prefix.</summary>
    public string NodeId { get; init; } = "cocktail_pc";
    public string DeviceName { get; init; } = "Cocktail PC";
    public string? SuggestedArea { get; init; }
    public string DiscoveryPrefix { get; init; } = "homeassistant";

    /// <summary>UNC share whose reachability the NAS sensor reports; null or empty hides the sensor.</summary>
    public string? NasShare { get; init; }

    /// <summary>LaunchBox install folder: emulators run from Emulators\ under it, BigBox is Core\BigBox.exe.</summary>
    public string LaunchBoxDir { get; init; } = @"C:\LaunchBox";

    /// <summary>
    /// Folder of an external input watcher (input-watch.heartbeat + last-input.txt)
    /// to take "last real input" from, for machines whose Windows idle timer is
    /// reset by phantom input. Null or empty: use Windows' own idle timer, and
    /// hide the "Input watcher" sensor.
    /// </summary>
    public string? InputWatchDir { get; init; }

    /// <summary>Entity id Home Assistant should create for the mute switch; default switch.&lt;nodeId&gt;_mute.</summary>
    public string? MuteEntityId { get; init; }

    /// <summary>Entity id Home Assistant should create for the volume number; default number.&lt;nodeId&gt;_volume.</summary>
    public string? VolumeEntityId { get; init; }

    [JsonIgnore]
    public string EffectiveMuteEntityId => string.IsNullOrEmpty(MuteEntityId) ? $"switch.{NodeId}_mute" : MuteEntityId;

    [JsonIgnore]
    public string EffectiveVolumeEntityId => string.IsNullOrEmpty(VolumeEntityId) ? $"number.{NodeId}_volume" : VolumeEntityId;

    [JsonIgnore]
    public bool HasInputWatch => !string.IsNullOrEmpty(InputWatchDir);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static AgentSettings Parse(string json)
    {
        // [JsonIgnore] keys count as known, so the unknown-key check would let a
        // plain-text password sit in the file unused. Refuse it explicitly.
        using (var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }))
        {
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any(p => p.Name.Equals(nameof(Password), StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("The password does not belong in agent.json (it is kept DPAPI-protected in agent.password).");
            }
        }

        var settings = JsonSerializer.Deserialize<AgentSettings>(json, JsonOptions)
            ?? throw new InvalidOperationException("Settings file is empty.");
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new InvalidOperationException("host is required.");
        }

        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"port {Port} is out of range.");
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            throw new InvalidOperationException("username is required.");
        }

        if (!Topics.IsValidNodeId(NodeId))
        {
            throw new InvalidOperationException($"nodeId '{NodeId}' may only contain a-z, 0-9 and _.");
        }

        if (string.IsNullOrWhiteSpace(LaunchBoxDir))
        {
            throw new InvalidOperationException("launchBoxDir must not be empty.");
        }

        foreach (var (key, value, domain) in new[] { ("muteEntityId", MuteEntityId, "switch"), ("volumeEntityId", VolumeEntityId, "number") })
        {
            if (!string.IsNullOrEmpty(value) && !Topics.IsValidNodeId(value.StartsWith(domain + ".", StringComparison.Ordinal) ? value[(domain.Length + 1)..] : string.Empty))
            {
                throw new InvalidOperationException($"{key} '{value}' must look like {domain}.some_name (a-z, 0-9 and _).");
            }
        }
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>The settings as agent.json: camelCase, unset optional keys left out, never the password.</summary>
    public string ToJson()
    {
        Validate();
        return JsonSerializer.Serialize(this, WriteOptions);
    }

    /// <summary>For the log: everything except the password.</summary>
    public override string ToString() =>
        $"{(UseTls ? "mqtts" : "mqtt")}://{Username}@{Host}:{Port} node={NodeId} ca={CaFile ?? "(system store)"}";
}
