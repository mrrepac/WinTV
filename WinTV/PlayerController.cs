using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;

namespace WinTV;

public sealed class PlayerController
{
    public MediaPlayer Native { get; }
    readonly LibVLC vlc;
    readonly PlayerCommandQueue queue = new();
    volatile bool closing, stopping;
    public bool IgnoreEvents => closing || stopping || IsFaulted;
    public int Generation => Volatile.Read(ref generation);
    public int ActiveGeneration => Volatile.Read(ref activeGeneration);
    int activeGeneration;
    public event Action? Rejected;
    public event Action? StateChanged;
    int generation, refreshing;
    record Snapshot(bool Playing, bool Seekable, long Time, int Audio, int Spu,
        TrackDescription[] AudioTracks, TrackDescription[] SubtitleTracks);
    Snapshot state = new(false, false, 0, -1, -1, [], []);
    int volume = 80;
    bool mute;
    public bool IsFaulted => queue.IsFaulted;
    public event Action? Failed;
    public PlayerController(LibVLC vlc)
    {
        this.vlc = vlc;
        Native = new MediaPlayer(vlc) { EnableHardwareDecoding = true, EnableKeyInput = false, EnableMouseInput = false };
        queue.Failed += () => Failed?.Invoke();
    }
    Task Run(string name, Action action) => closing ? Task.CompletedTask : queue.Run(name, () =>
    {
        if (!closing) action();
    });
    void ReadState()
    {
        Volatile.Write(ref state, new Snapshot(Native.IsPlaying, Native.IsSeekable, Native.Time,
            Native.AudioTrack, Native.Spu, Native.AudioTrackDescription, Native.SpuDescription));
        StateChanged?.Invoke();
    }
    void StopNative()
    {
        stopping = true;
        try { Native.Stop(); }
        finally { stopping = false; }
    }
    public bool IsPlaying => Volatile.Read(ref state).Playing;
    public bool IsSeekable => Volatile.Read(ref state).Seekable;
    public long Time { get => Volatile.Read(ref state).Time; set { _ = Run("Seek", () => Native.Time = value); } }
    public int AudioTrack => Volatile.Read(ref state).Audio;
    public int Spu => Volatile.Read(ref state).Spu;
    public TrackDescription[] AudioTrackDescription => Volatile.Read(ref state).AudioTracks;
    public TrackDescription[] SpuDescription => Volatile.Read(ref state).SubtitleTracks;
    public int Volume { get => volume; set { volume = value; _ = Run("Volume", () => Native.Volume = value); } }
    public bool Mute { get => mute; set { mute = value; _ = Run("Mute", () => Native.Mute = value); } }
    public void Pause() => _ = Run("Pause", () => { Native.Pause(); ReadState(); });
    public void SetSpu(int id) => _ = Run("Subtitle", () => { Native.SetSpu(id); ReadState(); });
    public void SetAudioTrack(int id) => _ = Run("Audio track", () => { Native.SetAudioTrack(id); ReadState(); });
    public void PlayUrl(string url, int caching, string userAgent, bool muted, int vol)
    {
        int id = Interlocked.Increment(ref generation);
        _ = Run("Play", () =>
        {
            if (id != Volatile.Read(ref generation) || closing) return;
            AppLog.Write("Playback requested");
            StopNative();
            if (id != Volatile.Read(ref generation) || closing) return;
            Volatile.Write(ref activeGeneration, id);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) { Rejected?.Invoke(); return; }
            using var media = new Media(vlc, uri);
            media.AddOption($":network-caching={caching}");
            if (!string.IsNullOrWhiteSpace(userAgent)) media.AddOption($":http-user-agent={userAgent}");
            if (!Native.Play(media)) { Rejected?.Invoke(); return; }
            Native.Mute = muted;
            Native.Volume = vol;
            ReadState();
        });
    }
    public Task StopAsync()
    {
        Interlocked.Increment(ref generation);
        return Run("Stop", () => { StopNative(); ReadState(); });
    }
    public async void Refresh()
    {
        if (Interlocked.Exchange(ref refreshing, 1) != 0) return;
        try { await Run("Read state", ReadState); }
        finally { Volatile.Write(ref refreshing, 0); }
    }
    public Task CloseAsync()
    {
        closing = true;
        Interlocked.Increment(ref generation);
        return queue.Run("Shutdown", () => { Native.Stop(); Native.Dispose(); vlc.Dispose(); });
    }
}
