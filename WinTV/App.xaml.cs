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
        int pid = Environment.ProcessId;
        var cmd = $"/c ping 127.0.0.1 -n 2 > nul & rmdir /s /q \"{appDir}\"";
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = cmd,
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }
}
