using System.Windows;

namespace WinTV;

public partial class SettingsWindow : Window
{
    readonly Settings _s;

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
    }

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
        DialogResult = true;
    }
}
