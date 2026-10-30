using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace WinTV;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show($"Произошла непредвиденная ошибка:\n\n{args.Exception}", "WinTV — ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
            // Если главное окно уже открыто, не роняем приложение из-за единичной ошибки.
            args.Handled = MainWindow is { IsLoaded: true };
        };
        if (e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            RunUninstall();
            Shutdown();
            return;
        }
        new MainWindow().Show();
    }

    static void RunUninstall()
    {
        if (MessageBox.Show("Удалить приложение WinTV с компьютера?", "Удаление WinTV",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "WinTV.lnk");
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "WinTV.lnk");
            if (File.Exists(desktop)) File.Delete(desktop);
            if (File.Exists(startMenu)) File.Delete(startMenu);
        }
        catch { }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinTV", false);
        }
        catch { }

        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!IsSafeToDelete(appDir))
        {
            MessageBox.Show($"Ярлыки и запись в списке программ удалены.\nПапку «{appDir}» удалите вручную.",
                "Удаление WinTV", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var cmd = $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{appDir}\"";
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = cmd,
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    /// <summary>
    /// Папку удаляем целиком, только если это действительно папка установки WinTV
    /// (есть метка установщика и WinTV.exe), а не корень диска или системная/пользовательская папка.
    /// </summary>
    static bool IsSafeToDelete(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
            if (!File.Exists(Path.Combine(full, ".installed")) || !File.Exists(Path.Combine(full, "WinTV.exe")))
                return false;
            var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(root) || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                return false;
            var protectedDirs = new[]
            {
                Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows,
                Environment.SpecialFolder.Programs, Environment.SpecialFolder.StartMenu,
            }
            .Select(Environment.GetFolderPath)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"))
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p.TrimEnd(Path.DirectorySeparatorChar));
            return !protectedDirs.Any(p => string.Equals(full, p, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
