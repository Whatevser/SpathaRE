using System.Diagnostics;
using System.IO;

public static class Logger
{
    public static readonly string AppPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MouseHud");

    private static readonly string LogFile = Path.Combine(AppPath, "log.txt");
    private static readonly object _lock = new();
    private static int _writes;

    /// <summary>Set false to silence disk logging (Debug output is always on).</summary>
    public static bool FileLogging { get; set; } = true;

    public static void WriteLine(string message)
    {
        Debug.WriteLine($"{DateTime.Now:HH:mm:ss.fff}: {message}");
        if (!FileLogging) return;

        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(AppPath);
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}: {message}{Environment.NewLine}");
            }
            catch { /* logging must never throw */ }

            if (++_writes % 500 == 0) Trim();
        }
    }

    private static void Trim()
    {
        try
        {
            var lines = File.ReadAllLines(LogFile);
            if (lines.Length > 4000)
                File.WriteAllLines(LogFile, lines.Skip(lines.Length - 2000));
        }
        catch { }
    }
}
