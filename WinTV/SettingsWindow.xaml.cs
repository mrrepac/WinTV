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

    public SettingsWindow(Settings s)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _s = s;
        NoHistoryBox.IsChecked = s.NoHistory;
        PlaylistBox.Text = s.PlaylistUrl;
        EpgBox.Text = s.EpgUrl;
        UaBox.Text = s.UserAgent;
        CacheBox.Text = s.NetworkCaching.ToString();
        (s.CatchupMode switch { "flussonic" => ModeFlussonic, "append" => ModeAppend, _ => ModeAuto }).IsChecked = true;

        foreach (var a in Hotkeys.Actions) _hotkeys[a.Id] = Hotkeys.For(s, a);
        BuildHotkeyRows();
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
        var url = PlaylistBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            MessageBox.Show(this, "Укажите корректную ссылку на плейлист.", "WinTV");
            return;
        }
        _s.PlaylistUrl = url;
        _s.EpgUrl = EpgBox.Text.Trim();
        _s.UserAgent = UaBox.Text.Trim();
        _s.NetworkCaching = int.TryParse(CacheBox.Text, out var ms) ? Math.Clamp(ms, 100, 20000) : 1500;
        var mode = ModeFlussonic.IsChecked == true ? "flussonic" : ModeAppend.IsChecked == true ? "append" : "auto";
        if (mode != _s.CatchupMode) _s.CatchupDetected = null;
        _s.CatchupMode = mode;
        _s.NoHistory = NoHistoryBox.IsChecked == true;
        if (_s.NoHistory) { _s.LastChannel = null; _s.LastGroup = null; }
        SaveHotkeys();
        DialogResult = true;
    }
}
