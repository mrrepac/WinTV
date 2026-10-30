namespace WinTV;

/// <summary>Построение ссылки на архив передачи.</summary>
public static class Catchup
{
    public static string Build(Channel ch, Programme p, string mode)
    {
        long start = new DateTimeOffset(p.Start).ToUnixTimeSeconds();
        long stop = new DateTimeOffset(p.Stop).ToUnixTimeSeconds();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (ch.CatchupSource.Length > 0)
        {
            var src = Fill(ch.CatchupSource, p, start, stop, now);
            return ch.CatchupType.Equals("append", StringComparison.OrdinalIgnoreCase) ? ch.Url + src : src;
        }

        return mode == "append" ? Append(ch.Url, start, now) : Flussonic(ch.Url, start, stop, now);
    }

    /// <summary>…/mono.m3u8?token → …/mono-START-DURATION.m3u8?token (или -now для идущей передачи).</summary>
    static string Flussonic(string url, long start, long stop, long now)
    {
        int q = url.IndexOf('?');
        string path = q >= 0 ? url[..q] : url, query = q >= 0 ? url[q..] : "";
        int slash = path.LastIndexOf('/');
        string dir = path[..(slash + 1)], file = path[(slash + 1)..];
        int dot = file.LastIndexOf('.');
        string name = dot > 0 ? file[..dot] : "index", ext = dot > 0 ? file[dot..] : ".m3u8";
        var tail = stop > now ? "now" : (stop - start).ToString();
        return $"{dir}{name}-{start}-{tail}{ext}{query}";
    }

    static string Append(string url, long start, long now) =>
        url + (url.Contains('?') ? "&" : "?") + $"utc={start}&lutc={now}";

    static string Fill(string t, Programme p, long start, long stop, long now)
    {
        var u = p.Start.ToUniversalTime();
        return t.Replace("${start}", start.ToString()).Replace("{utc}", start.ToString())
            .Replace("${end}", stop.ToString()).Replace("{utcend}", stop.ToString())
            .Replace("${timestamp}", now.ToString()).Replace("{lutc}", now.ToString())
            .Replace("${duration}", (stop - start).ToString()).Replace("{duration}", (stop - start).ToString())
            .Replace("${offset}", (now - start).ToString()).Replace("{offset}", (now - start).ToString())
            .Replace("{Y}", u.ToString("yyyy")).Replace("{m}", u.ToString("MM")).Replace("{d}", u.ToString("dd"))
            .Replace("{H}", u.ToString("HH")).Replace("{M}", u.ToString("mm")).Replace("{S}", u.ToString("ss"));
    }
}
