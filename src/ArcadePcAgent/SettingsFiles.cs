using System.Security.Cryptography;
using System.Text;
using ArcadePcAgent.Core;

namespace ArcadePcAgent;

/// <summary>
/// The agent's files in %LOCALAPPDATA%\ArcadePcAgent: agent.json (settings) and
/// agent.password (the MQTT password, DPAPI-protected for this Windows user,
/// base64). Both are written atomically (temp file, then replace).
/// </summary>
internal sealed class SettingsFiles(string dataDir)
{
    public string DataDir { get; } = dataDir;

    public string SettingsPath => Path.Combine(DataDir, "agent.json");

    public string PasswordPath => Path.Combine(DataDir, "agent.password");

    public string LogPath => Path.Combine(DataDir, "agent.log");

    public bool HasPassword => File.Exists(PasswordPath);

    /// <summary>Settings plus password, ready to run. Throws when agent.json is missing or invalid.</summary>
    public AgentSettings Load() => LoadWithoutPassword() with { Password = LoadPassword() };

    /// <summary>The settings file alone, for the Settings window. Throws when missing or invalid.</summary>
    public AgentSettings LoadWithoutPassword() => AgentSettings.Parse(File.ReadAllText(SettingsPath));

    /// <summary>Saves the settings and, when <paramref name="newPassword"/> is not null, the password.</summary>
    public void Save(AgentSettings settings, string? newPassword)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(DataDir);
        WriteAtomically(SettingsPath, settings.ToJson());
        if (newPassword is not null)
        {
            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(newPassword), optionalEntropy: null, DataProtectionScope.CurrentUser);
            WriteAtomically(PasswordPath, Convert.ToBase64String(protectedBytes));
        }
    }

    /// <summary>The stored password; empty when none is stored (brokers without authentication).</summary>
    private string LoadPassword() =>
        File.Exists(PasswordPath)
            ? Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(File.ReadAllText(PasswordPath).Trim()), optionalEntropy: null, DataProtectionScope.CurrentUser))
            : string.Empty;

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }
}
