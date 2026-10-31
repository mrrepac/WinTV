using System.IO.Compression;
using System.Net;
using WinTV;
using WinTV.Setup;

var root = Path.Combine(Path.GetTempPath(), "WinTV_Tests_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
const string valid = "#EXTM3U\n#EXTINF:-1,Test\nhttps://example.test/live\n";
int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS " + name);
    passed++;
}
try
{
    var settings = Path.Combine(root, "settings.json");
    AtomicFile.Write(settings, "old");
    AtomicFile.Write(settings, "new", settings + ".bak");
    Check(File.ReadAllText(settings) == "new" && File.ReadAllText(settings + ".bak") == "old", "Atomic replacement preserves backup");

    var cache = Path.Combine(root, "playlist.m3u");
    File.WriteAllText(cache, valid);
    using var html = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>error</html>") })));
    var result = await PlaylistLoader.LoadAsync("https://example.test/list", cache, html, default);
    Check(result.Channels.Count == 1 && result.Warning != null && File.ReadAllText(cache) == valid, "HTTP 200 HTML preserves valid cache");

    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var slow = new HttpClient(new Handler(async (_, ct) => {
        started.SetResult();
        await Task.Delay(Timeout.Infinite, ct);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }));
    using var cts = new CancellationTokenSource();
    var pending = PlaylistLoader.LoadAsync("https://example.test/list", cache, slow, cts.Token);
    await started.Task;
    cts.Cancel();
    bool cancelled = false;
    try { await pending; } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled && File.ReadAllText(cache) == valid, "Cancelled load does not return cached or stale data");

    using var good = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(valid) })));
    var blockedCache = Path.Combine(root, "directory");
    Directory.CreateDirectory(blockedCache);
    result = await PlaylistLoader.LoadAsync("https://example.test/list", blockedCache, good, default);
    Check(result.Channels.Count == 1 && result.Warning != null, "Cache write failure still returns downloaded channels");

    const string epgUrl = "https://example.test/epg";
    const string xml = "<tv><channel id=\"test\"><display-name>Test</display-name></channel></tv>";
    using var epgGood = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml) })));
    var epgFile = await Epg.EnsureFileAsync(epgUrl, epgGood, default, root);
    Check(File.ReadAllText(epgFile) == xml, "Valid XMLTV is cached");
    var oldTime = DateTime.Now.AddDays(-1);
    File.SetLastWriteTime(epgFile, oldTime);
    await Epg.EnsureFileAsync(epgUrl, html, default, root);
    Check(File.ReadAllText(epgFile) == xml && File.GetLastWriteTime(epgFile) < DateTime.Now.AddHours(-12), "HTML EPG preserves old cache and retry age");
    using var broken = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<tv><programme>") })));
    await Epg.EnsureFileAsync(epgUrl, broken, default, root);
    Check(File.ReadAllText(epgFile) == xml, "Truncated XMLTV preserves cache");
    bool invalidEpgRejected = false;
    try { await Epg.EnsureFileAsync(epgUrl + "-new", html, default, root); }
    catch (InvalidDataException) { invalidEpgRejected = true; }
    Check(invalidEpgRejected, "Invalid EPG without cache is rejected");
    using var gzBytes = new MemoryStream();
    using (var gz = new GZipStream(gzBytes, CompressionMode.Compress, true))
    using (var writer = new StreamWriter(gz)) writer.Write(xml);
    using var gzHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(gzBytes.ToArray()) })));
    var gzFile = await Epg.EnsureFileAsync(epgUrl + "-gzip", gzHttp, default, root);
    Check(File.ReadAllBytes(gzFile).SequenceEqual(gzBytes.ToArray()), "Gzip XMLTV is validated and cached");

    var target = Path.Combine(root, "install");
    Directory.CreateDirectory(target);
    File.WriteAllText(Path.Combine(target, "WinTV.exe"), "old");
    using var zipBytes = new MemoryStream();
    using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, true))
        foreach (var name in new[] { "WinTV/WinTV.exe", "WinTV/new.dll" })
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write("new");
        }
    zipBytes.Position = 0;
    using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Read, true))
    {
        bool failed = false;
        try { PayloadInstaller.Apply(zip, target, pct => { if (pct == 100) throw new IOException("Injected failure"); }); }
        catch (IOException) { failed = true; }
        Check(failed && File.ReadAllText(Path.Combine(target, "WinTV.exe")) == "old" && !File.Exists(Path.Combine(target, "new.dll")), "Failed installation restores old files and removes new files");
        PayloadInstaller.Apply(zip, target, null);
        Check(File.ReadAllText(Path.Combine(target, "WinTV.exe")) == "new", "Successful installation applies payload");
    }
    using var badBytes = new MemoryStream();
    using (var zip = new ZipArchive(badBytes, ZipArchiveMode.Create, true)) zip.CreateEntry("../escape.txt");
    badBytes.Position = 0;
    using (var zip = new ZipArchive(badBytes, ZipArchiveMode.Read))
    {
        bool rejected = false;
        try { PayloadInstaller.Apply(zip, target, null); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && !File.Exists(Path.Combine(root, "escape.txt")), "Archive traversal rejected");
    }
    Console.WriteLine($"Passed: {passed}");
}
finally { Directory.Delete(root, true); }

sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
}
