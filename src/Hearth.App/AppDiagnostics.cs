using System.IO;

namespace Hearth.App;
public static class AppDiagnostics
{
    public static string LogPath { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hearth", "Logs", "errors.log");
    private static readonly object Gate = new();
    public static void Configure(string profileRoot) => LogPath = Path.Combine(profileRoot, "Logs", "errors.log");
    public static void Log(Exception error)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                // Exception messages may contain credentials or invite URLs. Record types and stacks only.
                File.AppendAllText(LogPath, DateTimeOffset.UtcNow.ToString("O") + " " + error.GetType().FullName + Environment.NewLine + error.StackTrace + Environment.NewLine);
            }
        }
        catch { /* Logging must never replace the original failure. */ }
    }
}
