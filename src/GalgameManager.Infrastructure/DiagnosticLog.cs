using System.ComponentModel;
using Microsoft.Data.Sqlite;

namespace GalgameManager.Infrastructure;

public sealed class DiagnosticLog(string directory)
{
    private readonly object gate = new();

    public void Write(string operation, Exception error)
    {
        var code = error switch
        {
            SqliteException sqlite => sqlite.SqliteExtendedErrorCode,
            Win32Exception windows => windows.NativeErrorCode,
            _ => error.HResult
        };

        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "diagnostic.log");
                if (File.Exists(path) && new FileInfo(path).Length >= 256 * 1024)
                    File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {operation} {error.GetType().Name} 0x{code:X8}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostics are best-effort when the local data directory cannot be written.
        }
    }
}
