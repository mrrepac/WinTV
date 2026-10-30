using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinTV;

/// <summary>
/// Логотипы каналов с кэшем на диске (%LocalAppData%\WinTV\logos, имя файла — хеш URL).
/// Есть файл — картинка сразу из него; нет или устарел (старше 7 дней) — загрузка в фоне.
/// </summary>
public static class LogoCache
{
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    static readonly string Dir = Path.Combine(Settings.Dir, "logos");
    static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    { Timeout = TimeSpan.FromSeconds(30) };
    static readonly SemaphoreSlim Downloads = new(6);

    static readonly Dictionary<string, ImageSource?> Images = new();
    static readonly Dictionary<string, List<Action<ImageSource>>> Waiting = new();

    /// <summary>
    /// Возвращает логотип, если он уже есть (в памяти или на диске). Если его нужно загрузить или обновить,
    /// <paramref name="onReady"/> будет вызван в UI-потоке с новой картинкой. Вызывать из UI-потока.
    /// </summary>
    public static ImageSource? Get(string url, Action<ImageSource> onReady)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (Images.TryGetValue(url, out var img))
        {
            if (Waiting.TryGetValue(url, out var w)) w.Add(onReady);
            return img;
        }

        var file = FileFor(url);
        bool fresh = false;
        if (File.Exists(file))
        {
            try { img = Decode(File.ReadAllBytes(file)); } catch { img = null; }
            fresh = img != null && DateTime.Now - File.GetLastWriteTime(file) < MaxAge;
        }
        Images[url] = img;
        if (!fresh && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            Waiting[url] = new List<Action<ImageSource>> { onReady };
            _ = DownloadAsync(url, file);
        }
        return img;
    }

    public static void SetUserAgent(string ua)
    {
        Http.DefaultRequestHeaders.UserAgent.Clear();
        Http.DefaultRequestHeaders.UserAgent.TryParseAdd(ua);
    }

    static async Task DownloadAsync(string url, string file)
    {
        ImageSource? img = null;
        await Downloads.WaitAsync();
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            img = await Task.Run(() => Decode(bytes));
            Directory.CreateDirectory(Dir);
            await File.WriteAllBytesAsync(file, bytes);
        }
        catch
        {
            // Нет сети или битая картинка — остаётся то, что было (старый файл или пусто).
            if (File.Exists(file)) File.SetLastWriteTime(file, DateTime.Now - MaxAge + TimeSpan.FromHours(1));
        }
        finally { Downloads.Release(); }

        var callbacks = Waiting.Remove(url, out var list) ? list : new();
        if (img == null) return;
        Images[url] = img;
        foreach (var cb in callbacks) cb(img);
    }

    static string FileFor(string url) =>
        Path.Combine(Dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant());

    static BitmapImage Decode(byte[] bytes)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.StreamSource = new MemoryStream(bytes);
        img.DecodePixelHeight = 56;
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        img.EndInit();
        img.Freeze();
        return img;
    }
}
