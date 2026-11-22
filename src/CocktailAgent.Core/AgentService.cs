using System.Buffers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Protocol;

namespace CocktailAgent.Core;

/// <summary>
/// Connects to the broker, publishes discovery, availability and audio state,
/// and executes commands. Never gives up: connection failures retry with
/// backoff; one bad command or audio read is logged and skipped.
/// </summary>
public sealed class AgentService : IAsyncDisposable
{
    private static readonly TimeSpan HealthPoll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly AgentSettings _settings;
    private readonly Topics _topics;
    private readonly IAudioControl _audio;
    private readonly ISystemControl _system;
    private readonly IAgentLog _log;
    private readonly IHealthProbe _health;
    private string? _lastHealth;
    private readonly string _version;
    private readonly X509Certificate2Collection? _caBundle;
    private readonly IMqttClient _client;
    private readonly Channel<AgentCommand> _commands =
        Channel.CreateBounded<AgentCommand>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Channel<bool> _stateDirty =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly Dictionary<AgentButton, DateTimeOffset> _lastPress = [];
    private PublishedState? _lastPublished;
    private readonly Lock _screenGate = new();
    private bool _screenWantedOff;
    private DateTimeOffset _screenOffAt;
    private DateTimeOffset _lastReblank = DateTimeOffset.MinValue;
    private string? _lastTlsRejection;
    private volatile bool _republishAll;

    public AgentService(AgentSettings settings, IAudioControl audio, ISystemControl system, IHealthProbe health, IAgentLog log, string version)
    {
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _version = version;
        _topics = new Topics(settings.DiscoveryPrefix, settings.NodeId);
        _caBundle = settings.UseTls && !string.IsNullOrEmpty(settings.CaFile)
            ? CertificateValidation.LoadCaBundle(settings.CaFile)
            : null;
        _client = new MqttClientFactory().CreateMqttClient();
    }

    /// <summary>Delay after an audio change before publishing, so a slider drag is one message, not fifty.</summary>
    internal TimeSpan StateCoalesce { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Re-read the device this often even without a change event (a missed event cannot leave HA stale for long).</summary>
    internal TimeSpan StateResync { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How often health is re-read; it is published only when it changed.</summary>
    internal TimeSpan HealthInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A button pressed again within this window is ignored (double taps, retries).</summary>
    internal TimeSpan MinPressInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// After the screen wakes while it should be off, wait this long before deciding:
    /// An input watcher may record real input only every few seconds.
    /// </summary>
    internal TimeSpan ReblankDelay { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>Never blank the screen again more often than this.</summary>
    internal TimeSpan ReblankInterval { get; init; } = TimeSpan.FromSeconds(10);

    internal Topics Topics => _topics;

    public bool IsConnected => _client.IsConnected;

    /// <summary>The broker connection as last seen; changes raise <see cref="ConnectionChanged"/>.</summary>
    public ConnectionStatus Connection { get; private set; } = new(ConnectionState.Stopped, string.Empty, DateTimeOffset.Now);

    /// <summary>Raised on a background thread whenever <see cref="Connection"/> changes state or detail.</summary>
    public event EventHandler<ConnectionStatus>? ConnectionChanged;

    private string BrokerName => $"{_settings.Host}:{_settings.Port}";

    private void SetConnection(ConnectionState state, string detail)
    {
        if (Connection.State == state && Connection.Detail == detail)
        {
            return;
        }

        Connection = new ConnectionStatus(state, detail, DateTimeOffset.Now);
        ConnectionChanged?.Invoke(this, Connection);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _audio.Changed += OnAudioChanged;
        _system.DisplayChanged += OnDisplayChanged;
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
        var commandLoop = ProcessCommandsAsync(cancellationToken);
        var stateLoop = PublishStateLoopAsync(cancellationToken);
        var healthLoop = PublishHealthLoopAsync(cancellationToken);
        var backoff = TimeSpan.FromSeconds(1);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!_client.IsConnected)
                    {
                        if (Connection.State == ConnectionState.Connected)
                        {
                            SetConnection(ConnectionState.Disconnected, "connection lost; reconnecting");
                        }
                        else if (Connection.State != ConnectionState.Disconnected)
                        {
                            SetConnection(ConnectionState.Connecting, BrokerName);
                        }

                        await ConnectAsync(cancellationToken).ConfigureAwait(false);
                        SetConnection(ConnectionState.Connected, BrokerName);
                        backoff = TimeSpan.FromSeconds(1);
                    }
                    else if (_republishAll)
                    {
                        _republishAll = false;
                        await PublishAllAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await Task.Delay(HealthPoll, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Warn($"MQTT: {Describe(ex)}; retrying in {backoff.TotalSeconds:0}s");
                    SetConnection(ConnectionState.Disconnected, $"{ShortReason(ex)} (retrying)");
                    try
                    {
                        await Task.Delay(Jitter(backoff), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                }
            }
        }
        finally
        {
            _audio.Changed -= OnAudioChanged;
            _system.DisplayChanged -= OnDisplayChanged;
            _client.ApplicationMessageReceivedAsync -= OnMessageAsync;
            _commands.Writer.TryComplete();
            _stateDirty.Writer.TryComplete();
            await StopAsync().ConfigureAwait(false);
            SetConnection(ConnectionState.Stopped, string.Empty);
        }

        await IgnoreCancellation(commandLoop).ConfigureAwait(false);
        await IgnoreCancellation(stateLoop).ConfigureAwait(false);
        await IgnoreCancellation(healthLoop).ConfigureAwait(false);
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _lastTlsRejection = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        var result = await _client.ConnectAsync(BuildOptions(), timeout.Token).ConfigureAwait(false);
        if (result.ResultCode != MqttClientConnectResultCode.Success)
        {
            throw new InvalidOperationException($"broker refused connection: {result.ResultCode} {result.ReasonString}".TrimEnd());
        }

        foreach (var filter in new[] { _topics.MuteSet, _topics.VolumeSet, _topics.ScreenSet, _topics.CommandFilter })
        {
            await SubscribeAsync(filter, commandStyle: true, cancellationToken).ConfigureAwait(false);
        }

        await SubscribeAsync(_topics.HomeAssistantStatus, commandStyle: false, cancellationToken).ConfigureAwait(false);
        _lastPublished = null;
        await PublishAllAsync(cancellationToken).ConfigureAwait(false);
        _log.Info($"Connected to {_settings.Host}:{_settings.Port} as {_settings.Username} ({_client.Options?.ProtocolVersion})");
    }

    internal MqttClientOptions BuildOptions()
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_settings.Host, _settings.Port)
            .WithClientId($"cocktail-agent-{_settings.NodeId}")
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithCleanSession(true)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithTimeout(ConnectTimeout)
            .WithWillTopic(_topics.Availability)
            .WithWillPayload(Availability.Offline)
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithCredentials(_settings.Username, _settings.Password);

        if (_settings.UseTls)
        {
            var caBundle = _caBundle;
            builder.WithTlsOptions(tls =>
            {
                tls.UseTls();
                tls.WithTargetHost(_settings.Host);
                tls.WithRevocationMode(X509RevocationMode.NoCheck);
                if (caBundle is not null)
                {
                    tls.WithCertificateValidationHandler(args =>
                    {
                        _lastTlsRejection = CertificateValidation.Check(args.Certificate, args.Chain, args.SslPolicyErrors, caBundle);
                        return _lastTlsRejection is null;
                    });
                }
            });
        }

        return builder.Build();
    }

    private async Task SubscribeAsync(string filter, bool commandStyle, CancellationToken cancellationToken)
    {
        var options = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f =>
            {
                f.WithTopic(filter).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
                if (commandStyle)
                {
                    // A stale retained command must never replay (think: a retained "shutdown").
                    // RetainAsPublished keeps the flag on live deliveries, so those are refused too.
                    f.WithRetainHandling(MqttRetainHandling.DoNotSendOnSubscribe).WithRetainAsPublished(true);
                }
            })
            .Build();
        var result = await _client.SubscribeAsync(options, cancellationToken).ConfigureAwait(false);
        foreach (var item in result.Items)
        {
            if (item.ResultCode > MqttClientSubscribeResultCode.GrantedQoS2)
            {
                throw new InvalidOperationException($"broker refused subscription to '{filter}': {item.ResultCode}");
            }
        }
    }

    private async Task PublishAllAsync(CancellationToken cancellationToken)
    {
        foreach (var message in Discovery.Build(_settings, _topics, _version))
        {
            await PublishAsync(message.Topic, message.Payload, retain: true, cancellationToken).ConfigureAwait(false);
        }

        foreach (var topic in Discovery.Removals(_settings, _topics))
        {
            await PublishAsync(topic, string.Empty, retain: true, cancellationToken).ConfigureAwait(false);
        }

        await PublishAsync(_topics.Availability, Availability.Online, retain: true, cancellationToken).ConfigureAwait(false);
        await PublishStateAsync(force: true, cancellationToken).ConfigureAwait(false);
        await PublishHealthAsync(force: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishStateAsync(bool force, CancellationToken cancellationToken)
    {
        AudioState audio;
        try
        {
            audio = _audio.Read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"Audio read failed: {ex.Message}");
            return;
        }

        var state = new PublishedState(audio.Volume, audio.Muted, _system.DisplayOn);
        if (!force && state == _lastPublished)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new { volume = state.Volume, muted = state.Muted, screen = state.ScreenOn });
        await PublishAsync(_topics.State, payload, retain: true, cancellationToken).ConfigureAwait(false);
        _lastPublished = state;
    }

    private async Task PublishHealthAsync(bool force, CancellationToken cancellationToken)
    {
        string payload;
        try
        {
            payload = _health.Read().ToJson();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"Health read failed: {ex.Message}");
            return;
        }

        if (!force && payload == _lastHealth)
        {
            return;
        }

        await PublishAsync(_topics.Health, payload, retain: true, cancellationToken).ConfigureAwait(false);
        if (_lastHealth is not null && payload.Contains("\"stuck\":true", StringComparison.Ordinal) != _lastHealth.Contains("\"stuck\":true", StringComparison.Ordinal))
        {
            _log.Warn($"Stuck input changed: {payload}");
        }

        _lastHealth = payload;
    }

    private async Task PublishHealthLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(HealthInterval, cancellationToken).ConfigureAwait(false);
            if (!_client.IsConnected)
            {
                continue;
            }

            try
            {
                await PublishHealthAsync(force: false, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Warn($"Health publish failed: {ex.Message}");
            }
        }
    }

    private async Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken)
    {
        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(retain)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            var result = await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException($"broker rejected publish to '{topic}': {result.ReasonCode} {result.ReasonString}".TrimEnd());
            }
        }
        finally
        {
            _publishLock.Release();
        }
    }

    private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var message = args.ApplicationMessage;
        var payload = Encoding.UTF8.GetString(message.Payload.ToArray());

        if (message.Topic == _topics.HomeAssistantStatus)
        {
            // HA restarted: it forgot discovery and state until we send them again.
            if (payload == Availability.Online)
            {
                _republishAll = true;
            }

            return Task.CompletedTask;
        }

        if (message.Retain)
        {
            _log.Warn($"Ignored retained command on {message.Topic} (commands must not be retained)");
            return Task.CompletedTask;
        }

        var command = CommandParser.Parse(_topics, message.Topic, payload, out var rejection);
        if (command is null)
        {
            _log.Warn($"Ignored message: {rejection}");
        }
        else if (!_commands.Writer.TryWrite(command))
        {
            _log.Warn($"Command queue full, dropped {command}");
        }

        return Task.CompletedTask;
    }

    private async Task ProcessCommandsAsync(CancellationToken cancellationToken)
    {
        await foreach (var command in _commands.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Fail($"Command {command} failed", ex);
            }
        }
    }

    internal async Task ExecuteAsync(AgentCommand command, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case SetMutedCommand mute:
                _audio.SetMuted(mute.Muted);
                _log.Info($"Mute set to {mute.Muted}");
                SignalState();
                break;
            case SetVolumeCommand volume:
                _audio.SetVolume(volume.Volume);
                _log.Info($"Volume set to {volume.Volume}");
                SignalState();
                break;
            case SetScreenCommand screen:
                lock (_screenGate)
                {
                    _screenWantedOff = !screen.On;
                    _screenOffAt = DateTimeOffset.UtcNow;
                }

                if (screen.On)
                {
                    _system.ScreenOn();
                    _log.Info("Screen on");
                }
                else
                {
                    _system.ScreenOff();
                    _log.Info("Screen off");
                }

                SignalState();
                break;
            case ReblankScreenCommand:
                await Task.Delay(ReblankDelay, cancellationToken).ConfigureAwait(false);
                ReblankIfStillWanted();
                break;
            case PressCommand press:
                var now = DateTimeOffset.UtcNow;
                if (_lastPress.TryGetValue(press.Button, out var last) && now - last < MinPressInterval)
                {
                    _log.Warn($"Ignored {ButtonNames.Of(press.Button)}: pressed again within {MinPressInterval.TotalSeconds:0}s");
                    return;
                }

                _lastPress[press.Button] = now;
                await PressAsync(press.Button, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown command type");
        }
    }

    private async Task PressAsync(AgentButton button, CancellationToken cancellationToken)
    {
        _log.Info($"Command {ButtonNames.Of(button)} received");
        switch (button)
        {
            case AgentButton.CloseEmulators:
                _log.Info(await _system.CloseEmulatorsAsync(cancellationToken).ConfigureAwait(false));
                break;
            case AgentButton.CloseFrontend:
                _log.Info(await _system.CloseFrontendAsync(cancellationToken).ConfigureAwait(false));
                break;
            case AgentButton.RestartBigBox:
                _log.Info(await _system.RestartBigBoxAsync(cancellationToken).ConfigureAwait(false));
                break;
            case AgentButton.Shutdown:
            case AgentButton.Reboot:
                // Close everything the graceful way first, so MAME writes hiscores/nvram
                // and BigBox saves its settings, then hand over to Windows. A close that
                // fails must not cancel the power action: Windows ends what is left.
                try
                {
                    _log.Info(await _system.CloseFrontendAsync(cancellationToken).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warn($"Closing before {ButtonNames.Of(button)} failed, continuing anyway: {ex.Message}");
                }

                await PublishAsync(_topics.Availability, Availability.Offline, retain: true, cancellationToken).ConfigureAwait(false);
                if (button == AgentButton.Shutdown)
                {
                    _log.Info("Shutting Windows down");
                    _system.Shutdown();
                }
                else
                {
                    _log.Info("Restarting Windows");
                    _system.Reboot();
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(button));
        }
    }

    private void OnAudioChanged(object? sender, EventArgs e) => SignalState();

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        SignalState();
        if (_system.DisplayOn != true)
        {
            return;
        }

        lock (_screenGate)
        {
            if (!_screenWantedOff)
            {
                return;
            }
        }

        // Woke while it should be off: decide after the input source had time to record real input.
        _commands.Writer.TryWrite(new ReblankScreenCommand());
    }

    /// <summary>
    /// Keeps a screen turned off from HA off against phantom wakes: only real input
    /// since it was turned off (per the input source) or an ON from HA turns it back on.
    /// </summary>
    private void ReblankIfStillWanted()
    {
        if (_system.DisplayOn != true)
        {
            return;
        }

        lock (_screenGate)
        {
            if (!_screenWantedOff)
            {
                return;
            }

            var lastInput = _system.LastRealInput;
            if (lastInput is null || lastInput > _screenOffAt)
            {
                _screenWantedOff = false;
                _log.Info(lastInput is null
                    ? "Screen woke and the input watcher is not running; leaving it on"
                    : $"Screen woken by input at the cabinet ({lastInput:HH:mm:ss}); leaving it on");
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (now - _lastReblank < ReblankInterval)
            {
                return;
            }

            _lastReblank = now;
        }

        _log.Info("Screen woke without input at the cabinet; turning it off again");
        _system.ScreenOff();
    }

    private void SignalState() => _stateDirty.Writer.TryWrite(true);

    private async Task PublishStateLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var resync = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            resync.CancelAfter(StateResync);
            try
            {
                await _stateDirty.Reader.ReadAsync(resync.Token).ConfigureAwait(false);
                await Task.Delay(StateCoalesce, cancellationToken).ConfigureAwait(false);
                _stateDirty.Reader.TryRead(out _);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Periodic resync: fall through and publish only if something changed.
            }
            catch (ChannelClosedException)
            {
                return;
            }

            if (!_client.IsConnected)
            {
                continue;
            }

            try
            {
                await PublishStateAsync(force: false, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Warn($"State publish failed: {ex.Message}");
            }
        }
    }

    private async Task StopAsync()
    {
        if (!_client.IsConnected)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await PublishAsync(_topics.Availability, Availability.Offline, retain: true, timeout.Token).ConfigureAwait(false);
            await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn($"Clean disconnect failed (the broker's last will marks us offline): {ex.Message}");
        }
    }

    private string Describe(Exception ex)
    {
        var reason = _lastTlsRejection is { } tls ? $" [TLS: {tls}]" : string.Empty;
        var inner = ex.InnerException is { } i && i.Message != ex.Message ? $" ({i.Message})" : string.Empty;
        return $"{ex.GetType().Name}: {ex.Message}{inner}{reason}";
    }

    /// <summary>The most useful single reason for a failed attempt, for the tray menu.</summary>
    private string ShortReason(Exception ex) =>
        _lastTlsRejection is { } tls ? $"TLS: {tls}" : (ex.InnerException?.Message ?? ex.Message);

    private static TimeSpan Jitter(TimeSpan delay) =>
        TimeSpan.FromMilliseconds(delay.TotalMilliseconds * (0.8 + (Random.Shared.NextDouble() * 0.4)));

    private static async Task IgnoreCancellation(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        _publishLock.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>What the state topic carries.</summary>
internal readonly record struct PublishedState(int Volume, bool Muted, bool? ScreenOn);

/// <summary>Internal: re-check a screen that woke while it should be off.</summary>
internal sealed record ReblankScreenCommand : AgentCommand;
