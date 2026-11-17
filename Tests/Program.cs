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
    var recovery = new StreamRecovery();
    recovery.Begin(0);
    recovery.Buffering(false, 19_000);
    Check(!recovery.LoadExpired(19_999) && recovery.LoadExpired(20_000), "Repeated buffering cannot postpone startup timeout");
    for (int attempt = 1; attempt <= 3; attempt++)
    {
        recovery.Begin(0);
        recovery.Playing(100);
        recovery.Fail(true, 1100);
        recovery.Fail(true, 1101);
        Check(recovery.Retries == attempt && !recovery.RetryDue(3099) && recovery.RetryDue(3100),
            $"Short stream and duplicate failure schedule just retry {attempt}");
    }
    recovery.Begin(0);
    recovery.Fail(true, 100);
    Check(recovery.CanReselect && !recovery.RetryDue(10_000), "Exhausted stream waits for manual retry");
    recovery.Reset();
    recovery.Begin(0);
    recovery.Fail(true, 1000);
    recovery.Reset();
    recovery.Begin(1500);
    Check(!recovery.RetryDue(3000) && recovery.Retries == 0, "Channel switch cancels the old pending retry");
    recovery.Fail(true, 4000);
    recovery.Begin(6000);
    recovery.Playing(6100);
    recovery.Buffering(false, 36_101);
    Check(recovery.LoadExpired(56_101) && recovery.Retries == 0, "Stable playback restores budget and a later buffer stall times out");
    recovery.Playing(57_000);
    recovery.Pause(58_000);
    recovery.Buffering(false, 60_000);
    Check(!recovery.LoadExpired(100_000), "Paused channel does not time out");

    var commands = new PlayerCommandQueue(TimeSpan.FromSeconds(5));
    using var releaseFirst = new ManualResetEventSlim();
    var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var order = new List<int>();
    var first = commands.Run("first", () => { firstStarted.SetResult(); releaseFirst.Wait(); order.Add(1); });
    await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var second = commands.Run("second", () => order.Add(2));
    Check(!first.IsCompleted && !second.IsCompleted, "Player commands return without blocking caller and wait for prior command");
    releaseFirst.Set();
    await Task.WhenAll(first, second);
    Check(order.SequenceEqual(new[] { 1, 2 }), "Player commands execute in order");

    var stuck = new PlayerCommandQueue(TimeSpan.FromMilliseconds(150));
    using var unblock = new ManualResetEventSlim();
    var stuckStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var stuckExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int notifications = 0, forbiddenCalls = 0;
    stuck.Failed += () => notifications++;
    try
    {
        var hung = stuck.Run("simulated hang", () => { stuckStarted.SetResult(); unblock.Wait(); stuckExited.SetResult(); });
        await stuckStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var afterHang = stuck.Run("must not run", () => forbiddenCalls++);
        await Task.WhenAll(hung, afterHang).WaitAsync(TimeSpan.FromSeconds(5));
        Check(stuck.IsFaulted && notifications == 1 && forbiddenCalls == 0, "Timeout quarantines player without concurrent native calls");
    }
    finally { unblock.Set(); }
    await stuckExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await stuck.Run("after native recovery", () => forbiddenCalls++);
    Check(forbiddenCalls == 0, "Timed out player stays quarantined after late completion");

    var brokenQueue = new PlayerCommandQueue();
    await brokenQueue.Run("simulated error", () => throw new InvalidOperationException("test failure"));
    Check(brokenQueue.IsFaulted, "Player exception is observed and quarantines player");

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
    if (args.Contains("--player-smoke"))
    {
        var wave = Path.Combine(root, "test.wav");
        using (var writer = new BinaryWriter(File.Create(wave)))
        {
            const int bytes = 44100 * 2 * 3;
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + bytes);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(44100); writer.Write(88200);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(bytes); writer.Write(new byte[bytes]);
        }
        LibVLCSharp.Shared.Core.Initialize();
        var native = new LibVLCSharp.Shared.LibVLC("--aout=dummy", "--vout=dummy", "--no-video-title-show");
        var player = new PlayerController(native);
        try
        {
            player.PlayUrl(new Uri(wave).AbsoluteUri, 100, "", false, 50);
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!player.IsPlaying && !player.IsFaulted && DateTime.UtcNow < deadline)
            {
                player.Refresh();
                await Task.Delay(50);
            }
            Check(player.IsPlaying && !player.IsFaulted, "Native VLC playback starts through background controller");
            // Serve part of a WAV over loopback, then drop the connection before Content-Length.
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await player.StopAsync();
            player.Native.EndReached += (_, _) => ended.TrySetResult();
            player.Native.EncounteredError += (_, _) => ended.TrySetResult();
            player.Native.Playing += (_, _) => playing.TrySetResult();
            player.PlayUrl($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/live.wav", 100, "", false, 50);
            using (var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(8)))
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) is { Length: > 0 }) { }
                await Task.Delay(400);
                var bytes = File.ReadAllBytes(wave);
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.0 200 OK\r\nContent-Type: audio/wav\r\nContent-Length: {bytes.Length}\r\n\r\n"));
                await stream.WriteAsync(bytes.AsMemory(0, bytes.Length / 2));
                await playing.Task.WaitAsync(TimeSpan.FromSeconds(8));
            }
            listener.Stop();
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(12));
            Check(!player.IsFaulted, "Delayed HTTP audio starts and reports a dropped connection through VLC");
            for (int n = 0; n < 20; n++)
            {
                player.PlayUrl(new Uri(wave).AbsoluteUri, 100, "", false, 50);
                player.Volume = n + 20;
                player.Mute = n % 2 == 0;
                player.Refresh();
            }
            player.Pause();
            await player.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(!player.IsFaulted && !player.IsPlaying, "Rapid native switching, volume, pause and stop complete without collision");
        }
        finally { await player.CloseAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        Check(!player.IsFaulted, "Native shutdown completes");
    }
    Console.WriteLine($"Passed: {passed}");
}
finally { Directory.Delete(root, true); }

sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
}
