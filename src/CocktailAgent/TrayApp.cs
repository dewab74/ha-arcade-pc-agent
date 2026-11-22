using System.Diagnostics;
using System.Reflection;
using CocktailAgent.Core;

namespace CocktailAgent;

/// <summary>
/// The notification-area (tray) icon: shows the broker connection in its icon,
/// tooltip and menu, and offers Settings, Open log, About and Quit. Runs its own
/// STA thread with a WinForms message loop, so the agent itself stays free of UI.
/// </summary>
internal sealed class TrayApp : IDisposable
{
    private const string AppName = "Cocktail agent";

    private readonly string _version;
    private readonly string? _repositoryUrl;
    private readonly SettingsFiles _files;
    private readonly IAgentLog _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private SynchronizationContext? _ui;
    private NotifyIcon? _icon;
    private ToolStripMenuItem? _statusItem;
    private SettingsForm? _settingsForm;
    private Icon[] _icons = [];
    private ConnectionStatus _status = ConnectionStatus.NotConfigured("starting");

    public TrayApp(string version, SettingsFiles files, IAgentLog log)
    {
        _version = version;
        _files = files;
        _log = log;
        _repositoryUrl = typeof(TrayApp).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value;
        _thread = new Thread(Run) { IsBackground = true, Name = "tray" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>The user saved new settings; the agent should restart with them.</summary>
    public event EventHandler? SettingsSaved;

    /// <summary>The user chose Quit and confirmed.</summary>
    public event EventHandler? QuitRequested;

    public void Start()
    {
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>Thread-safe: updates the icon, tooltip and status line.</summary>
    public void SetStatus(ConnectionStatus status)
    {
        _status = status;
        _ui?.Post(_ => ApplyStatus(), null);
    }

    /// <summary>Thread-safe: opens the Settings window (or brings it to the front).</summary>
    public void ShowSettings() => _ui?.Post(_ => OpenSettings(), null);

    public void Dispose()
    {
        if (_ui is not null && _thread.IsAlive)
        {
            _ui.Send(
                _ =>
                {
                    _settingsForm?.Close();
                    if (_icon is not null)
                    {
                        _icon.Visible = false;
                        _icon.Dispose();
                    }

                    foreach (var icon in _icons)
                    {
                        icon.Dispose();
                    }

                    Application.ExitThread();
                },
                null);
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        _ready.Dispose();
    }

    private void Run()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        _ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_ui);

        _icons = [LoadIcon("waiting"), LoadIcon("connected"), LoadIcon("disconnected")];
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"{AppName} {ShortVersion}") { Enabled = false, Font = new Font(menu.Font, FontStyle.Bold) });
        _statusItem = new ToolStripMenuItem(string.Empty) { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        menu.Items.Add("Open log", null, (_, _) => OpenLog());
        menu.Items.Add("About…", null, (_, _) => ShowAbout());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ConfirmQuit());

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => OpenSettings();
        ApplyStatus();
        _ready.Set();
        Application.Run();
    }

    private string ShortVersion => _version.Split('+')[0];

    private void ApplyStatus()
    {
        if (_icon is null || _statusItem is null)
        {
            return;
        }

        var status = _status;
        _icon.Icon = _icons[status.State switch
        {
            ConnectionState.Connected => 1,
            ConnectionState.Disconnected => 2,
            _ => 0,
        }];
        _statusItem.Text = status.Summary;
        var tip = $"{AppName}: {status.Summary}";
        _icon.Text = tip.Length > 127 ? tip[..126] + "…" : tip;
    }

    private void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_files, _log);
        _settingsForm.Saved += (_, _) => SettingsSaved?.Invoke(this, EventArgs.Empty);
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private void OpenLog()
    {
        if (File.Exists(_files.LogPath))
        {
            using var _ = Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_files.LogPath}\"") { UseShellExecute = true });
        }
    }

    private void ShowAbout()
    {
        var page = new TaskDialogPage
        {
            Caption = $"About {AppName}",
            Heading = $"{AppName} {ShortVersion}",
            Text = "Lets Home Assistant control a LaunchBox / BigBox Windows PC over MQTT: "
                + "volume, mute, the screen, closing emulators and the front end, restart and shut down, "
                + "plus health sensors. Entities appear through MQTT discovery.",
            Footnote = new TaskDialogFootnote($"MIT License. Version {_version}"),
            Icon = TaskDialogIcon.Information,
            Buttons = { TaskDialogButton.OK },
        };
        if (!string.IsNullOrEmpty(_repositoryUrl))
        {
            page.EnableLinks = true;
            page.Text += $"\n\n<a href=\"{_repositoryUrl}\">{_repositoryUrl}</a>";
            page.LinkClicked += (_, e) =>
            {
                using var browser = Process.Start(new ProcessStartInfo(e.LinkHref) { UseShellExecute = true });
            };
        }

        TaskDialog.ShowDialog(page);
    }

    private void ConfirmQuit()
    {
        var quit = new TaskDialogButton("Quit");
        var page = new TaskDialogPage
        {
            Caption = AppName,
            Heading = "Quit the agent?",
            Text = "Home Assistant loses control of this PC (volume, mute, screen and power buttons) "
                + "until the agent starts again, normally at the next sign-in.",
            Icon = TaskDialogIcon.Warning,
            Buttons = { quit, TaskDialogButton.Cancel },
            DefaultButton = TaskDialogButton.Cancel,
        };
        if (TaskDialog.ShowDialog(page) == quit)
        {
            _log.Info("Quit from the tray menu");
            QuitRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private static Icon LoadIcon(string name)
    {
        using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream($"CocktailAgent.Icons.{name}.ico")
            ?? throw new InvalidOperationException($"Embedded icon {name}.ico is missing");
        return new Icon(stream, SystemInformation.SmallIconSize);
    }
}
