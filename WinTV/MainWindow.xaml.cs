using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace WinTV;

public partial class MainWindow : Window
{
    const string FavGroup = "★ Избранное";
    const string AllGroup = "Все каналы";

    readonly Settings _s = Settings.Load();
    readonly HttpClient _http;
    LibVLC? _vlc;
    VlcPlayer? _player;

    List<Channel> _all = new();
    List<Channel> _view = new();
    Channel? _current, _previous, _epgChannel;
    Programme? _archive;
    string _archiveMode = "";
    bool _isFallback;
    int _retries, _playId;
    Dictionary<Gesture, string> _keymap;

    readonly DispatcherTimer _minuteTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    readonly DispatcherTimer _osdTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly DispatcherTimer _volumeTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer _numberTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _cursorTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    Point _lastMouse;
    string _numberBuffer = "";

    bool _fullscreen, _fsPanelShown;
    WindowState _prevState;

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _keymap = Hotkeys.BuildMap(_s);
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        { Timeout = TimeSpan.FromMinutes(3) };
        ApplyUserAgent();

        _minuteTimer.Tick += (_, _) => { UpdateNowAll(); if (Osd.Visibility == Visibility.Visible) FillOsd(); };
        _osdTimer.Tick += (_, _) => { _osdTimer.Stop(); Osd.Visibility = Visibility.Collapsed; };
        _volumeTimer.Tick += (_, _) => { _volumeTimer.Stop(); VolumeBox.Visibility = Visibility.Collapsed; };
        _numberTimer.Tick += (_, _) => { _numberTimer.Stop(); JumpToNumber(); };
        _tickTimer.Tick += (_, _) => { if (Osd.Visibility == Visibility.Visible) FillOsd(); };
        _cursorTimer.Tick += (_, _) => { _cursorTimer.Stop(); if (_fullscreen) Overlay.Cursor = Cursors.None; };
        _minuteTimer.Start();
        _tickTimer.Start();

        ContentRendered += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnKey;
    }

    // ---------- Запуск и загрузка ----------

    async void OnLoaded(object? sender, EventArgs e)
    {
        SetStatus("Запуск плеера…");
        // Первая инициализация LibVLC (сканирование плагинов) занимает секунды — не держим UI.
        _vlc = await Task.Run(() =>
        {
            Core.Initialize();
            return new LibVLC("--no-video-title-show");
        });
        _player = new VlcPlayer(_vlc)
        {
            EnableHardwareDecoding = true,
            EnableKeyInput = false,
            EnableMouseInput = false,
        };
        _player.Playing += (_, _) => Dispatcher.BeginInvoke(OnPlaying);
        _player.Buffering += (_, a) => Dispatcher.BeginInvoke(() =>
        {
            if (a.Cache < 100) ShowCenter($"Буферизация {a.Cache:0}%"); else HideCenter();
        });
        _player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(OnStreamFailed);
        _player.EndReached += (_, _) => Dispatcher.BeginInvoke(OnStreamEnded);
        Video.MediaPlayer = _player;

        if (string.IsNullOrWhiteSpace(_s.PlaylistUrl) && !OpenSettings()) return;
        await LoadAllAsync(autoplay: true);
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_player != null)
        {
            _s.Volume = _player.Volume;
            var p = _player;
            Task.Run(() => p.Stop()).Wait(2000);
        }
        _s.Save();
    }

    async Task LoadAllAsync(bool autoplay)
    {
        SetStatus("Загрузка плейлиста…");
        var cache = Path.Combine(Settings.Dir, "playlist.m3u");
        string text;
        try
        {
            text = await _http.GetStringAsync(_s.PlaylistUrl);
            Directory.CreateDirectory(Settings.Dir);
            await File.WriteAllTextAsync(cache, text);
        }
        catch (Exception ex)
        {
            if (!File.Exists(cache)) { SetStatus("Не удалось загрузить плейлист: " + ex.Message); return; }
            text = await File.ReadAllTextAsync(cache);
            SetStatus("Нет связи — открыт сохранённый плейлист");
        }

        var (channels, epgUrl) = M3uParser.Parse(text);
        foreach (var c in channels) c.IsFavorite = _s.Favorites.Contains(c.Key);
        var oldKey = _current?.Key;
        _all = channels;
        _current = null;

        var groups = new List<GroupItem>
        {
            new(FavGroup, _all.Count(c => c.IsFavorite)),
            new(AllGroup, _all.Count),
        };
        groups.AddRange(_all.GroupBy(c => c.Group).Select(g => new GroupItem(g.Key, g.Count())));
        var keepGroup = (GroupList.SelectedItem as GroupItem)?.Name ?? (_s.NoHistory ? null : _s.LastGroup);
        GroupList.ItemsSource = groups;
        GroupList.SelectedItem = groups.FirstOrDefault(g => g.Name == keepGroup) ?? groups[1];

        SetStatus($"Каналов: {_all.Count}");

        var lastKey = oldKey ?? (_s.NoHistory ? null : _s.LastChannel);
        var last = lastKey == null ? null : _all.FirstOrDefault(c => c.Key == lastKey);
        if (last != null)
        {
            if (!_view.Contains(last)) SelectGroup(AllGroup);
            if (autoplay || oldKey != null) Play(last);
            ChannelList.SelectedItem = last;
            ChannelList.ScrollIntoView(last);
        }
        FocusList(ChannelList);

        _ = LoadEpgAsync(string.IsNullOrWhiteSpace(_s.EpgUrl) ? epgUrl : _s.EpgUrl);
    }

    async Task LoadEpgAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var count = _all.Count;
        SetStatus($"Каналов: {count} · загрузка программы передач…");
        try
        {
            var file = await Epg.EnsureFileAsync(url, _http);
            var channels = _all;
            var map = await Task.Run(() => Epg.Load(file, channels));
            if (channels != _all) return;
            foreach (var (ch, list) in map) ch.Programmes = list;
            UpdateNowAll();
            SetStatus($"Каналов: {count} · программа есть для {map.Count}");
            if (EpgPanel.Visibility == Visibility.Visible) ShowEpg(_epgChannel);
            if (Osd.Visibility == Visibility.Visible) FillOsd();
        }
        catch (Exception ex)
        {
            SetStatus($"Каналов: {count} · программа не загрузилась: {ex.Message}");
        }
    }

    void UpdateNowAll()
    {
        var now = DateTime.Now;
        foreach (var c in _all) c.UpdateNow(now);
    }

    void SetStatus(string text) => StatusText.Text = text;

    // ---------- Списки ----------

    void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupList.SelectedItem is GroupItem g) { _s.LastGroup = _s.NoHistory ? null : g.Name; ApplyFilter(); }
    }

    void SelectGroup(string name)
    {
        var g = (GroupList.ItemsSource as IEnumerable<GroupItem>)?.FirstOrDefault(x => x.Name == name);
        if (g != null) GroupList.SelectedItem = g;
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        var g = (GroupList.SelectedItem as GroupItem)?.Name ?? AllGroup;
        IEnumerable<Channel> src =
            q.Length > 0 ? _all.Where(c => c.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            : g == FavGroup ? _all.Where(c => c.IsFavorite)
            : g == AllGroup ? _all
            : _all.Where(c => c.Group == g);
        _view = src.ToList();
        ChannelList.ItemsSource = _view;
        if (_current != null && _view.Contains(_current))
        {
            ChannelList.SelectedItem = _current;
            ChannelList.ScrollIntoView(_current);
        }
    }

    void ChannelList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ChannelList, e.OriginalSource as DependencyObject) is not ListBoxItem item
            || item.DataContext is not Channel ch) return;
        if ((e.OriginalSource as FrameworkElement)?.Tag as string == "fav") ToggleFavorite(ch);
        else if (ch != _current || _archive != null) Play(ch);
    }

    void FavMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ChannelList.SelectedItem is Channel ch) ToggleFavorite(ch);
    }

    void EpgMenu_Click(object sender, RoutedEventArgs e) => ShowEpg(ChannelList.SelectedItem as Channel);

    void EpgButton_Click(object sender, RoutedEventArgs e) => ToggleEpg();

    void EpgClose_Click(object sender, RoutedEventArgs e) => EpgPanel.Visibility = Visibility.Collapsed;

    void ToggleFavorite(Channel ch)
    {
        ch.IsFavorite = !ch.IsFavorite;
        if (ch.IsFavorite) _s.Favorites.Add(ch.Key); else _s.Favorites.Remove(ch.Key);
        _s.Save();
        if (GroupList.ItemsSource is List<GroupItem> groups)
        {
            var sel = GroupList.SelectedItem as GroupItem;
            groups[0] = new GroupItem(FavGroup, _all.Count(c => c.IsFavorite));
            GroupList.Items.Refresh();
            GroupList.SelectedItem = sel?.Name == FavGroup ? groups[0] : sel;
        }
        if ((GroupList.SelectedItem as GroupItem)?.Name == FavGroup) ApplyFilter();
    }

    static void FocusList(ListBox lb)
    {
        var item = lb.SelectedItem ?? (lb.Items.Count > 0 ? lb.Items[0] : null);
        if (item == null) { lb.Focus(); return; }
        lb.SelectedItem = item;
        lb.ScrollIntoView(item);
        lb.UpdateLayout();
        if (lb.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem c) c.Focus(); else lb.Focus();
    }

    // ---------- Воспроизведение ----------

    void Play(Channel ch, Programme? archive = null, string? mode = null, bool fallback = false)
    {
        if (_player == null || _vlc == null) return;
        if (_current != null && _current != ch) { _previous = _current; _current.IsPlaying = false; }
        _current = ch;
        ch.IsPlaying = true;
        _archive = archive;
        _isFallback = fallback;
        _retries = 0;

        var url = ch.Url;
        if (archive != null)
        {
            _archiveMode = mode ?? (_s.CatchupMode == "auto" ? _s.CatchupDetected ?? "flussonic" : _s.CatchupMode);
            url = Catchup.Build(ch, archive, _archiveMode);
        }
        StartMedia(url);

        _s.LastChannel = _s.NoHistory ? null : ch.Key;
        _s.Save();
        ShowOsd();
    }

    void StartMedia(string url)
    {
        _playId++;
        using var media = new Media(_vlc!, new Uri(url));
        media.AddOption($":network-caching={_s.NetworkCaching}");
        if (!string.IsNullOrWhiteSpace(_s.UserAgent)) media.AddOption($":http-user-agent={_s.UserAgent}");
        _player!.Play(media);
        _player.Volume = _s.Volume;
        ShowCenter("Загрузка…");
    }

    void OnPlaying()
    {
        HideCenter();
        _retries = 0;
        if (_archive != null && _s.CatchupMode == "auto" && _s.CatchupDetected != _archiveMode)
        {
            _s.CatchupDetected = _archiveMode;
            _s.Save();
        }
    }

    async void OnStreamFailed()
    {
        if (_current == null) return;
        if (_archive != null)
        {
            if (_s.CatchupMode == "auto" && _current.CatchupSource.Length == 0 && !_isFallback)
            {
                Play(_current, _archive, _archiveMode == "flussonic" ? "append" : "flussonic", fallback: true);
                return;
            }
            ShowCenter("Архив недоступен");
            return;
        }
        if (_retries >= 3) { ShowCenter("Не удалось открыть поток"); return; }
        _retries++;
        ShowCenter($"Переподключение… ({_retries})");
        var id = _playId;
        await Task.Delay(2000);
        if (id == _playId && _current != null && _archive == null) StartMedia(_current.Url);
    }

    void OnStreamEnded()
    {
        if (_archive == null) OnStreamFailed();
        else ShowCenter("Запись закончилась");
    }

    void Step(int delta)
    {
        var list = _view.Count > 0 ? _view : _all;
        if (list.Count == 0) return;
        int i = _current == null ? -1 : list.IndexOf(_current);
        i = i < 0 ? (delta > 0 ? 0 : list.Count - 1) : ((i + delta) % list.Count + list.Count) % list.Count;
        var ch = list[i];
        ChannelList.SelectedItem = ch;
        ChannelList.ScrollIntoView(ch);
        Play(ch);
    }

    void JumpToNumber()
    {
        var n = int.TryParse(_numberBuffer, out var v) ? v : 0;
        _numberBuffer = "";
        HideCenter();
        var ch = _all.FirstOrDefault(c => c.Number == n);
        if (ch == null) return;
        if (!_view.Contains(ch)) { SearchBox.Text = ""; SelectGroup(AllGroup); }
        ChannelList.SelectedItem = ch;
        ChannelList.ScrollIntoView(ch);
        Play(ch);
    }

    void ChangeVolume(int delta)
    {
        if (_player == null) return;
        _s.Volume = Math.Clamp(_s.Volume + delta, 0, 150);
        _player.Volume = _s.Volume;
        _player.Mute = false;
        ShowVolume($"🔊 {_s.Volume}%");
    }

    void ShowVolume(string text)
    {
        VolumeText.Text = text;
        VolumeBox.Visibility = Visibility.Visible;
        _volumeTimer.Stop();
        _volumeTimer.Start();
    }

    void Seek(int seconds)
    {
        if (_player == null || _archive == null || !_player.IsSeekable) return;
        _player.Time = Math.Max(0, _player.Time + seconds * 1000L);
        ShowOsd();
    }

    // ---------- Экранные подсказки ----------

    void ShowCenter(string text) { CenterText.Text = text; CenterBox.Visibility = Visibility.Visible; }
    void HideCenter() => CenterBox.Visibility = Visibility.Collapsed;

    void ShowOsd()
    {
        if (_current == null) return;
        FillOsd();
        Osd.Visibility = Visibility.Visible;
        _osdTimer.Stop();
        _osdTimer.Start();
    }

    void FillOsd()
    {
        var ch = _current;
        if (ch == null) return;
        OsdNumber.Text = ch.Number.ToString();
        OsdName.Text = ch.Name;
        OsdLogo.Source = new LogoConverter().Convert(ch.Logo, typeof(object), null!, null!) as System.Windows.Media.ImageSource;
        OsdArchive.Visibility = _archive != null ? Visibility.Visible : Visibility.Collapsed;
        OsdClock.Text = DateTime.Now.ToString("HH:mm");

        if (_archive != null)
        {
            var p = _archive;
            OsdTitle.Text = p.Title;
            var pos = TimeSpan.FromMilliseconds(Math.Max(0, _player?.Time ?? 0));
            var dur = p.Stop - p.Start;
            OsdProgress.Value = dur.TotalSeconds > 0 ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds * 100, 0, 100) : 0;
            OsdTime.Text = $"{p.Start:dd.MM HH:mm} · {Fmt(pos)} / {Fmt(dur)}";
            var next = ch.At(p.Stop.AddSeconds(1));
            OsdNext.Text = "←/→ перемотка 30 с · Shift — 5 мин · Esc — в прямой эфир"
                           + (next != null ? $"   ·   далее: {next.Title}" : "");
        }
        else
        {
            var now = ch.Now;
            OsdTitle.Text = now?.Title ?? "Нет программы";
            OsdProgress.Value = ch.NowProgress;
            OsdProgress.Visibility = now != null ? Visibility.Visible : Visibility.Collapsed;
            OsdTime.Text = now != null ? $"{now.Start:HH:mm} – {now.Stop:HH:mm}" : "";
            OsdNext.Text = ch.Next != null ? $"Далее {ch.Next.Start:HH:mm}  {ch.Next.Title}" : "";
        }
        if (_archive != null) OsdProgress.Visibility = Visibility.Visible;
    }

    static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    // ---------- Программа передач ----------

    void ToggleEpg()
    {
        if (EpgPanel.Visibility == Visibility.Visible) EpgPanel.Visibility = Visibility.Collapsed;
        else ShowEpg(ChannelList.SelectedItem as Channel ?? _current);
    }

    void ShowEpg(Channel? ch)
    {
        if (ch == null) return;
        _epgChannel = ch;
        EpgTitle.Text = ch.Name;
        EpgSub.Text = ch.HasArchive ? $"Архив: {ch.ArchiveDays} дн. · двойной щелчок или Enter — смотреть" : "Без архива";
        EpgPanel.Visibility = Visibility.Visible;

        var now = DateTime.Now;
        var from = ch.HasArchive ? now.AddDays(-ch.ArchiveDays) : now.AddHours(-3);
        var items = (ch.Programmes ?? new List<Programme>())
            .Where(p => p.Stop > from)
            .Select(p => new EpgItem(ch, p, now))
            .ToList();
        if (items.Count == 0)
        {
            EpgList.ItemsSource = null;
            EpgSub.Text = ch.Programmes == null ? "Программа ещё не загружена" : "Нет программы для этого канала";
            return;
        }
        var view = new ListCollectionView(items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(EpgItem.DayText)));
        EpgList.ItemsSource = view;

        var cur = items.FirstOrDefault(i => i.IsNow) ?? items[0];
        EpgList.SelectedItem = cur;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            EpgList.ScrollIntoView(items[^1]);
            EpgList.ScrollIntoView(cur);
        });
    }

    void EpgList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PlayEpgItem(EpgList.SelectedItem as EpgItem);

    void PlayEpgItem(EpgItem? item)
    {
        if (item == null) return;
        if (item.IsNow) Play(item.Channel);
        else if (item.CanPlay) Play(item.Channel, item.Programme);
        else ShowCenter("Эта передача ещё не вышла");
    }

    // ---------- Полноэкранный режим ----------

    void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        _fsPanelShown = false;
        var panels = _fullscreen ? Visibility.Collapsed : Visibility.Visible;
        GroupsPanel.Visibility = panels;
        ChannelsPanel.Visibility = panels;
        if (_fullscreen)
        {
            EpgPanel.Visibility = Visibility.Collapsed;
            _prevState = WindowState;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _prevState;
        }
        ShowCursor();
        Activate();
    }

    void ShowFsPanel(bool show)
    {
        _fsPanelShown = show;
        ChannelsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) FocusList(ChannelList);
    }

    void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Activate();
        if (e.ClickCount == 2) ToggleFullscreen();
        else ShowOsd();
    }

    // Мышь над видео ловит Overlay (он в отдельном окне LibVLCSharp), поэтому курсор прячем именно на нём.
    void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(Overlay);
        // Смена курсора порождает MouseMove без реального движения — такие события пропускаем.
        if (Math.Abs(pos.X - _lastMouse.X) < 2 && Math.Abs(pos.Y - _lastMouse.Y) < 2) return;
        _lastMouse = pos;
        ShowCursor();
    }

    void ShowCursor()
    {
        Overlay.Cursor = null;
        _cursorTimer.Stop();
        if (_fullscreen) _cursorTimer.Start();
    }

    void Overlay_MouseWheel(object sender, MouseWheelEventArgs e) => ChangeVolume(e.Delta > 0 ? 5 : -5);

    // ---------- Клавиатура ----------

    void OnKey(object sender, KeyEventArgs e)
    {
        if (SearchBox.IsKeyboardFocusWithin)
        {
            if (e.Key == Key.Escape) { SearchBox.Text = ""; FocusList(ChannelList); e.Handled = true; }
            else if (e.Key is Key.Down or Key.Enter) { FocusList(ChannelList); e.Handled = true; }
            return;
        }

        bool inChannels = ChannelList.IsKeyboardFocusWithin;
        bool inGroups = GroupList.IsKeyboardFocusWithin;
        bool inEpg = EpgList.IsKeyboardFocusWithin;
        bool listsHidden = _fullscreen && !_fsPanelShown;
        bool plain = Keyboard.Modifiers == ModifierKeys.None;
        e.Handled = true;

        // Навигация (не настраивается) — только без модификаторов; остальное идёт через карту горячих клавиш.
        switch (e.Key)
        {
            case Key.Escape when plain:
                if (_fullscreen && _fsPanelShown) ShowFsPanel(false);
                else if (EpgPanel.Visibility == Visibility.Visible && inEpg) { EpgPanel.Visibility = Visibility.Collapsed; FocusList(ChannelList); }
                else if (_archive != null && _current != null) Play(_current);
                else if (_fullscreen) ToggleFullscreen();
                else if (EpgPanel.Visibility == Visibility.Visible) EpgPanel.Visibility = Visibility.Collapsed;
                break;
            case Key.Up when plain && listsHidden:
                Step(-1);
                break;
            case Key.Down when plain && listsHidden:
                Step(1);
                break;
            case Key.Up or Key.Down when plain && !inChannels && !inGroups && !inEpg:
                FocusList(ChannelList);
                break;
            case Key.Enter when plain:
                if (listsHidden) ShowFsPanel(true);
                else if (inEpg) PlayEpgItem(EpgList.SelectedItem as EpgItem);
                else if (inGroups) FocusList(ChannelList);
                else if (ChannelList.SelectedItem is Channel sel)
                {
                    if (sel != _current || _archive != null) Play(sel);
                    if (_fullscreen) ShowFsPanel(false);
                }
                break;
            case Key.Left when plain && inChannels && !_fullscreen:
                FocusList(GroupList);
                break;
            case Key.Right when plain && inGroups:
                FocusList(ChannelList);
                break;
            case Key.Right when plain && inChannels && !_fullscreen:
                ShowEpg(ChannelList.SelectedItem as Channel);
                if (EpgList.Items.Count > 0) FocusList(EpgList);
                break;
            case Key.Left when plain && inEpg:
                FocusList(ChannelList);
                break;
            case (>= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9) when plain:
                var digit = e.Key >= Key.NumPad0 ? e.Key - Key.NumPad0 : e.Key - Key.D0;
                if (_numberBuffer.Length < 4) _numberBuffer += digit;
                ShowCenter(_numberBuffer);
                _numberTimer.Stop();
                _numberTimer.Start();
                break;
            default:
                e.Handled = Gesture.FromEvent(e) is { } g && _keymap.TryGetValue(g, out var action) && RunAction(action, inChannels);
                break;
        }
    }

    bool RunAction(string id, bool inChannels)
    {
        switch (id)
        {
            case "fullscreen": ToggleFullscreen(); break;
            case "search":
                if (_fullscreen) ToggleFullscreen();
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case "favorite":
                if ((inChannels ? ChannelList.SelectedItem as Channel : _current) is { } ch) ToggleFavorite(ch);
                break;
            case "epg": ToggleEpg(); break;
            case "info": ShowOsd(); break;
            case "prevInList": Step(-1); break;
            case "nextInList": Step(1); break;
            case "lastChannel":
                if (_previous != null) { var p = _previous; ChannelList.SelectedItem = p; Play(p); }
                break;
            case "pause": _player?.Pause(); break;
            case "mute":
                if (_player != null) { _player.Mute = !_player.Mute; ShowVolume(_player.Mute ? "🔇 Без звука" : $"🔊 {_s.Volume}%"); }
                break;
            case "volumeUp": ChangeVolume(5); break;
            case "volumeDown": ChangeVolume(-5); break;
            case "seekBack": Seek(-30); break;
            case "seekForward": Seek(30); break;
            case "seekBackLong": Seek(-300); break;
            case "seekForwardLong": Seek(300); break;
            case "reload": Reload_Click(this, new RoutedEventArgs()); break;
            case "settings": Settings_Click(this, new RoutedEventArgs()); break;
            default: return false;
        }
        return true;
    }

    // ---------- Настройки ----------

    async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAllAsync(autoplay: false);

    async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (OpenSettings()) await LoadAllAsync(autoplay: false);
    }

    bool OpenSettings()
    {
        var w = new SettingsWindow(_s) { Owner = this };
        if (w.ShowDialog() != true) return false;
        _s.Save();
        ApplyUserAgent();
        _keymap = Hotkeys.BuildMap(_s);
        return true;
    }

    void ApplyUserAgent()
    {
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.TryParseAdd(
            string.IsNullOrWhiteSpace(_s.UserAgent) ? "WinTV/1.0" : _s.UserAgent);
    }
}
