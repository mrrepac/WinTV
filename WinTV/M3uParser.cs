using System.Text.RegularExpressions;

namespace WinTV;

public static partial class M3uParser
{
    [GeneratedRegex(@"([\w-]+)=""([^""]*)""")]
    private static partial Regex AttrRx();

    public static (List<Channel> Channels, string? EpgUrl) Parse(string text)
    {
        var list = new List<Channel>();
        string? epgUrl = null;
        Channel? cur = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                var a = Attrs(line);
                epgUrl = a.GetValueOrDefault("url-tvg") ?? a.GetValueOrDefault("x-tvg-url");
                if (epgUrl != null && epgUrl.Contains(',')) epgUrl = epgUrl.Split(',')[0].Trim();
            }
            else if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                var a = Attrs(line);
                // Имя канала — после первой запятой, идущей за последним атрибутом.
                int q = line.LastIndexOf('"');
                int c = line.IndexOf(',', Math.Max(q, 0));
                var name = c >= 0 ? line[(c + 1)..].Trim() : "";
                var tvgName = a.GetValueOrDefault("tvg-name") ?? "";
                cur = new Channel
                {
                    Name = name.Length > 0 ? name : tvgName,
                    TvgName = tvgName,
                    TvgId = a.GetValueOrDefault("tvg-id") ?? "",
                    Logo = a.GetValueOrDefault("tvg-logo") ?? "",
                    Group = a.GetValueOrDefault("group-title") ?? "",
                    CatchupType = a.GetValueOrDefault("catchup") ?? "",
                    CatchupSource = a.GetValueOrDefault("catchup-source") ?? "",
                    ArchiveDays = ParseInt(a.GetValueOrDefault("catchup-days") ?? a.GetValueOrDefault("tvg-rec") ?? a.GetValueOrDefault("timeshift")),
                };
            }
            else if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
            {
                if (cur != null && cur.Group.Length == 0) cur.Group = line[8..].Trim();
            }
            else if (line.StartsWith('#'))
            {
                continue;
            }
            else if (cur != null)
            {
                cur.Url = line;
                cur.Number = list.Count + 1;
                if (cur.Group.Length == 0) cur.Group = "Без группы";
                list.Add(cur);
                cur = null;
            }
        }
        return (list, epgUrl);
    }

    static Dictionary<string, string> Attrs(string line)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttrRx().Matches(line)) d[m.Groups[1].Value] = m.Groups[2].Value;
        return d;
    }

    static int ParseInt(string? s) => int.TryParse(s, out var v) ? v : 0;
}
