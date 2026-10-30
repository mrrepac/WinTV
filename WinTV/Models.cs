using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace WinTV;

public sealed class Programme
{
    public DateTime Start { get; init; }
    public DateTime Stop { get; init; }
    public string Title { get; init; } = "";
    public string? Desc { get; set; }
}

public sealed class Channel : INotifyPropertyChanged
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Group { get; set; } = "";
    public string TvgId { get; set; } = "";
    public string TvgName { get; set; } = "";
    public string Logo { get; set; } = "";
    public int ArchiveDays { get; set; }
    public string CatchupType { get; set; } = "";
    public string CatchupSource { get; set; } = "";

    public string Key => string.IsNullOrEmpty(TvgId) ? Name : TvgId;
    public bool HasArchive => ArchiveDays > 0;

    /// <summary>Отсортированы по Start.</summary>
    public List<Programme>? Programmes { get; set; }

    ImageSource? _logoImage;
    bool _logoRequested;
    /// <summary>Логотип из LogoCache; запрашивается при первом показе, после загрузки приходит PropertyChanged.</summary>
    public ImageSource? LogoImage
    {
        get
        {
            if (!_logoRequested)
            {
                _logoRequested = true;
                _logoImage = LogoCache.Get(Logo, img => { _logoImage = img; OnChanged(); });
            }
            return _logoImage;
        }
    }

    bool _isFavorite;
    public bool IsFavorite { get => _isFavorite; set { _isFavorite = value; OnChanged(); } }

    bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; set { _isPlaying = value; OnChanged(); } }

    public Programme? Now { get; private set; }
    public Programme? Next { get; private set; }
    public string NowTitle => Now?.Title ?? "";
    public double NowProgress => Now == null ? 0
        : Math.Clamp((DateTime.Now - Now.Start).TotalSeconds / (Now.Stop - Now.Start).TotalSeconds * 100, 0, 100);

    public Programme? At(DateTime t)
    {
        var l = Programmes;
        if (l == null || l.Count == 0) return null;
        int i = IndexAtOrBefore(l, t);
        return i >= 0 && l[i].Stop > t ? l[i] : null;
    }

    public void UpdateNow(DateTime t)
    {
        Programme? now = null, next = null;
        var l = Programmes;
        if (l != null && l.Count > 0)
        {
            int i = IndexAtOrBefore(l, t);
            if (i >= 0 && l[i].Stop > t) now = l[i];
            if (i + 1 < l.Count) next = l[i + 1];
        }
        Now = now;
        Next = next;
        OnChanged(nameof(Now));
        OnChanged(nameof(NowTitle));
        OnChanged(nameof(NowProgress));
    }

    static int IndexAtOrBefore(List<Programme> l, DateTime t)
    {
        int lo = 0, hi = l.Count - 1, idx = -1;
        while (lo <= hi)
        {
            int m = (lo + hi) / 2;
            if (l[m].Start <= t) { idx = m; lo = m + 1; } else hi = m - 1;
        }
        return idx;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record GroupItem(string Name, int Count);

public sealed class EpgItem
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public EpgItem(Channel channel, Programme p, DateTime now)
    {
        Channel = channel;
        Programme = p;
        IsNow = p.Start <= now && p.Stop > now;
        IsPast = p.Stop <= now;
        CanPlay = IsNow || (IsPast && channel.HasArchive && p.Start >= now.AddDays(-channel.ArchiveDays));
        var day = p.Start.Date;
        DayText = day == now.Date ? "Сегодня"
            : day == now.Date.AddDays(-1) ? "Вчера"
            : day == now.Date.AddDays(1) ? "Завтра"
            : Ru.TextInfo.ToTitleCase(p.Start.ToString("dddd, d MMMM", Ru));
    }

    public Channel Channel { get; }
    public Programme Programme { get; }
    public bool IsNow { get; }
    public bool IsPast { get; }
    public bool CanPlay { get; }
    public bool IsArchive => CanPlay && IsPast;
    public string DayText { get; }
    public string TimeText => Programme.Start.ToString("HH:mm");
    public string Title => Programme.Title;
    public string? Desc => Programme.Desc;
}
