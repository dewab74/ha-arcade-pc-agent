namespace CocktailAgent.Core;

/// <summary>Where the agent's broker connection stands, for the tray icon and the log.</summary>
public enum ConnectionState
{
    /// <summary>No usable settings yet; the agent is not running.</summary>
    NotConfigured,

    Connecting,

    Connected,

    /// <summary>The last attempt failed or the connection dropped; the agent keeps retrying.</summary>
    Disconnected,

    Stopped,
}

/// <param name="State">The state.</param>
/// <param name="Detail">Human-readable detail: the broker, or why it failed.</param>
/// <param name="Since">When the state was entered.</param>
public sealed record ConnectionStatus(ConnectionState State, string Detail, DateTimeOffset Since)
{
    public static ConnectionStatus NotConfigured(string detail) => new(ConnectionState.NotConfigured, detail, DateTimeOffset.Now);

    /// <summary>One line for a menu or a tooltip, e.g. "Connected to broker:8883".</summary>
    public string Summary => State switch
    {
        ConnectionState.Connected => $"Connected to {Detail}",
        ConnectionState.Connecting => $"Connecting to {Detail}…",
        ConnectionState.Disconnected => $"Disconnected: {Detail}",
        ConnectionState.NotConfigured => $"Not configured: {Detail}",
        _ => "Stopped",
    };
}
