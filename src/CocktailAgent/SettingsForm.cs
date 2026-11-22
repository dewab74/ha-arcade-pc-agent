using CocktailAgent.Core;

namespace CocktailAgent;

/// <summary>
/// Settings window, opened from the tray icon (and on first run, when there is no
/// agent.json yet). Saving writes agent.json and, if one was typed, the
/// DPAPI-protected password, then raises <see cref="Saved"/> so the agent
/// restarts with the new settings.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly SettingsFiles _files;
    private readonly IAgentLog _log;
    private readonly TableLayoutPanel _grid;
    private readonly TextBox _host = new();
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Width = 90 };
    private readonly CheckBox _useTls = new() { Text = "Use TLS", AutoSize = true };
    private readonly TextBox _username = new();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly TextBox _caFile = new();
    private readonly TextBox _nodeId = new();
    private readonly TextBox _deviceName = new();
    private readonly TextBox _area = new();
    private readonly TextBox _muteEntityId = new();
    private readonly TextBox _volumeEntityId = new();
    private readonly TextBox _launchBoxDir = new();
    private readonly TextBox _inputWatchDir = new();
    private readonly TextBox _nasShare = new();

    public SettingsForm(SettingsFiles files, IAgentLog log)
    {
        _files = files;
        _log = log;
        Text = "Cocktail agent settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        AutoScaleMode = AutoScaleMode.Dpi;
        Padding = new Padding(12);
        using (var stream = typeof(SettingsForm).Assembly.GetManifestResourceStream("CocktailAgent.Icons.agent.ico"))
        {
            if (stream is not null)
            {
                Icon = new Icon(stream);
            }
        }

        _grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Section("MQTT broker");
        Row("Host", _host, "Broker host name, e.g. homeassistant.local");
        Row("Port", _port, "8883 for TLS, 1883 without");
        Row(string.Empty, _useTls, null);
        Row("Username", _username, null);
        Row("Password", _password, null);
        Row("CA certificate", _caFile, "PEM file of a private CA the broker must chain to; empty: the Windows trust store",
            BrowseButton(() => PickFile(_caFile, "Certificates (*.pem;*.crt)|*.pem;*.crt|All files|*.*")));
        Section("Home Assistant");
        Row("Node id", _nodeId, "Topic segment, device id and unique_id prefix (a-z, 0-9, _)");
        Row("Device name", _deviceName, null);
        Row("Area", _area, "Suggested area for the device (optional)");
        Row("Mute entity id", _muteEntityId, "Optional, e.g. switch.arcade_muted; default switch.<node id>_mute");
        Row("Volume entity id", _volumeEntityId, "Optional; default number.<node id>_volume");
        Section("This PC");
        Row("LaunchBox folder", _launchBoxDir, null, BrowseButton(() => PickFolder(_launchBoxDir)));
        Row("Input watcher folder", _inputWatchDir, "Optional: folder of an external input watcher (see the docs); empty uses Windows' idle timer",
            BrowseButton(() => PickFolder(_inputWatchDir)));
        Row("NAS share", _nasShare, @"Optional UNC path to report on, e.g. \\nas\games");

        var save = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => SaveSettings();
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        _grid.Controls.Add(buttons, 0, _grid.RowCount);
        _grid.SetColumnSpan(buttons, 3);
        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(_grid);
        LoadSettings();
    }

    /// <summary>Raised on the UI thread after the files were written.</summary>
    public event EventHandler? Saved;

    private void LoadSettings()
    {
        AgentSettings current;
        try
        {
            current = _files.LoadWithoutPassword();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            current = new AgentSettings { Host = string.Empty, Username = string.Empty };
            if (File.Exists(_files.SettingsPath))
            {
                MessageBox.Show(this, $"The current agent.json could not be read:\n{ex.Message}\n\nSaving replaces it.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        _host.Text = current.Host;
        _port.Value = current.Port;
        _useTls.Checked = current.UseTls;
        _username.Text = current.Username;
        _password.PlaceholderText = _files.HasPassword ? "(unchanged)" : "(none)";
        _caFile.Text = current.CaFile ?? string.Empty;
        _nodeId.Text = current.NodeId;
        _deviceName.Text = current.DeviceName;
        _area.Text = current.SuggestedArea ?? string.Empty;
        _muteEntityId.Text = current.MuteEntityId ?? string.Empty;
        _volumeEntityId.Text = current.VolumeEntityId ?? string.Empty;
        _launchBoxDir.Text = current.LaunchBoxDir;
        _inputWatchDir.Text = current.InputWatchDir ?? string.Empty;
        _nasShare.Text = current.NasShare ?? string.Empty;
    }

    private void SaveSettings()
    {
        var caFile = Optional(_caFile);
        if (caFile is not null && !File.Exists(caFile))
        {
            Error($"CA certificate file not found:\n{caFile}");
            return;
        }

        AgentSettings settings;
        try
        {
            settings = new AgentSettings
            {
                Host = _host.Text.Trim(),
                Port = (int)_port.Value,
                UseTls = _useTls.Checked,
                Username = _username.Text.Trim(),
                CaFile = caFile,
                NodeId = _nodeId.Text.Trim(),
                DeviceName = string.IsNullOrWhiteSpace(_deviceName.Text) ? "Cocktail PC" : _deviceName.Text.Trim(),
                SuggestedArea = Optional(_area),
                MuteEntityId = Optional(_muteEntityId),
                VolumeEntityId = Optional(_volumeEntityId),
                LaunchBoxDir = _launchBoxDir.Text.Trim(),
                InputWatchDir = Optional(_inputWatchDir),
                NasShare = Optional(_nasShare),
            };
            settings.Validate();
        }
        catch (InvalidOperationException ex)
        {
            Error(ex.Message);
            return;
        }

        try
        {
            _files.Save(settings, _password.Text.Length > 0 ? _password.Text : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            Error($"Could not save the settings:\n{ex.Message}");
            return;
        }

        _log.Info($"Settings saved from the tray: {settings}");
        Saved?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private static string? Optional(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();

    private void Error(string message) => MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);

    private void Section(string title)
    {
        var label = new Label { Text = title, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, _grid.RowCount == 0 ? 0 : 12, 0, 4) };
        _grid.Controls.Add(label, 0, _grid.RowCount);
        _grid.SetColumnSpan(label, 3);
        _grid.RowCount++;
    }

    private void Row(string label, Control input, string? hint, Button? browse = null)
    {
        var row = _grid.RowCount;
        _grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, row);
        if (input is TextBox)
        {
            input.Dock = DockStyle.Fill;
        }

        _grid.Controls.Add(input, 1, row);
        if (browse is not null)
        {
            _grid.Controls.Add(browse, 2, row);
        }

        _grid.RowCount++;
        if (hint is not null)
        {
            _grid.Controls.Add(new Label { Text = hint, AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 4) }, 1, _grid.RowCount);
            _grid.RowCount++;
        }
    }

    private static Button BrowseButton(Action onClick)
    {
        var button = new Button { Text = "Browse…", AutoSize = true };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void PickFile(TextBox target, string filter)
    {
        using var dialog = new OpenFileDialog { Filter = filter, FileName = target.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            target.Text = dialog.FileName;
        }
    }

    private void PickFolder(TextBox target)
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = target.Text, UseDescriptionForTitle = true, Description = "Choose a folder" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            target.Text = dialog.SelectedPath;
        }
    }
}
