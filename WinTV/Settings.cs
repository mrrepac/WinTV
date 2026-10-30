using System.IO;
using System.Text.Json;

namespace WinTV;

public sealed class Settings
{
    public string PlaylistUrl { get; set; } = "";
    /// <summary>Пусто — брать url-tvg из плейлиста.</summary>
    public string EpgUrl { get; set; } = "";
    /// <summary>auto | flussonic | append</summary>
    public string CatchupMode { get; set; } = "auto";
    /// <summary>Формат архива, который сработал в режиме auto.</summary>
    public string? CatchupDetected { get; set; }
    public string UserAgent { get; set; } = "";
    public int NetworkCaching { get; set; } = 1500;
    public HashSet<string> Favorites { get; set; } = new();
    /// <summary>Не запоминать последний канал и группу.</summary>
    public bool NoHistory { get; set; }
    public string? LastChannel { get; set; }
    public string? LastGroup { get; set; }
    public int Volume { get; set; } = 80;

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTV");

    static string FilePath => Path.Combine(Dir, "settings.json");

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { }
    }
}
