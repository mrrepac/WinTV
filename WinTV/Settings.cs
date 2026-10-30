using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinTV;

/// <summary>Плейлист и всё, что запоминается отдельно для него.</summary>
public sealed class Playlist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Пусто — брать url-tvg из плейлиста.</summary>
    public string EpgUrl { get; set; } = "";
    public HashSet<string> Favorites { get; set; } = new();
    /// <summary>Группы, которые не показываются в списках, поиске и при переключении каналов.</summary>
    public HashSet<string> HiddenGroups { get; set; } = new();
    public string? LastChannel { get; set; }
    public string? LastGroup { get; set; }

    /// <summary>Сохранённая копия плейлиста на случай отсутствия сети.</summary>
    [JsonIgnore] public string CacheFile => Path.Combine(Settings.Dir, $"playlist_{Id}.m3u");
}

public sealed class Settings
{
    public List<Playlist> Playlists { get; set; } = new();
    public string? ActivePlaylist { get; set; }
    /// <summary>auto | flussonic | append</summary>
    public string CatchupMode { get; set; } = "auto";
    /// <summary>Формат архива, который сработал в режиме auto.</summary>
    public string? CatchupDetected { get; set; }
    public string UserAgent { get; set; } = "";
    public int NetworkCaching { get; set; } = 1500;
    /// <summary>Соль и хеш PIN на показ скрытых групп (см. Pin). Пусто — без PIN.</summary>
    public string? HiddenPinHash { get; set; }
    /// <summary>Не запоминать последний канал и группу.</summary>
    public bool NoHistory { get; set; }
    public int Volume { get; set; } = 80;
    /// <summary>Переназначенные горячие клавиши: id действия → сочетания. Отсутствие — значения по умолчанию.</summary>
    public Dictionary<string, List<string>> Hotkeys { get; set; } = new();

    // Поля версий с одним плейлистом: только читаются и переносятся в Playlists (см. Migrate).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PlaylistUrl { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? EpgUrl { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public HashSet<string>? Favorites { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public HashSet<string>? HiddenGroups { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LastChannel { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LastGroup { get; set; }

    /// <summary>Активный плейлист (первый, если выбранный удалён). null — плейлистов нет.</summary>
    [JsonIgnore] public Playlist? Active => Playlists.FirstOrDefault(p => p.Id == ActivePlaylist) ?? Playlists.FirstOrDefault();

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTV");

    static string FilePath => Path.Combine(Dir, "settings.json");

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Settings Load()
    {
        Settings? s = null;
        try
        {
            if (File.Exists(FilePath))
                s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
        }
        catch { }
        s ??= new Settings();
        if (s.Migrate()) s.Save();
        return s;
    }

    /// <summary>Единственный плейлист старых версий становится первым в списке вместе с избранным и историей.</summary>
    bool Migrate()
    {
        if (PlaylistUrl == null && EpgUrl == null && Favorites == null && HiddenGroups == null
            && LastChannel == null && LastGroup == null) return false;
        if (Playlists.Count == 0 && !string.IsNullOrWhiteSpace(PlaylistUrl))
        {
            var p = new Playlist
            {
                Name = "Мой плейлист",
                Url = PlaylistUrl,
                EpgUrl = EpgUrl ?? "",
                Favorites = Favorites ?? new(),
                HiddenGroups = HiddenGroups ?? new(),
                LastChannel = LastChannel,
                LastGroup = LastGroup,
            };
            Playlists.Add(p);
            ActivePlaylist = p.Id;
            var oldCache = Path.Combine(Dir, "playlist.m3u");
            try { if (File.Exists(oldCache) && !File.Exists(p.CacheFile)) File.Move(oldCache, p.CacheFile); } catch { }
        }
        PlaylistUrl = EpgUrl = LastChannel = LastGroup = null;
        Favorites = HiddenGroups = null;
        return true;
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
