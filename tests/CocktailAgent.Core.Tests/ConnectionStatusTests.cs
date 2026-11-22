using System.Net;
using System.Net.Sockets;

namespace CocktailAgent.Core.Tests;

public class ConnectionStatusTests
{
    [Fact]
    public async Task Reports_disconnected_with_a_reason_when_the_broker_is_unreachable()
    {
        var port = FreePort();
        var settings = new AgentSettings { Host = "127.0.0.1", Port = port, UseTls = false, Username = "agent", Password = "pw" };  // pragma: allowlist secret
        var seen = new List<ConnectionState>();
        await using var agent = new AgentService(settings, new FakeAudio(), new FakeSystem(), new FakeHealth(), new RecordingLog(), "1");
        agent.ConnectionChanged += (_, status) =>
        {
            lock (seen)
            {
                seen.Add(status.State);
            }
        };
        using var stop = new CancellationTokenSource();
        var run = agent.RunAsync(stop.Token);

        await Eventually.True(() => agent.Connection.State == ConnectionState.Disconnected, "disconnected");
        Assert.StartsWith("Disconnected: ", agent.Connection.Summary, StringComparison.Ordinal);
        Assert.EndsWith("(retrying)", agent.Connection.Detail, StringComparison.Ordinal);

        await stop.CancelAsync();
        await run;
        Assert.Equal(ConnectionState.Stopped, agent.Connection.State);
        lock (seen)
        {
            Assert.Equal([ConnectionState.Connecting, ConnectionState.Disconnected, ConnectionState.Stopped], seen.Distinct());
        }
    }

    [Fact]
    public void Summary_reads_well_for_each_state()
    {
        Assert.Equal("Connected to broker:8883", new ConnectionStatus(ConnectionState.Connected, "broker:8883", DateTimeOffset.Now).Summary);
        Assert.Equal("Not configured: no agent.json", ConnectionStatus.NotConfigured("no agent.json").Summary);
        Assert.Equal("Stopped", new ConnectionStatus(ConnectionState.Stopped, string.Empty, DateTimeOffset.Now).Summary);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
