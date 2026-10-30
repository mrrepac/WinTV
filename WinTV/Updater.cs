using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinTV;

public sealed record ReleaseInfo(
    string Version,
    string Title,
    string Notes,
    string HtmlUrl,
    string? DownloadUrl,
    string? AssetName,
    long Size);

public sealed record UpdateCheckResult(ReleaseInfo? Available, string? LatestVersion, string? Error);

public static class Updater
{
    public const string Repo = "mrrepac/WinTV";

    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "1.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    public static bool IsInstalledBuild =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, ".installed"));

    public static async Task<UpdateCheckResult> CheckAsync(HttpClient http)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            req.Headers.TryAddWithoutValidation("User-Agent", $"WinTV/{CurrentVersion}");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var resp = await http.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return new UpdateCheckResult(null, null, null);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var cleanVer = CleanVersion(tag);
            var title = root.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString())
                ? n.GetString()!
                : $"WinTV {cleanVer}";
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : $"https://github.com/{Repo}/releases";

            string? setupUrl = null, setupName = null, zipUrl = null, zipName = null;
            long setupSize = 0, zipSize = 0;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var aName = a.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                    var aUrl = a.TryGetProperty("browser_download_url", out var au) ? au.GetString() ?? "" : "";
                    var aSize = a.TryGetProperty("size", out var asz) ? asz.GetInt64() : 0;
                    if (string.IsNullOrEmpty(aUrl)) continue;

                    if (aName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        setupUrl = aUrl; setupName = aName; setupSize = aSize;
                    }
                    else if (aName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        zipUrl = aUrl; zipName = aName; zipSize = aSize;
                    }
                }
            }

            // Для установленной версии предпочитаем установщик .exe, для портативной — .zip (с откатом на любой доступный).
            bool pickSetup = IsInstalledBuild ? setupUrl != null : zipUrl == null && setupUrl != null;
            var dlUrl = pickSetup ? setupUrl : zipUrl ?? setupUrl;
            var dlName = pickSetup ? setupName : zipName ?? setupName;
            var dlSize = pickSetup ? setupSize : (zipUrl != null ? zipSize : setupSize);

            var info = new ReleaseInfo(cleanVer, title, notes, htmlUrl, dlUrl, dlName, dlSize);
            if (IsNewer(cleanVer, CurrentVersion))
                return new UpdateCheckResult(info, cleanVer, null);

            return new UpdateCheckResult(null, cleanVer, null);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(null, null, ex.Message);
        }
    }

    static string CleanVersion(string tag)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        int dash = s.IndexOf('-');
        return dash > 0 ? s[..dash] : s;
    }

    public static bool IsNewer(string remote, string local) =>
        Version.TryParse(CleanVersion(remote), out var r)
        && Version.TryParse(CleanVersion(local), out var l)
        && r > l;

    /// <summary>Окно подтверждения и скачивания обновления.</summary>
    public static void ShowUpdateDialog(Window owner, HttpClient http, ReleaseInfo rel)
    {
        var win = new Window
        {
            Title = "Обновление WinTV",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            Background = (Brush)Application.Current.Resources["Panel"],
            Foreground = (Brush)Application.Current.Resources["Text"],
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
        };
        DarkTitleBar.Apply(win);

        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock
        {
            Text = $"Доступна новая версия {rel.Version}",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
        });
        root.Children.Add(new TextBlock
        {
            Text = $"Установлена версия {CurrentVersion}",
            Foreground = (Brush)Application.Current.Resources["Muted"],
            Margin = new Thickness(0, 4, 0, 12),
        });

        if (!string.IsNullOrWhiteSpace(rel.Notes))
        {
            var notesBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x20, 0x28)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x34, 0x40)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 14),
                MaxHeight = 200,
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBlock
                    {
                        Text = rel.Notes.Trim(),
                        TextWrapping = TextWrapping.Wrap,
                    }
                }
            };
            root.Children.Add(notesBorder);
        }

        var status = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["Muted"],
            Margin = new Thickness(0, 0, 0, 6),
            Visibility = Visibility.Collapsed,
        };
        var progress = new ProgressBar
        {
            Height = 6,
            Minimum = 0,
            Maximum = 100,
            Margin = new Thickness(0, 0, 0, 14),
            Visibility = Visibility.Collapsed,
        };
        root.Children.Add(status);
        root.Children.Add(progress);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var updateBtn = new Button
        {
            Content = rel.DownloadUrl != null ? "Скачать и обновить" : "Открыть страницу релиза",
            Background = (Brush)Application.Current.Resources["Accent"],
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
        };
        var cancelBtn = new Button
        {
            Content = "Позже",
            IsCancel = true,
        };
        buttons.Children.Add(updateBtn);
        buttons.Children.Add(cancelBtn);
        root.Children.Add(buttons);

        CancellationTokenSource? cts = null;
        win.Closed += (_, _) => cts?.Cancel();

        updateBtn.Click += async (_, _) =>
        {
            if (rel.DownloadUrl == null || rel.AssetName == null)
            {
                Process.Start(new ProcessStartInfo(rel.HtmlUrl) { UseShellExecute = true });
                win.Close();
                return;
            }
            updateBtn.IsEnabled = false;
            status.Visibility = Visibility.Visible;
            progress.Visibility = Visibility.Visible;
            status.Text = "Скачивание обновления…";
            cts = new CancellationTokenSource();
            try
            {
                await DownloadAndApplyAsync(http, rel, p =>
                {
                    progress.Value = p;
                    status.Text = $"Скачивание обновления… {p}%";
                }, status, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                status.Text = "Ошибка обновления: " + ex.Message;
                updateBtn.IsEnabled = true;
            }
        };

        win.Content = root;
        win.ShowDialog();
    }

    static async Task DownloadAndApplyAsync(
        HttpClient http,
        ReleaseInfo rel,
        Action<int> onProgress,
        TextBlock status,
        CancellationToken ct)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "WinTV_Update_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);
        var filePath = Path.Combine(tempRoot, rel.AssetName!);

        using (var req = new HttpRequestMessage(HttpMethod.Get, rel.DownloadUrl))
        {
            req.Headers.TryAddWithoutValidation("User-Agent", $"WinTV/{CurrentVersion}");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? rel.Size;

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(filePath);
            var buf = new byte[81920];
            long readTotal = 0;
            int lastPct = -1;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                readTotal += n;
                if (total > 0)
                {
                    int pct = (int)Math.Clamp(readTotal * 100 / total, 0, 100);
                    if (pct != lastPct) { lastPct = pct; onProgress(pct); }
                }
            }
        }

        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        int pid = Environment.ProcessId;

        if (filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            status.Text = "Запуск установщика…";
            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                Arguments = $"--silent --dir \"{appDir}\" --restart --wait-pid {pid}",
                UseShellExecute = true,
            });
            Application.Current.Shutdown();
            return;
        }

        status.Text = "Распаковка обновления…";
        var extractDir = Path.Combine(tempRoot, "extracted");
        await Task.Run(() => ZipFile.ExtractToDirectory(filePath, extractDir, overwriteFiles: true), ct);

        // В архиве файлы могут лежать как в корне, так и в подпапке WinTV.
        var sourceDir = extractDir;
        if (!File.Exists(Path.Combine(sourceDir, "WinTV.exe")))
        {
            var sub = Directory.GetDirectories(extractDir)
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "WinTV.exe")));
            if (sub != null) sourceDir = sub;
        }

        var scriptPath = Path.Combine(tempRoot, "apply_update.ps1");
        var exePath = Path.Combine(appDir, "WinTV.exe");
        var script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            for ($i = 0; $i -lt 60; $i++) {
                if (-not (Get-Process -Id {{pid}} -ErrorAction SilentlyContinue)) { break }
                Start-Sleep -Milliseconds 300
            }
            Start-Sleep -Milliseconds 400
            Copy-Item -Path '{{sourceDir.Replace("'", "''")}}\*' -Destination '{{appDir.Replace("'", "''")}}' -Recurse -Force
            Start-Process -FilePath '{{exePath.Replace("'", "''")}}' -WorkingDirectory '{{appDir.Replace("'", "''")}}'
            Remove-Item -Path '{{tempRoot.Replace("'", "''")}}' -Recurse -Force
            """;
        await File.WriteAllTextAsync(scriptPath, script, System.Text.Encoding.UTF8, ct);

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Application.Current.Shutdown();
    }
}
