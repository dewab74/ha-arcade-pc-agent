using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using CocktailAgent;
using CocktailAgent.Core;

// Cocktail agent: lets Home Assistant control a LaunchBox / BigBox Windows PC
// over MQTT (volume, mute, screen, close/restart/shutdown buttons, health).
// Runs in the signed-in user's session with a tray icon (connection status,
// Settings, Open log, About, Quit). Files in %LOCALAPPDATA%\CocktailAgent:
//   agent.json      settings (see AgentSettings; editable from the tray)
//   agent.password  MQTT password, DPAPI-protected for this user (base64)
//   agent.log       log
var files = new SettingsFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CocktailAgent"));
Directory.CreateDirectory(files.DataDir);
var log = new FileLog(files.LogPath);
var version = typeof(AgentService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

using var single = new Mutex(initiallyOwned: true, @"Local\CocktailAgent", out var first);
if (!first)
{
    log.Warn("Another instance is already running; exiting");
    return 2;
}

try
{
    using var quit = new CancellationTokenSource();
    AppDomain.CurrentDomain.ProcessExit += (_, _) => quit.Cancel();
    using var tray = new TrayApp(version, files, log);
    var settingsSaved = new SemaphoreSlim(0);
    tray.SettingsSaved += (_, _) => settingsSaved.Release();
    tray.QuitRequested += (_, _) => quit.Cancel();
    tray.Start();
    log.Info($"Cocktail agent {version} starting");

    while (!quit.IsCancellationRequested)
    {
        AgentSettings settings;
        try
        {
            settings = files.Load();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or CryptographicException or FormatException or UnauthorizedAccessException)
        {
            var reason = ex is FileNotFoundException ? "no settings yet" : ex.Message;
            log.Warn($"Not configured: {reason}");
            tray.SetStatus(ConnectionStatus.NotConfigured(reason));
            tray.ShowSettings();
            await settingsSaved.WaitAsync(quit.Token).ConfigureAwait(false);
            continue;
        }

        log.Info($"Running with {settings}");
        using var restart = CancellationTokenSource.CreateLinkedTokenSource(quit.Token);
        var waitForSave = settingsSaved.WaitAsync(restart.Token).ContinueWith(
            t =>
            {
                if (t.Status == TaskStatus.RanToCompletion)
                {
                    log.Info("Settings changed; restarting the agent");
                    restart.Cancel();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        using var audio = new CoreAudio(log);
        using var system = new SystemControl(audio, log, settings.LaunchBoxDir, settings.InputWatchDir);
        using var health = new HealthProbe(log, settings.NasShare, settings.InputWatchDir);
        var agent = new AgentService(settings, audio, system, health, log, version);
        await using (agent.ConfigureAwait(false))
        {
            agent.ConnectionChanged += (_, status) => tray.SetStatus(status);
            await agent.RunAsync(restart.Token).ConfigureAwait(false);
        }

        await restart.CancelAsync().ConfigureAwait(false);
        await waitForSave.ConfigureAwait(false);
    }

    log.Info("Stopped");
    return 0;
}
catch (OperationCanceledException)
{
    log.Info("Stopped");
    return 0;
}
catch (Exception ex)
{
    log.Fail("Fatal", ex);
    return 1;
}
