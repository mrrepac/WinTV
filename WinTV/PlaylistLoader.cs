using System.IO;
using System.Net.Http;

namespace WinTV;

public static class PlaylistLoader
{
    public static (List<Channel> Channels, string? EpgUrl) ParseValid(string text)
    {
        if (!text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ответ не является плейлистом M3U.");
        var result = M3uParser.Parse(text.TrimStart('\uFEFF'));
        if (result.Channels.Count == 0)
            throw new InvalidDataException("В плейлисте нет каналов.");
        return result;
    }

    public static async Task<(List<Channel> Channels, string? EpgUrl, string? Warning)> LoadAsync(
        string url, string cache, HttpClient http, CancellationToken ct)
    {
        string text;
        (List<Channel> Channels, string? EpgUrl) parsed;
        try
        {
            text = await http.GetStringAsync(url, ct);
            ct.ThrowIfCancellationRequested();
            parsed = ParseValid(text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) when (File.Exists(cache))
        {
            parsed = ParseValid(await File.ReadAllTextAsync(cache, ct));
            return (parsed.Channels, parsed.EpgUrl, "Открыт сохранённый плейлист");
        }
        ct.ThrowIfCancellationRequested();
        string? warning = null;
        try { AtomicFile.Write(cache, text); }
        catch (Exception ex) { warning = "Не удалось сохранить кэш: " + ex.Message; }
        return (parsed.Channels, parsed.EpgUrl, warning);
    }
}
