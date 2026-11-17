using System.IO;
using System.Text.RegularExpressions;

namespace WinTV;

public static class AppLog
{
    static readonly object Gate = new();
    public static string DirectoryPath => Path.Combine(Settings.Dir, "logs");
    public static void Error(string context, Exception ex) => Write(context + ": " + ex);
    public static void Write(string message)
    {
        try
        {
            // Stream URLs may contain subscription credentials; never persist them.
            message = Regex.Replace(message, @"(?i)\b[a-z][a-z0-9+.-]*://[^\s<>""']+", "[URL]");
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var file = Path.Combine(DirectoryPath, "wintv.log");
                if (File.Exists(file) && new FileInfo(file).Length > 2 * 1024 * 1024)
                    File.Move(file, file + ".1", true);
                File.AppendAllText(file, $"{DateTimeOffset.Now:O} [T{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
            }
        }
        catch { /* Logging must not crash the application. */ }
    }
}
