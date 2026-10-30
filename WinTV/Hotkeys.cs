using System.Windows.Input;

namespace WinTV;

/// <summary>Сочетание клавиш. В настройках хранится как "Ctrl+Shift+F" (имена из enum Key).</summary>
public readonly record struct Gesture(ModifierKeys Mods, Key Key)
{
    public static Gesture? FromEvent(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None or Key.ImeProcessed)
            return null;
        return new Gesture(Keyboard.Modifiers & ~ModifierKeys.Windows, key);
    }

    public static Gesture? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var mods = ModifierKeys.None;
        var parts = s.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
            mods |= parts[i].ToLowerInvariant() switch
            {
                "ctrl" => ModifierKeys.Control,
                "shift" => ModifierKeys.Shift,
                "alt" => ModifierKeys.Alt,
                _ => ModifierKeys.None,
            };
        return parts.Length > 0 && Enum.TryParse<Key>(parts[^1], true, out var key) ? new Gesture(mods, key) : null;
    }

    /// <summary>Строка для settings.json.</summary>
    public string Serialize() => Prefix("Ctrl", "Shift", "Alt") + Key;

    /// <summary>Строка для показа пользователю.</summary>
    public string Display() => Prefix("Ctrl", "Shift", "Alt") + KeyName(Key);

    string Prefix(string ctrl, string shift, string alt) =>
        (Mods.HasFlag(ModifierKeys.Control) ? ctrl + "+" : "")
        + (Mods.HasFlag(ModifierKeys.Shift) ? shift + "+" : "")
        + (Mods.HasFlag(ModifierKeys.Alt) ? alt + "+" : "");

    static string KeyName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(k - Key.NumPad0),
        Key.Add => "Num +",
        Key.Subtract => "Num −",
        Key.Multiply => "Num *",
        Key.Divide => "Num /",
        Key.OemPlus => "+",
        Key.OemMinus => "−",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.Back => "Backspace",
        Key.Return => "Enter",
        Key.Escape => "Esc",
        Key.Space => "Пробел",
        Key.PageUp => "PgUp",
        Key.PageDown => "PgDn",
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Delete => "Del",
        Key.Insert => "Ins",
        _ => k.ToString(),
    };
}

public sealed record HotkeyAction(string Id, string Title, params string[] Defaults);

public static class Hotkeys
{
    public const int Slots = 2;

    public static readonly HotkeyAction[] Actions =
    {
        new("fullscreen", "Полный экран", "F", "F11"),
        new("search", "Поиск канала", "Ctrl+F"),
        new("favorite", "Избранное: добавить / убрать", "Ctrl+D", "Multiply"),
        new("epg", "Программа передач", "G"),
        new("info", "Информация о передаче", "I"),
        new("prevInList", "Предыдущий канал в списке", "PageUp"),
        new("nextInList", "Следующий канал в списке", "PageDown"),
        new("lastChannel", "Вернуться к прошлому каналу", "Back"),
        new("pause", "Пауза", "Space"),
        new("mute", "Без звука", "M"),
        new("audioTrack", "Звуковая дорожка", "A"),
        new("subtitles", "Субтитры", "S"),
        new("volumeUp", "Громкость +", "OemPlus", "Add"),
        new("volumeDown", "Громкость −", "OemMinus", "Subtract"),
        new("seekBack", "Архив: назад 30 с", "Left"),
        new("seekForward", "Архив: вперёд 30 с", "Right"),
        new("seekBackLong", "Архив: назад 5 мин", "Shift+Left"),
        new("seekForwardLong", "Архив: вперёд 5 мин", "Shift+Right"),
        new("hiddenGroups", "Показать / спрятать скрытые группы", "Ctrl+H"),
        new("reload", "Обновить плейлист", "Ctrl+R", "F5"),
        new("settings", "Настройки", "Ctrl+OemComma"),
    };

    /// <summary>Сочетания действия: из настроек, если пользователь их менял, иначе по умолчанию.</summary>
    public static List<Gesture?> For(Settings s, HotkeyAction a)
    {
        var src = s.Hotkeys.TryGetValue(a.Id, out var custom) ? custom : a.Defaults.ToList();
        var list = src.Select(Gesture.Parse).ToList();
        while (list.Count < Slots) list.Add(null);
        return list.Take(Slots).ToList();
    }

    public static Dictionary<Gesture, string> BuildMap(Settings s)
    {
        var map = new Dictionary<Gesture, string>();
        foreach (var a in Actions)
            foreach (var g in For(s, a))
                if (g is { } gesture) map.TryAdd(gesture, a.Id);
        return map;
    }
}
