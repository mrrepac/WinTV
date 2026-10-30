using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WinTV;

public partial class SettingsWindow : Window
{
    readonly Settings _s;

    /// <summary>Рабочая копия горячих клавиш: id действия → ячейки.</summary>
    readonly Dictionary<string, List<Gesture?>> _hotkeys = new();
    readonly Dictionary<(string Id, int Slot), Button> _slotButtons = new();
    (string Id, int Slot)? _capturing;

    /// <summary>Клавиши навигации без модификаторов — их нельзя назначить действиям.</summary>
    static readonly HashSet<Key> Reserved = new()
    {
        Key.Enter, Key.Escape, Key.Up, Key.Down, Key.Tab,
        Key.D0, Key.D1, Key.D2, Key.D3, Key.D4, Key.D5, Key.D6, Key.D7, Key.D8, Key.D9,
        Key.NumPad0, Key.NumPad1, Key.NumPad2, Key.NumPad3, Key.NumPad4,
        Key.NumPad5, Key.NumPad6, Key.NumPad7, Key.NumPad8, Key.NumPad9,
    };

    readonly Dictionary<string, CheckBox> _groupChecks = new();
    /// <summary>Плейлист, чьи скрытые группы редактируются (активный на момент открытия).</summary>
    readonly Playlist? _hiddenTarget;

    /// <summary>Рабочая копия списка плейлистов; в настройки переносится при сохранении.</summary>
    sealed class PlaylistDraft : INotifyPropertyChanged
    {
        public string Id { get; init; } = new Playlist().Id;
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string EpgUrl { get; set; } = "";
        public bool IsActive { get; set; }
        public string Display => (IsActive ? "●  " : "     ") + (Name.Trim().Length > 0 ? Name.Trim() : "Без названия")
                                 + (IsActive ? "   (активный)" : "");
        public void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    readonly List<PlaylistDraft> _drafts = new();
    bool _loadingFields;

    public SettingsWindow(Settings s, IEnumerable<string> groups)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _s = s;
        NoHistoryBox.IsChecked = s.NoHistory;
        _hiddenTarget = s.Active;
        var active = s.Active;
        foreach (var p in s.Playlists)
            _drafts.Add(new PlaylistDraft { Id = p.Id, Name = p.Name, Url = p.Url, EpgUrl = p.EpgUrl, IsActive = p == active });
        if (_drafts.Count == 0) _drafts.Add(new PlaylistDraft { Name = "Мой плейлист", IsActive = true });
        PlaylistList.ItemsSource = _drafts;
        PlaylistList.SelectedItem = _drafts.FirstOrDefault(d => d.IsActive) ?? _drafts[0];
        UaBox.Text = s.UserAgent;
        CacheBox.Text = s.NetworkCaching.ToString();
        (s.CatchupMode switch { "flussonic" => ModeFlussonic, "append" => ModeAppend, _ => ModeAuto }).IsChecked = true;

        foreach (var a in Hotkeys.Actions) _hotkeys[a.Id] = Hotkeys.For(s, a);
        BuildHotkeyRows();
        BuildGroupChecks(groups);
    }

    // ---------- Плейлисты ----------

    PlaylistDraft? Selected => PlaylistList.SelectedItem as PlaylistDraft;

    void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _loadingFields = true;
        NameBox.Text = Selected?.Name ?? "";
        PlaylistBox.Text = Selected?.Url ?? "";
        EpgBox.Text = Selected?.EpgUrl ?? "";
        _loadingFields = false;
        NameBox.IsEnabled = PlaylistBox.IsEnabled = EpgBox.IsEnabled = Selected != null;
    }

    void PlaylistField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingFields || Selected is not { } d) return;
        d.Name = NameBox.Text;
        d.Url = PlaylistBox.Text;
        d.EpgUrl = EpgBox.Text;
        d.Changed();
    }

    void AddPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var d = new PlaylistDraft { Name = $"Плейлист {_drafts.Count + 1}" };
        _drafts.Add(d);
        PlaylistList.Items.Refresh();
        PlaylistList.SelectedItem = d;
        PlaylistBox.Focus();
    }

    void ActivatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } sel) return;
        foreach (var d in _drafts) { d.IsActive = d == sel; d.Changed(); }
    }

    void RemovePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } sel) return;
        var name = sel.Name.Trim().Length > 0 ? sel.Name.Trim() : "без названия";
        if (MessageBox.Show(this, $"Удалить плейлист «{name}»? Его избранное и скрытые группы тоже удалятся.",
                "WinTV", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        int i = _drafts.IndexOf(sel);
        _drafts.Remove(sel);
        if (sel.IsActive && _drafts.Count > 0) { _drafts[0].IsActive = true; _drafts[0].Changed(); }
        PlaylistList.Items.Refresh();
        PlaylistList.SelectedItem = _drafts.Count > 0 ? _drafts[Math.Min(i, _drafts.Count - 1)] : null;
    }

    /// <summary>Переносит рабочую копию в настройки; у оставшихся плейлистов избранное и история сохраняются.</summary>
    bool SavePlaylists()
    {
        if (_drafts.Count == 0)
        {
            MessageBox.Show(this, "Добавьте хотя бы один плейлист.", "WinTV");
            return false;
        }
        foreach (var d in _drafts)
            if (!Uri.TryCreate(d.Url.Trim(), UriKind.Absolute, out _))
            {
                PlaylistList.SelectedItem = d;
                MessageBox.Show(this, "Укажите корректную ссылку на плейлист.", "WinTV");
                PlaylistBox.Focus();
                return false;
            }

        var old = _s.Playlists.ToDictionary(p => p.Id);
        _s.Playlists = _drafts.Select((d, i) =>
        {
            var p = old.GetValueOrDefault(d.Id) ?? new Playlist { Id = d.Id };
            p.Name = d.Name.Trim().Length > 0 ? d.Name.Trim() : $"Плейлист {i + 1}";
            p.Url = d.Url.Trim();
            p.EpgUrl = d.EpgUrl.Trim();
            return p;
        }).ToList();
        _s.ActivePlaylist = (_drafts.FirstOrDefault(d => d.IsActive) ?? _drafts[0]).Id;
        foreach (var removed in old.Values.Where(p => !_s.Playlists.Contains(p)))
            try { File.Delete(removed.CacheFile); } catch { }
        return true;
    }

    // ---------- Скрытые группы ----------

    void BuildGroupChecks(IEnumerable<string> groups)
    {
        var hidden = _hiddenTarget?.HiddenGroups ?? new HashSet<string>();
        foreach (var g in groups.Concat(hidden).Distinct())
        {
            var cb = new CheckBox { Content = g, IsChecked = hidden.Contains(g), Margin = new Thickness(0, 2, 0, 2) };
            _groupChecks[g] = cb;
            GroupChecks.Children.Add(cb);
        }
        if (_groupChecks.Count == 0)
            GroupChecks.Children.Add(new TextBlock { Text = "Группы появятся после загрузки плейлиста", Foreground = (System.Windows.Media.Brush)FindResource("Muted") });
        bool locked = !string.IsNullOrEmpty(_s.HiddenPinHash);
        RemovePinBox.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        SetLocked(locked);
    }

    void SetLocked(bool locked)
    {
        GroupChecks.IsEnabled = !locked;
        PinRow.IsEnabled = !locked;
        UnlockButton.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
    }

    void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (Pin.Ask(this, _s.HiddenPinHash)) SetLocked(false);
    }

    void SaveHiddenGroups()
    {
        if (!GroupChecks.IsEnabled) return; // не разблокировано — ничего не меняем
        if (_hiddenTarget != null)
            _hiddenTarget.HiddenGroups = _groupChecks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToHashSet();
        if (PinBox.Password.Length > 0) _s.HiddenPinHash = Pin.Hash(PinBox.Password);
        else if (RemovePinBox.IsChecked == true) _s.HiddenPinHash = null;
    }

    // ---------- Горячие клавиши ----------

    void BuildHotkeyRows()
    {
        foreach (var a in Hotkeys.Actions)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            for (int slot = 0; slot < Hotkeys.Slots; slot++)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            }
            row.Children.Add(new TextBlock { Text = a.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

            for (int slot = 0; slot < Hotkeys.Slots; slot++)
            {
                (string Id, int Slot) key = (a.Id, slot);
                var btn = new Button { Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(6, 0, 0, 0) };
                btn.Click += (_, _) => StartCapture(key);
                Grid.SetColumn(btn, 1 + slot * 2);
                row.Children.Add(btn);
                _slotButtons[key] = btn;

                var clear = new Button { Content = "✕", Padding = new Thickness(0, 5, 0, 5), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Очистить" };
                clear.Click += (_, _) => { _capturing = null; _hotkeys[key.Id][key.Slot] = null; RefreshSlots(); };
                Grid.SetColumn(clear, 2 + slot * 2);
                row.Children.Add(clear);
            }
            HotkeyRows.Children.Add(row);
        }
        RefreshSlots();
    }

    void RefreshSlots()
    {
        foreach (var ((id, slot), btn) in _slotButtons)
        {
            bool active = _capturing == (id, slot);
            btn.Content = active ? "Нажмите…" : _hotkeys[id][slot]?.Display() ?? "—";
            btn.Background = (System.Windows.Media.Brush)FindResource(active ? "Accent" : "Hover");
        }
    }

    void StartCapture((string Id, int Slot) key)
    {
        _capturing = key;
        RefreshSlots();
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturing is not { } target) return;
        e.Handled = true;
        if (Gesture.FromEvent(e) is not { } g) return; // нажат только модификатор — ждём основную клавишу

        if (g.Key == Key.Escape && g.Mods == ModifierKeys.None) { _capturing = null; RefreshSlots(); return; }
        if (g.Mods == ModifierKeys.None && Reserved.Contains(g.Key))
        {
            HotkeyHint.Text = $"«{g.Display()}» занята навигацией — выберите другое сочетание.";
            return;
        }

        // Сочетание может принадлежать только одному действию.
        string? movedFrom = null;
        foreach (var (id, slots) in _hotkeys)
            for (int i = 0; i < slots.Count; i++)
                if (slots[i] == g && (id, i) != target)
                {
                    slots[i] = null;
                    movedFrom = Hotkeys.Actions.First(a => a.Id == id).Title;
                }
        _hotkeys[target.Id][target.Slot] = g;
        _capturing = null;
        HotkeyHint.Text = movedFrom != null
            ? $"«{g.Display()}» снято с действия «{movedFrom}»."
            : "Щёлкните по ячейке и нажмите сочетание. Esc — отмена, ✕ — очистить.";
        RefreshSlots();
    }

    void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        _capturing = null;
        foreach (var a in Hotkeys.Actions)
            _hotkeys[a.Id] = Hotkeys.For(new Settings(), a);
        RefreshSlots();
    }

    void SaveHotkeys()
    {
        _s.Hotkeys.Clear();
        foreach (var a in Hotkeys.Actions)
        {
            var chosen = _hotkeys[a.Id].Where(g => g != null).Select(g => g!.Value.Serialize()).ToList();
            var defaults = a.Defaults.Select(Gesture.Parse).Where(g => g != null).Select(g => g!.Value.Serialize());
            if (!chosen.SequenceEqual(defaults)) _s.Hotkeys[a.Id] = chosen;
        }
    }

    // ---------- Сохранение ----------

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!SavePlaylists()) return;
        _s.UserAgent = UaBox.Text.Trim();
        _s.NetworkCaching = int.TryParse(CacheBox.Text, out var ms) ? Math.Clamp(ms, 100, 20000) : 1500;
        var mode = ModeFlussonic.IsChecked == true ? "flussonic" : ModeAppend.IsChecked == true ? "append" : "auto";
        if (mode != _s.CatchupMode) _s.CatchupDetected = null;
        _s.CatchupMode = mode;
        _s.NoHistory = NoHistoryBox.IsChecked == true;
        if (_s.NoHistory)
            foreach (var p in _s.Playlists) { p.LastChannel = null; p.LastGroup = null; }
        SaveHotkeys();
        SaveHiddenGroups();
        DialogResult = true;
    }
}
