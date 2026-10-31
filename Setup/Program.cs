using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace WinTV.Setup;

internal static class Program
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "1.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        bool silent = false, restart = false;
        string installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "WinTV");
        int waitPid = 0;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--silent", StringComparison.OrdinalIgnoreCase) || a.Equals("/S", StringComparison.OrdinalIgnoreCase))
                silent = true;
            else if (a.Equals("--restart", StringComparison.OrdinalIgnoreCase))
                restart = true;
            else if (a.Equals("--dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                installDir = args[++i];
            else if (a.Equals("--wait-pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                int.TryParse(args[++i], out waitPid);
        }

        if (silent)
        {
            try
            {
                InstallFiles(installDir, waitPid, createDesktop: false, createStartMenu: false, isSilentUpdate: true, null);
                if (restart)
                {
                    var exe = Path.Combine(installDir, "WinTV.exe");
                    if (File.Exists(exe))
                        Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = installDir, UseShellExecute = true });
                }
                return 0;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Ошибка обновления: " + ex.Message, "WinTV",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        var app = new Application();
        app.Run(BuildWindow(installDir));
        return 0;
    }

    static Window BuildWindow(string defaultDir)
    {
        var bg = Brush("#0F1115");
        var panel = Brush("#15181E");
        var inputBg = Brush("#1C2028");
        var borderCol = Brush("#2E3440");
        var accent = Brush("#4C8DFF");
        var text = Brush("#E6E6E6");
        var muted = Brush("#8A93A3");

        var win = new Window
        {
            Title = $"Установка WinTV {Version}",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = panel,
            Foreground = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
        };
        win.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(win).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        };

        var root = new StackPanel { Margin = new Thickness(24) };

        root.Children.Add(new TextBlock
        {
            Text = $"Установка WinTV {Version}",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
        });
        root.Children.Add(new TextBlock
        {
            Text = "IPTV-плеер для Windows (не требует установленного .NET или VLC)",
            Foreground = muted,
            Margin = new Thickness(0, 4, 0, 18),
        });

        root.Children.Add(new TextBlock
        {
            Text = "Папка установки",
            Margin = new Thickness(0, 0, 0, 6),
        });

        var dirGrid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition());
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dirBox = new TextBox
        {
            Text = defaultDir,
            Background = inputBg,
            Foreground = text,
            BorderBrush = borderCol,
            CaretBrush = text,
            Padding = new Thickness(8, 6, 8, 6),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var browseBtn = MakeButton("Обзор…", Brush("#1E232C"), text);
        Grid.SetColumn(browseBtn, 2);
        dirGrid.Children.Add(dirBox);
        dirGrid.Children.Add(browseBtn);
        root.Children.Add(dirGrid);

        browseBtn.Click += (_, _) =>
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Выберите папку для установки WinTV",
                SelectedPath = dirBox.Text,
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var p = dlg.SelectedPath;
                if (!p.EndsWith("WinTV", StringComparison.OrdinalIgnoreCase))
                    p = Path.Combine(p, "WinTV");
                dirBox.Text = p;
            }
        };

        var cbDesktop = new CheckBox
        {
            Content = "Создать ярлык на Рабочем столе",
            Foreground = text,
            IsChecked = true,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var cbStartMenu = new CheckBox
        {
            Content = "Добавить ярлык в меню «Пуск»",
            Foreground = text,
            IsChecked = true,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var cbLaunch = new CheckBox
        {
            Content = "Запустить WinTV после установки",
            Foreground = text,
            IsChecked = true,
            Margin = new Thickness(0, 0, 0, 18),
        };
        root.Children.Add(cbDesktop);
        root.Children.Add(cbStartMenu);
        root.Children.Add(cbLaunch);

        var statusText = new TextBlock
        {
            Foreground = muted,
            Margin = new Thickness(0, 0, 0, 6),
            Visibility = Visibility.Collapsed,
        };
        var progressBar = new ProgressBar
        {
            Height = 6,
            Minimum = 0,
            Maximum = 100,
            Foreground = accent,
            Background = bg,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 18),
            Visibility = Visibility.Collapsed,
        };
        root.Children.Add(statusText);
        root.Children.Add(progressBar);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var installBtn = MakeButton("Установить", accent, Colors.White);
        installBtn.Margin = new Thickness(0, 0, 8, 0);
        var cancelBtn = MakeButton("Отмена", Brush("#1E232C"), text);
        btnRow.Children.Add(installBtn);
        btnRow.Children.Add(cancelBtn);
        root.Children.Add(btnRow);

        bool installing = false;
        win.Closing += (_, e) => { if (installing) e.Cancel = true; };
        cancelBtn.Click += (_, _) => win.Close();

        installBtn.Click += async (_, _) =>
        {
            string targetDir;
            try { targetDir = NormalizeInstallDir(dirBox.Text.Trim()); }
            catch
            {
                System.Windows.MessageBox.Show(win, "Укажите корректную папку установки.", "Установка WinTV",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrEmpty(targetDir)) return;
            dirBox.Text = targetDir;

            installing = true;
            cancelBtn.IsEnabled = false;
            installBtn.IsEnabled = browseBtn.IsEnabled = dirBox.IsEnabled =
                cbDesktop.IsEnabled = cbStartMenu.IsEnabled = cbLaunch.IsEnabled = false;
            statusText.Visibility = progressBar.Visibility = Visibility.Visible;
            statusText.Text = "Подготовка к установке…";

            bool desk = cbDesktop.IsChecked == true;
            bool start = cbStartMenu.IsChecked == true;
            bool launch = cbLaunch.IsChecked == true;

            try
            {
                await Task.Run(() => InstallFiles(targetDir, 0, desk, start, isSilentUpdate: false, pct =>
                {
                    win.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        progressBar.Value = pct;
                        statusText.Text = $"Распаковка файлов… {pct}%";
                    }));
                }));

                statusText.Text = "Установка завершена!";
                progressBar.Value = 100;

                if (launch)
                {
                    var exe = Path.Combine(targetDir, "WinTV.exe");
                    if (File.Exists(exe))
                        Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = targetDir, UseShellExecute = true });
                }
                installing = false;
                win.Close();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(win, "Ошибка установки: " + ex.Message, "Установка WinTV",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                installing = false;
                cancelBtn.IsEnabled = true;
                installBtn.IsEnabled = browseBtn.IsEnabled = dirBox.IsEnabled =
                    cbDesktop.IsEnabled = cbStartMenu.IsEnabled = cbLaunch.IsEnabled = true;
            }
        };

        win.Content = root;
        return win;
    }

    static Button MakeButton(string caption, Brush bg, object fg)
    {
        var fgBrush = fg is Color c ? new SolidColorBrush(c) : (Brush)fg;
        return new Button
        {
            Content = caption,
            Background = bg,
            Foreground = fgBrush,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(16, 8, 16, 8),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
    }

    static SolidColorBrush Brush(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    /// <summary>
    /// Корень диска или непустая папка без WinTV превращается в подпапку WinTV:
    /// при удалении папка установки стирается целиком, и чужие файлы не должны в неё попасть.
    /// </summary>
    static string NormalizeInstallDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "";
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dir));
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        bool isRoot = string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase);
        bool foreign = Directory.Exists(full)
                       && Directory.EnumerateFileSystemEntries(full).Any()
                       && !File.Exists(Path.Combine(full, "WinTV.exe"))
                       && !File.Exists(Path.Combine(full, ".installed"));
        return isRoot || foreign ? Path.Combine(full, "WinTV") : trimmed;
    }

    static void InstallFiles(
        string targetDir,
        int waitPid,
        bool createDesktop,
        bool createStartMenu,
        bool isSilentUpdate,
        Action<int>? onProgress)
    {
        if (waitPid > 0)
        {
            for (int i = 0; i < 50; i++)
            {
                try { Process.GetProcessById(waitPid); Thread.Sleep(200); }
                catch { break; }
            }
        }

        // Закрываем WinTV, запущенный из папки установки, чтобы он не блокировал перезапись файлов.
        // Портативные копии в других папках не трогаем.
        var targetExe = Path.GetFullPath(Path.Combine(targetDir, "WinTV.exe"));
        foreach (var proc in Process.GetProcessesByName("WinTV"))
        {
            try
            {
                var path = proc.MainModule?.FileName;
                if (path == null || !string.Equals(Path.GetFullPath(path), targetExe, StringComparison.OrdinalIgnoreCase))
                    continue;
                proc.Kill();
                proc.WaitForExit(3000);
            }
            catch { }
            finally { proc.Dispose(); }
        }

        Directory.CreateDirectory(targetDir);

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
            ?? throw new InvalidOperationException("В установщике отсутствует встроенный архив payload.zip.");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        long totalBytes = PayloadInstaller.Apply(archive, targetDir, onProgress);

        var markerPath = Path.Combine(targetDir, ".installed");
        if (!isSilentUpdate || File.Exists(markerPath))
        {
            File.WriteAllText(markerPath, Version);
            RegisterUninstall(targetDir, totalBytes);
        }

        var exePath = Path.Combine(targetDir, "WinTV.exe");
        if (createDesktop)
        {
            var desk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "WinTV.lnk");
            CreateShortcut(desk, exePath, targetDir);
        }
        if (createStartMenu)
        {
            var start = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "WinTV.lnk");
            CreateShortcut(start, exePath, targetDir);
        }
    }

    static void RegisterUninstall(string targetDir, long totalBytes)
    {
        try
        {
            var exePath = Path.Combine(targetDir, "WinTV.exe");
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinTV");
            if (key == null) return;
            key.SetValue("DisplayName", "WinTV");
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "WinTV");
            key.SetValue("DisplayIcon", $"{exePath},0");
            key.SetValue("InstallLocation", targetDir);
            key.SetValue("UninstallString", $"\"{exePath}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)Math.Max(1, totalBytes / 1024), RegistryValueKind.DWord);
        }
        catch { }
    }

    static void CreateShortcut(string shortcutPath, string targetExe, string workingDir)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic sc = shell.CreateShortcut(shortcutPath);
            sc.TargetPath = targetExe;
            sc.WorkingDirectory = workingDir;
            sc.IconLocation = $"{targetExe},0";
            sc.Description = "WinTV — IPTV-плеер";
            sc.Save();
        }
        catch { }
    }
}
