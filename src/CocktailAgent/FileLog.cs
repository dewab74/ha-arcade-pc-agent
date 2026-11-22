using CocktailAgent.Core;

namespace CocktailAgent;

/// <summary>Plain text log, rotated at 1 MB to one .1 file.</summary>
internal sealed class FileLog(string path) : IAgentLog
{
    private const long MaxBytes = 1024 * 1024;
    private readonly Lock _gate = new();

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Fail(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception}");

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            try
            {
                var file = new FileInfo(path);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }

                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never take the agent down.
            }
        }
    }
}
