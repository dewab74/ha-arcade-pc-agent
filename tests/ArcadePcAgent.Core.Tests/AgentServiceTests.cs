using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace ArcadePcAgent.Core.Tests;

/// <summary>The agent against a real (in-process MQTTnet) broker, observed the way Home Assistant sees it.</summary>
public sealed class AgentServiceTests : IAsyncLifetime
{
    private readonly FakeAudio _audio = new();
    private readonly FakeSystem _system = new();
    private readonly RecordingLog _log = new();
    private readonly FakeHealth _health = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, string> _seen = new();
    private MqttServer? _server;
    private IMqttClient? _observer;
    private AgentService? _agent;
    private Task? _run;
    private int _port;

    private Topics T => _agent!.Topics;

    public async ValueTask InitializeAsync()
    {
        await StartBrokerAsync();
        var settings = new AgentSettings { Host = "localhost", Port = _port, UseTls = false, Username = "agent", Password = "pw" };  // pragma: allowlist secret
        _agent = new AgentService(settings, _audio, _system, _health, _log, "9.9.9")
        {
            StateCoalesce = TimeSpan.FromMilliseconds(20),
            MinPressInterval = TimeSpan.FromSeconds(30),
            ReblankDelay = TimeSpan.FromMilliseconds(50),
            ReblankInterval = TimeSpan.Zero,
            HealthInterval = TimeSpan.FromMilliseconds(50),
        };
        _run = _agent.RunAsync(_stop.Token);
        await Eventually.True(() => _agent.IsConnected, "agent connected");
        await Eventually.True(() => _agent.Connection.State == ConnectionState.Connected, "connection status connected");

        _observer = new MqttClientFactory().CreateMqttClient();
        _observer.ApplicationMessageReceivedAsync += e =>
        {
            _seen[e.ApplicationMessage.Topic] = e.ApplicationMessage.ConvertPayloadToString();
            return Task.CompletedTask;
        };
        await _observer.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("localhost", _port).WithClientId("observer").Build());
        await _observer.SubscribeAsync("#");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_run is not null)
        {
            await _run;
        }

        if (_agent is not null)
        {
            await _agent.DisposeAsync();
        }

        _observer?.Dispose();
        if (_server is not null)
        {
            await _server.StopAsync();
            _server.Dispose();
        }

        _stop.Dispose();
    }

    [Fact]
    public async Task Publishes_discovery_online_and_state_retained()
    {
        await Eventually.True(() => _seen.Count(kv => kv.Key.StartsWith("homeassistant/", StringComparison.Ordinal) && kv.Value.Length > 0) == 13, "thirteen discovery configs (no NAS share or input watcher configured)");
        await Eventually.True(() => _seen.TryGetValue(T.Availability, out var a) && a == Availability.Online, "online");
        await Eventually.True(() => _seen.TryGetValue(T.State, out var s) && s == """{"volume":40,"muted":false,"screen":true}""", "state");
    }

    [Fact]
    public async Task Health_is_published_and_republished_when_it_changes()
    {
        await Eventually.True(() => _seen.TryGetValue(T.Health, out var h) && h.Contains("\"disk_free_gb\":301.8", StringComparison.Ordinal), "health retained");
        _health.Snapshot = _health.Snapshot with { StuckInputs = ["joystick 2 button 11 held since 2026-11-21 10:31"] };
        await Eventually.True(() => _seen.TryGetValue(T.Health, out var h) && h.Contains("\"stuck\":true", StringComparison.Ordinal), "stuck pushed");
        Assert.Contains(_log.Lines, l => l.Contains("Stuck input changed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mute_and_volume_commands_change_audio_and_report_back()
    {
        await PublishAsync(T.MuteSet, "ON");
        await Eventually.True(() => _audio.Read().Muted, "muted");
        await Eventually.True(() => State().GetProperty("muted").GetBoolean(), "state says muted");

        await PublishAsync(T.VolumeSet, "65");
        await Eventually.True(() => _audio.Read().Volume == 65, "volume 65");
        await Eventually.True(() => State().GetProperty("volume").GetInt32() == 65, "state says 65");
    }

    [Fact]
    public async Task A_change_made_at_the_cabinet_is_pushed()
    {
        await Eventually.True(() => _seen.ContainsKey(T.State), "initial state");
        _audio.Set(new AudioState(12, true));
        await Eventually.True(() => State().GetProperty("volume").GetInt32() == 12 && State().GetProperty("muted").GetBoolean(), "pushed");
    }

    [Fact]
    public async Task Screen_off_and_on_from_home_assistant()
    {
        await PublishAsync(T.ScreenSet, "OFF");
        await Eventually.True(() => _system.DisplayOn == false, "screen off");
        await Eventually.True(() => State().TryGetProperty("screen", out var s) && s.ValueKind == JsonValueKind.False, "state says off");
        await PublishAsync(T.ScreenSet, "ON");
        await Eventually.True(() => _system.DisplayOn == true, "screen on");
        await Eventually.True(() => State().GetProperty("screen").GetBoolean(), "state says on");
    }

    [Fact]
    public async Task A_phantom_wake_is_blanked_again()
    {
        await PublishAsync(T.ScreenSet, "OFF");
        await Eventually.True(() => _system.DisplayOn == false, "screen off");
        _system.SetDisplay(true); // woke, but input-watch saw nothing since
        await Eventually.True(() => _system.DisplayOn == false, "blanked again");
        Assert.Contains(_log.Lines, l => l.Contains("without input at the cabinet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Real_input_wakes_it_for_good()
    {
        await PublishAsync(T.ScreenSet, "OFF");
        await Eventually.True(() => _system.DisplayOn == false, "screen off");
        _system.LastRealInput = DateTimeOffset.UtcNow.AddSeconds(1);
        _system.SetDisplay(true);
        await Eventually.True(() => _log.Lines.Any(l => l.Contains("woken by input", StringComparison.Ordinal)), "accepted");
        Assert.True(_system.DisplayOn);
        Assert.Equal(1, _system.Calls.Count(c => c == "screen_off"));
    }

    [Fact]
    public async Task Without_input_watch_any_wake_is_accepted()
    {
        await PublishAsync(T.ScreenSet, "OFF");
        await Eventually.True(() => _system.DisplayOn == false, "screen off");
        _system.LastRealInput = null;
        _system.SetDisplay(true);
        await Eventually.True(() => _log.Lines.Any(l => l.Contains("input watcher is not running", StringComparison.Ordinal)), "accepted");
        Assert.Equal(1, _system.Calls.Count(c => c == "screen_off"));
    }

    [Fact]
    public async Task Restart_bigbox_button_restarts_it()
    {
        await PublishAsync(T.Command(AgentButton.RestartBigBox), "PRESS");
        await Eventually.True(() => _system.Calls.Contains("restart_bigbox"), "restart_bigbox called");
    }

    [Fact]
    public async Task A_retained_command_is_never_acted_on()
    {
        await PublishAsync(T.Command(AgentButton.Shutdown), "PRESS", retain: true);
        await PublishAsync(T.MuteSet, "ON", retain: true);
        await Eventually.True(() => _log.Lines.Count(l => l.Contains("retained command", StringComparison.Ordinal)) == 2, "both refused");
        Assert.Empty(_system.Calls);
        Assert.False(_audio.Read().Muted);
    }

    [Fact]
    public async Task Shutdown_closes_everything_goes_offline_then_shuts_down()
    {
        await PublishAsync(T.Command(AgentButton.Shutdown), "PRESS");
        await Eventually.True(() => _system.Calls.Contains("shutdown"), "shutdown called");
        Assert.Equal(["close_frontend", "shutdown"], _system.Calls.ToArray());
        await Eventually.True(() => _seen.TryGetValue(T.Availability, out var a) && a == Availability.Offline, "offline first");
    }

    [Fact]
    public async Task Reboot_still_happens_when_closing_fails()
    {
        _system.FailClose = true;
        await PublishAsync(T.Command(AgentButton.Reboot), "PRESS");
        await Eventually.True(() => _system.Calls.Contains("reboot"), "reboot called despite the close failure");
        Assert.Contains(_log.Lines, l => l.Contains("continuing anyway", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_press_inside_the_window_is_ignored()
    {
        await PublishAsync(T.Command(AgentButton.CloseEmulators), "PRESS");
        await PublishAsync(T.Command(AgentButton.CloseEmulators), "PRESS");
        await PublishAsync(T.Command(AgentButton.Reboot), "PRESS");
        await Eventually.True(() => _system.Calls.Contains("reboot"), "reboot ran");
        Assert.Equal(1, _system.Calls.Count(c => c == "close_emulators"));
        Assert.Contains(_log.Lines, l => l.Contains("pressed again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Home_assistant_coming_online_gets_everything_again()
    {
        await Eventually.True(() => _seen.ContainsKey("homeassistant/button/arcade_pc/reboot/config"), "initial discovery");
        _seen.Clear();
        await PublishAsync("homeassistant/status", "online");
        await Eventually.True(() => _seen.ContainsKey("homeassistant/button/arcade_pc/reboot/config"), "republished", timeoutMs: 10_000);
    }

    [Fact]
    public async Task Broker_restart_reconnects()
    {
        var server = _server!;
        await server.StopAsync();
        await Eventually.True(() => !_agent!.IsConnected, "dropped");
        await server.StartAsync();
        await Eventually.True(() => _agent!.IsConnected, "reconnected", timeoutMs: 15_000);
    }

    private JsonElement State() => JsonElement.Parse(_seen.TryGetValue(T.State, out var s) ? s : "{}");

    private async Task PublishAsync(string topic, string payload, bool retain = false)
    {
        var message = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build();
        await _observer!.PublishAsync(message);
    }

    private async Task StartBrokerAsync()
    {
        // The free port is released before the broker binds it; retry if something took it.
        for (var attempt = 1; ; attempt++)
        {
            _port = FreePort();
            var factory = new MqttServerFactory();
            var server = factory.CreateMqttServer(factory.CreateServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(_port).Build());
            try
            {
                await server.StartAsync();
                _server = server;
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < 5)
            {
                server.Dispose();
            }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
