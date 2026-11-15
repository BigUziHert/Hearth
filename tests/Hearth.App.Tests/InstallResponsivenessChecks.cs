using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Hearth.Core;

internal static class InstallResponsivenessChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        var task = RunAsync(root, check); var deadline = Stopwatch.StartNew();
        // Windows CI storage flushes can be slower than a desktop SSD. This is only
        // the total completion deadline; UI heartbeat and cancel bounds stay strict.
        while (!task.IsCompleted && deadline.Elapsed < TimeSpan.FromMinutes(2))
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        check(task.IsCompleted, "The 150-mod installation and verification responsiveness checks finish");
        task.GetAwaiter().GetResult();
    }

    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var fixture = await Task.Run(async () =>
        {
            var sources = Path.Combine(root, "source-jars"); Directory.CreateDirectory(sources);
            var paths = new Dictionary<string, string>(); var files = new List<PackFile>();
            var block = new byte[1024 * 1024];
            for (var index = 0; index < 150; index++)
            {
                var path = Path.Combine(sources, $"mod-{index:000}.jar");
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    using (var metadata = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open()))
                        metadata.Write($"{{\"schemaVersion\":1,\"id\":\"responsiveness_fixture_{index}\",\"version\":\"1.0\"}}");
                    using var payload = archive.CreateEntry("synthetic-payload", CompressionLevel.NoCompression).Open();
                    payload.Write(block); payload.Write(block);
                }
                var file = await ModFiles.InspectAsync(path); files.Add(file); paths.Add(file.Sha256, path);
            }
            return (Manifest: new PackManifest("large-fixture", "first-release", "150 mod responsiveness fixture", "1.21.1", "fabric", "0.16.10", files), Paths: paths);
        });
        check(fixture.Manifest.Files.Count == 150 && fixture.Manifest.Files.Sum(f => f.Size) >= 300L * 1024 * 1024,
            "The responsiveness fixture contains 150 distinct valid synthetic JARs totaling at least 300 MiB");
        var profile = Path.Combine(root, "profile"); var sync = new PackSynchronizer(profile);
        var pack = new JoinedPack("http://127.0.0.1:1", fixture.Manifest.PackId, fixture.Manifest.Name, "fixture-member", "fixture-member");
        using var http = new HttpClient(new FixtureHandler(fixture.Manifest, fixture.Paths));
        using var api = new HearthApi(pack.HostUrl, pack.Token, http);
        var uiThread = Environment.CurrentManagedThreadId;
        var phases = new HashSet<string>(); var progressOnUi = true;
        var progress = new Progress<SyncProgress>(value =>
        {
            progressOnUi &= Environment.CurrentManagedThreadId == uiThread;
            phases.Add(value.Message.StartsWith("Downloading ", StringComparison.Ordinal) ? "Downloading files" : value.Message.Split('…')[0]);
        });
        using var installPulse = new Heartbeat();
        var call = Stopwatch.StartNew(); var installation = sync.SyncAsync(api, pack, progress); var callMs = call.Elapsed.TotalMilliseconds;
        var installed = await installation;
        await Task.Delay(35); installPulse.Stop();
        check(installed.DownloadedFiles == 150 && installPulse.Ticks > 0 && installPulse.MaxGap.TotalMilliseconds < 1200 && callMs < 250,
            "A 150-mod / 300 MiB first installation keeps the WPF dispatcher responsive through hashing, ZIP reads, copying, and journal writes");
        check(progressOnUi && phases.Contains("Verifying files") && phases.Contains("Downloading files")
            && phases.Contains("Checking mod identities") && phases.Contains("Preparing update") && phases.Contains("Applying the verified release"),
            "Installation progress returns to the UI thread and reports verification and applying phases instead of a stale last-download label");

        using var verifyPulse = new Heartbeat();
        var verify = Stopwatch.StartNew(); var verification = sync.VerifyInstalledAsync(pack.InstallationUrl, pack.PackId); var verifyCallMs = verify.Elapsed.TotalMilliseconds;
        var verified = await verification;
        await Task.Delay(35); verifyPulse.Stop();
        check(verified.Files.Count == 150 && verifyPulse.Ticks > 0 && verifyPulse.MaxGap.TotalMilliseconds < 1200 && verifyCallMs < 250,
            "Verifying the complete 300 MiB installation yields to the WPF dispatcher");
        using var cancellation = new CancellationTokenSource();
        var cancelTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(5) };
        cancelTimer.Tick += (_, _) => { cancelTimer.Stop(); cancellation.Cancel(); };
        var cancelWatch = Stopwatch.StartNew(); cancelTimer.Start();
        try { await sync.VerifyInstalledAsync(pack.InstallationUrl, pack.PackId, cancellation.Token); throw new InvalidOperationException("Active verification completed before cancellation could be observed"); }
        catch (OperationCanceledException)
        {
            check(cancellation.IsCancellationRequested && cancelWatch.Elapsed < TimeSpan.FromSeconds(2),
                "Cancel dispatched from the UI interrupts active installation verification promptly");
        }
        finally { cancelTimer.Stop(); }
        check(sync.GetInstalled(pack.InstallationUrl, pack.PackId)?.ReleaseId == installed.ReleaseId,
            "Canceled verification leaves the complete installed release intact");
        Console.WriteLine("RESPONSIVENESS " + JsonSerializer.Serialize(new
        {
            mods = 150, bytes = fixture.Manifest.Files.Sum(f => f.Size),
            installMaxUiGapMs = Math.Round(installPulse.MaxGap.TotalMilliseconds, 2), installCallMs = Math.Round(callMs, 2),
            verifyMaxUiGapMs = Math.Round(verifyPulse.MaxGap.TotalMilliseconds, 2), verifyCallMs = Math.Round(verifyCallMs, 2),
            cancelMs = Math.Round(cancelWatch.Elapsed.TotalMilliseconds, 2), phases = phases.ToArray()
        }));
    }

    private sealed class Heartbeat : IDisposable
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly DispatcherTimer timer = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(15) };
        private TimeSpan previous;
        public TimeSpan MaxGap { get; private set; }
        public int Ticks { get; private set; }
        public Heartbeat() { timer.Tick += (_, _) => { Observe(); Ticks++; }; timer.Start(); }
        private void Observe() { var now = watch.Elapsed; if (now - previous > MaxGap) MaxGap = now - previous; previous = now; }
        public void Stop() { Observe(); timer.Stop(); }
        public void Dispose() => timer.Stop();
    }

    private sealed class FixtureHandler(PackManifest manifest, Dictionary<string, string> sources) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var route = request.RequestUri!.AbsolutePath;
            HttpContent content;
            if (route.EndsWith("/manifest", StringComparison.Ordinal)) content = new StringContent(JsonSerializer.Serialize(manifest, JsonDefaults.Options), Encoding.UTF8, "application/json");
            else if (sources.TryGetValue(route.Split('/')[^1], out var path))
            {
                content = new StreamContent(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous));
                content.Headers.ContentLength = new FileInfo(path).Length;
            }
            else throw new InvalidOperationException("Unexpected fixture request: " + route);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
