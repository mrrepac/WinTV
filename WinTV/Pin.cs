using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace WinTV;

/// <summary>PIN на показ скрытых групп. В настройках хранится только соль и PBKDF2-хеш.</summary>
public static class Pin
{
    const int Iterations = 100_000;

    public static string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);
    }

    public static bool Verify(string pin, string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 2) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(hash, expected);
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Спрашивает PIN, если он задан. true — PIN верный или не задан.</summary>
    public static bool Ask(Window owner, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return true;
        var box = new PasswordBox { Margin = new Thickness(0, 8, 0, 0), MaxLength = 12 };
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.IndianRed, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed, Text = "Неверный PIN" };
        var ok = new Button { Content = "OK", IsDefault = true, Margin = new Thickness(0, 0, 8, 0), Background = (System.Windows.Media.Brush)Application.Current.FindResource("Accent") };
        var cancel = new Button { Content = "Отмена", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Введите PIN для скрытых групп" });
        panel.Children.Add(box);
        panel.Children.Add(error);
        panel.Children.Add(buttons);

        var w = new Window
        {
            Title = "WinTV", Owner = owner, Content = panel, Width = 320, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("Panel"),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Text"),
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 13,
        };
        DarkTitleBar.Apply(w);
        ok.Click += (_, _) =>
        {
            if (Verify(box.Password, stored)) w.DialogResult = true;
            else { error.Visibility = Visibility.Visible; box.Clear(); box.Focus(); }
        };
        w.Loaded += (_, _) => box.Focus();
        return w.ShowDialog() == true;
    }
}
