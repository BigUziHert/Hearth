using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using Hearth.App;
using Hearth.Core;

internal static class Program
{
    private static int checks;
    private static void Check(bool passed, string description)
    {
        if (!passed) throw new InvalidOperationException(description);
        checks++; Console.WriteLine("PASS " + description);
    }
    [STAThread]
    private static void Main()
    {
        var taskRoot = Path.Combine(Path.GetTempPath(), "Hearth-app-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(taskRoot);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var sourceRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (sourceRoot != null && !File.Exists(Path.Combine(sourceRoot.FullName, "Hearth.sln"))) sourceRoot = sourceRoot.Parent;
        if (sourceRoot == null) throw new IOException("Run the component suite from its source checkout.");
        var theme = XDocument.Load(Path.Combine(sourceRoot.FullName, "src", "Hearth.App", "App.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new XElement(xaml + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"), theme.Root!.Element(xaml + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            CheckProfiles(Path.Combine(taskRoot, "migration"));
            CheckWindows(Path.Combine(taskRoot, "windows"));
            Console.WriteLine($"PASS: {checks} profile and WPF component checks.");
        }
        finally { app.Shutdown(); Directory.Delete(taskRoot, recursive: true); }
    }
    private static void CheckProfiles(string taskRoot)
    {
        Directory.CreateDirectory(taskRoot);
        Check(!JsonSerializer.Serialize(new ProfileState(), JsonDefaults.Options).Contains("updateRepository", StringComparison.OrdinalIgnoreCase), "New profiles do not store an update-source override");
        var host = new HostConnection("fixture-host", "Fixture host", "http://127.0.0.1:17842", "local-owner-token");
        var oldProfile = new { displayName = "Test player", updateRepository = "someone/custom", prismExecutable = "fixture-prism.exe", selectedPackHost = host.Url, selectedPackId = "fixture-pack", hosts = new[] { host }, packs = new[] { new JoinedPack(host.Url, "fixture-pack", "Fixture pack", "local-test-token", "member", TerrainMode.Voxy) } };
        var legacyBytes = JsonSerializer.SerializeToUtf8Bytes(oldProfile, JsonDefaults.Options);
        var path = Path.Combine(taskRoot, "profile.bin");
        File.WriteAllBytes(path, ProtectedData.Protect(legacyBytes, null, DataProtectionScope.CurrentUser));
        var before = File.ReadAllBytes(path);
        var store = new ProfileStore(taskRoot);
        Check(File.ReadAllBytes(path).SequenceEqual(before), "Reading a legacy profile does not rewrite its encrypted bytes");
        Check(store.State.Packs.Single().Terrain == TerrainMode.Voxy && store.State.Packs.Single().Token == "local-test-token", "Existing pack credential and terrain choice survive legacy profile loading");
        Check(store.State.DisplayName == "Test player" && store.State.PrismExecutable == "fixture-prism.exe" && store.State.SelectedPackHost == host.Url && store.State.SelectedPackId == "fixture-pack", "Player preferences survive a legacy custom-source profile");
        Check(store.State.Hosts.Single() == host, "Host connection and owner credential survive legacy profile loading");
        store.State.DisplayName = "Renamed player"; store.Save();
        var reloaded = new ProfileStore(taskRoot);
        Check(reloaded.State.DisplayName == "Renamed player", "Ordinary profile changes still save after loading legacy settings");
        var decoded = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
        using var document = JsonDocument.Parse(decoded);
        Check(!document.RootElement.TryGetProperty("updateRepository", out _) && !document.RootElement.TryGetProperty("effectiveUpdateRepository", out _) && !document.RootElement.TryGetProperty("usesOfficialUpdateRepository", out _), "Legacy custom update source is ignored and omitted on the next profile save");
        Check(reloaded.State.Packs.Single().Terrain == TerrainMode.Voxy && reloaded.State.Packs.Single().Token == "local-test-token" && reloaded.State.Hosts.Single() == host, "Existing pack and host preferences survive encrypted save and reload");
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("local-test-token", StringComparison.Ordinal), "Saved profile does not expose the member credential as plaintext");
    }
    private static T Named<T>(MainWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static void Click(MainWindow window, string name) => Named<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static string Primary(MainWindow window) => AutomationProperties.GetName(Named<Button>(window, "PlayButton"));
    private static void Capture(MainWindow window, string name, double width = 1280, double height = 850)
    {
        var output = Environment.GetEnvironmentVariable("HEARTH_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var image = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32); image.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        var version = typeof(MainWindow).Assembly.GetName().Version!.ToString(3);
        using var file = File.Create(Path.Combine(output, version + "-" + name + ".png")); encoder.Save(file);
    }
    private static void Await(Func<bool> condition, string description)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        Check(condition(), description);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void CheckWindows(string taskRoot)
    {
        var empty = new MainWindow(new ProfileStore(Path.Combine(taskRoot, "empty")));
        try
        {
            Check(empty.WindowStyle == WindowStyle.SingleBorderWindow && empty.ResizeMode == ResizeMode.CanResize, "Main window retains native Windows resize and title-bar controls");
            Check(Primary(empty) == "Add modpack" && Named<Button>(empty, "PlayButton").IsEnabled, "Empty library offers Add modpack as its primary action");
            Check(Named<Border>(empty, "TerrainPanel").Visibility == Visibility.Collapsed && Named<Border>(empty, "PrismNotice").Visibility == Visibility.Collapsed, "Empty library hides terrain and premature Prism setup");
            Capture(empty, "empty"); Capture(empty, "empty-min", 1000, 720);
            Click(empty, "PlayButton");
            Check(Named<TextBlock>(empty, "PageTitle").Text == "Add modpack", "Empty primary action opens the invite form");
            Capture(empty, "join");
            Click(empty, "HostsNav");
            Check(Named<TextBlock>(empty, "PageTitle").Text == "Hosting" && Named<FrameworkElement>(empty, "LibraryView").Visibility == Visibility.Collapsed, "Hosting is directly reachable from the sidebar");
            Check(((SolidColorBrush)Named<Button>(empty, "HostsNav").Background).Color != ((SolidColorBrush)Named<Button>(empty, "PublishNav").Background).Color, "Hosting route visibly selects its own navigation item");
            Capture(empty, "hosting");
            Click(empty, "SettingsNav");
            Check(Descendants(empty).OfType<TextBlock>().Any(text => text.Text == "Official Hearth releases") && Descendants(empty).OfType<Button>().Count(button => button.Content is string text && text == "Check for updates") == 1, "Settings shows one update action for official Hearth releases");
            Capture(empty, "settings");
        }
        finally { empty.Close(); }
        using var fixture = new LibraryFixture();
        var profile = new ProfileStore(Path.Combine(taskRoot, "player"));
        var pack = new JoinedPack(fixture.Url, "fixture-pack", "Fixture pack", "local-test-token", "member");
        profile.State.Packs.Add(pack);
        var window = new MainWindow(profile);
        try
        {
            Await(() => Primary(window) == "Check approval", "Pending membership offers Check approval instead of Play");
            Capture(window, "pending");
            Check(!Named<StackPanel>(window, "TerrainSelector").IsEnabled && fixture.ManifestRequests == 0, "Pending membership neither offers terrain changes nor requests protected release metadata");
            fixture.Access = "approved";
            Click(window, "PlayButton");
            Await(() => Primary(window) == "Set up Prism", "Approved pack without Prism offers setup as the next action");
            Check(Named<RadioButton>(window, "TerrainVoxy").IsEnabled && !Named<RadioButton>(window, "TerrainDh").IsEnabled, "Approved manifest enables only terrain options supplied by its owner");
            var prismPath = Path.Combine(taskRoot, "prismlauncher.exe"); File.WriteAllText(prismPath, "fixture only; never executed");
            profile.State.PrismExecutable = prismPath;
            Click(window, "LibraryNav");
            Await(() => Primary(window) == "Play", "Approved pack with Prism offers Play");
            Capture(window, "player");
            Named<RadioButton>(window, "TerrainVoxy").IsChecked = true;
            Check(new ProfileStore(profile.Root).State.Packs.Single().Terrain == TerrainMode.Voxy, "Terrain selection persists in the encrypted per-pack profile");
            fixture.Access = "removed"; Click(window, "LibraryNav");
            Await(() => Primary(window) == "Request access", "Removed membership offers a fresh access request");
            Click(window, "PlayButton");
            Check(Named<TextBlock>(window, "PageTitle").Text == "Add modpack", "Removed member action opens the invite form");
            fixture.Access = "approved"; fixture.ManifestStatus = 404; Click(window, "LibraryNav");
            Await(() => Primary(window) == "Check again", "Approved unpublished pack offers Check again");
            Check(Named<TextBlock>(window, "PackStatusText").Text == "No release yet", "Unpublished pack explains the owner must publish first");
            var installation = Path.Combine(new PackSynchronizer(profile.Root).GetInstanceDirectory(pack.HostUrl, pack.PackId), ".hearth");
            Directory.CreateDirectory(installation);
            File.WriteAllText(Path.Combine(installation, "installed.json"), JsonSerializer.Serialize(new InstalledPack(pack.HostUrl, pack.PackId, "fixture-release", TerrainMode.Off, [], fixture.Manifest), JsonDefaults.Options));
            fixture.AccessStatus = 503; Click(window, "LibraryNav");
            Await(() => Primary(window) == "Play installed", "Unavailable host with a complete local installation offers Play installed");
            Check(Named<TextBlock>(window, "PackStatusHint").Text.Contains("No update or terrain change") && !Named<StackPanel>(window, "TerrainSelector").IsEnabled, "Offline action explains the unchanged installed copy and disables terrain editing");
        }
        finally { window.Close(); }
    }
}

internal sealed class LibraryFixture : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cancellation = new();
    public string Url { get; }
    public string Access = "pending";
    public int AccessStatus = 200;
    public int ManifestStatus = 200;
    public int ManifestRequests;
    public PackManifest Manifest { get; } = new("fixture-pack", "fixture-release", "Fixture pack", "1.21.10", "fabric", "0.18.1", [new PackFile("mods/voxy.jar", new string('a', 64), 12, "voxy")]);
    public LibraryFixture()
    {
        listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(Accept);
    }
    private async Task Accept()
    {
        try { while (!cancellation.IsCancellationRequested) _ = Respond(await listener.AcceptTcpClientAsync(cancellation.Token)); }
        catch (OperationCanceledException) { }
        catch (SocketException) when (cancellation.IsCancellationRequested) { }
    }
    private async Task Respond(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var request = await reader.ReadLineAsync(cancellation.Token) ?? "";
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellation.Token))) { }
                var manifest = request.Contains("/manifest ", StringComparison.Ordinal);
                if (manifest) Interlocked.Increment(ref ManifestRequests);
                var status = manifest ? ManifestStatus : AccessStatus;
                var body = status == 200 ? (manifest ? JsonSerializer.Serialize(Manifest, JsonDefaults.Options) : JsonSerializer.Serialize(new AccessResponse(Access, "player", "Fixture pack"), JsonDefaults.Options)) : "{\"error\":\"Fixture unavailable\"}";
                var bytes = Encoding.UTF8.GetBytes(body);
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, cancellation.Token); await stream.WriteAsync(bytes, cancellation.Token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }
    public void Dispose() { cancellation.Cancel(); listener.Stop(); cancellation.Dispose(); }
}
