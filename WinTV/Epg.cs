using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace WinTV;

/// <summary>Загрузка и разбор XMLTV (в том числе .gz).</summary>
public static class Epg
{
    static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    public static async Task<string> EnsureFileAsync(string url, HttpClient http)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..10];
        var file = Path.Combine(Settings.Dir, $"epg_{hash}.xml");
        if (File.Exists(file) && DateTime.Now - File.GetLastWriteTime(file) < MaxAge) return file;

        Directory.CreateDirectory(Settings.Dir);
        var tmp = file + ".tmp";
        try
        {
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(tmp);
                await src.CopyToAsync(dst);
            }
            File.Move(tmp, file, true);
        }
        catch when (File.Exists(file))
        {
            // Нет сети — используем старую программу.
        }
        return file;
    }

    public static Dictionary<Channel, List<Programme>> Load(string file, IList<Channel> channels)
    {
        var now = DateTime.Now;
        var minStop = now.AddDays(-(Math.Max(channels.Select(c => c.ArchiveDays).DefaultIfEmpty(0).Max(), 1) + 1));
        var maxStart = now.AddDays(3);
        var descFrom = now.AddDays(-1);
        var descTo = now.AddDays(1);

        var byId = channels.Where(c => c.TvgId.Length > 0)
            .GroupBy(c => c.TvgId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var epgChannelNames = new Dictionary<string, List<string>>();
        Dictionary<string, List<Channel>>? map = null;
        var result = new Dictionary<Channel, List<Programme>>();
        var lists = new Dictionary<string, List<Programme>>();
        var titlePool = new Dictionary<string, string>();

        using var fs = File.OpenRead(file);
        Stream stream = fs;
        if (fs.ReadByte() == 0x1f && fs.ReadByte() == 0x8b) { fs.Position = 0; stream = new GZipStream(fs, CompressionMode.Decompress); }
        else fs.Position = 0;

        using var r = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        });

        while (!r.EOF)
        {
            if (r.NodeType != XmlNodeType.Element) { r.Read(); continue; }

            if (r.Name == "channel")
            {
                var id = r.GetAttribute("id") ?? "";
                var names = new List<string>();
                ReadChildren(r, sub =>
                {
                    if (sub.Name == "display-name") names.Add(sub.ReadElementContentAsString());
                    else sub.Skip();
                });
                epgChannelNames[id] = names;
                r.Read();
            }
            else if (r.Name == "programme")
            {
                map ??= BuildMap(byId, epgChannelNames, channels);
                var chId = r.GetAttribute("channel") ?? "";
                if (!map.ContainsKey(chId)
                    || !TryParseTime(r.GetAttribute("start"), out var start)
                    || !TryParseTime(r.GetAttribute("stop"), out var stop)
                    || stop < minStop || start > maxStart)
                {
                    r.Skip();
                    continue;
                }

                string? title = null, desc = null;
                ReadChildren(r, sub =>
                {
                    if (sub.Name == "title" && title == null) title = sub.ReadElementContentAsString();
                    else if (sub.Name == "desc" && desc == null) desc = sub.ReadElementContentAsString();
                    else sub.Skip();
                });
                r.Read();

                title = (title ?? "").Trim();
                if (!titlePool.TryGetValue(title, out var pooled)) titlePool[title] = pooled = title;
                if (desc != null && (stop < descFrom || start > descTo)) desc = null;
                if (desc is { Length: > 600 }) desc = desc[..600] + "…";

                if (!lists.TryGetValue(chId, out var list)) lists[chId] = list = new List<Programme>();
                list.Add(new Programme { Start = start, Stop = stop, Title = pooled, Desc = desc?.Trim() });
            }
            else r.Read();
        }

        if (map == null) return result;
        foreach (var (id, list) in lists)
        {
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
            foreach (var ch in map[id]) result.TryAdd(ch, list);
        }
        return result;
    }

    /// <summary>Соответствие id из EPG каналам: сначала по tvg-id, затем по названию.</summary>
    static Dictionary<string, List<Channel>> BuildMap(Dictionary<string, List<Channel>> byId,
        Dictionary<string, List<string>> epgChannels, IList<Channel> channels)
    {
        var map = new Dictionary<string, List<Channel>>();
        var matched = new HashSet<Channel>();
        foreach (var id in epgChannels.Keys)
            if (byId.TryGetValue(id, out var chs)) { map[id] = chs; matched.UnionWith(chs); }
        // Каналы из плейлиста, у которых нет id в EPG — пробуем найти по имени.
        var byName = new Dictionary<string, List<Channel>>();
        foreach (var c in channels.Where(c => !matched.Contains(c)))
            foreach (var n in new[] { c.Name, c.TvgName }.Where(n => n.Length > 0).Select(Normalize).Distinct())
            {
                if (!byName.TryGetValue(n, out var l)) byName[n] = l = new List<Channel>();
                l.Add(c);
            }
        foreach (var (id, names) in epgChannels)
        {
            if (map.ContainsKey(id)) continue;
            foreach (var n in names)
                if (byName.TryGetValue(Normalize(n), out var l)) { map[id] = l; byName.Remove(Normalize(n)); break; }
        }
        // Если в EPG нет списка каналов — используем tvg-id как есть.
        if (epgChannels.Count == 0) foreach (var (id, chs) in byId) map[id] = chs;
        return map;
    }

    static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant()) if (char.IsLetterOrDigit(ch) || ch == '+') sb.Append(ch);
        return sb.ToString();
    }

    static void ReadChildren(XmlReader r, Action<XmlReader> onElement)
    {
        using var sub = r.ReadSubtree();
        sub.Read();
        sub.Read();
        while (!sub.EOF)
        {
            if (sub.NodeType == XmlNodeType.Element && sub.Depth == 1) onElement(sub);
            else sub.Read();
        }
    }

    /// <summary>Формат XMLTV: "20261030120000 +0300".</summary>
    static bool TryParseTime(string? s, out DateTime local)
    {
        local = default;
        if (s == null || s.Length < 14) return false;
        if (!DateTime.TryParseExact(s[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return false;
        var offset = TimeSpan.Zero;
        var tz = s[14..].Trim();
        if (tz.Length >= 5 && (tz[0] == '+' || tz[0] == '-')
            && int.TryParse(tz.AsSpan(1, 2), out var hh) && int.TryParse(tz.AsSpan(3, 2), out var mm))
        {
            offset = new TimeSpan(hh, mm, 0);
            if (tz[0] == '-') offset = -offset;
        }
        local = new DateTimeOffset(dt, offset).LocalDateTime;
        return true;
    }
}
