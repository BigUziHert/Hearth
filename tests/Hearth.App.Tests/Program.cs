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
        FluentScrollBars.Apply(app.Resources);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            CheckProfiles(Path.Combine(taskRoot, "migration"));
            CheckStartup();
            var hostingChecks = CheckHostingSetup(Path.Combine(taskRoot, "hosting"));
            Await(() => hostingChecks.IsCompleted, "Hosting setup regression scenarios complete");
            hostingChecks.GetAwaiter().GetResult();
            CheckWindows(Path.Combine(taskRoot, "windows"));
            OwnerFlowChecks.Run(Path.Combine(taskRoot, "owner-flow"), Check, Await);
            HostingMigrationFlowChecks.Run(Path.Combine(taskRoot, "migration-flow"), Check);
            var clientIdentity = ClientIdentityChecks.RunAsync(Path.Combine(taskRoot, "client-identity"), Check);
            Await(() => clientIdentity.IsCompleted, "Secure invite reconnection scenarios complete");
            clientIdentity.GetAwaiter().GetResult();
            var networkChecks = HostNetworkChecks.RunAsync(Check);
            Await(() => networkChecks.IsCompleted, "Automatic network setup scenarios complete");
            networkChecks.GetAwaiter().GetResult();
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
        Check(!store.State.CloseToTray && !new ProfileState().CloseToTray, "New and legacy profiles keep close-to-tray off until enabled");
        Check(File.ReadAllBytes(path).SequenceEqual(before), "Reading a legacy profile does not rewrite its encrypted bytes");
        Check(store.State.Packs.Single().Terrain == TerrainMode.Voxy && store.State.Packs.Single().Token == "local-test-token", "Existing pack credential and terrain choice survive legacy profile loading");
        Check(store.State.DisplayName == "Test player" && store.State.PrismExecutable == "fixture-prism.exe" && store.State.SelectedPackHost == host.Url && store.State.SelectedPackId == "fixture-pack", "Player preferences survive a legacy custom-source profile");
        Check(store.State.Hosts.Single() == host, "Host connection and owner credential survive legacy profile loading");
        store.State.DisplayName = "Renamed player"; store.State.CloseToTray = true; store.Save();
        var reloaded = new ProfileStore(taskRoot);
        Check(reloaded.State.DisplayName == "Renamed player", "Ordinary profile changes still save after loading legacy settings");
        Check(reloaded.State.CloseToTray, "Close-to-tray preference survives encrypted save and reload");
        var decoded = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
        using var document = JsonDocument.Parse(decoded);
        Check(!document.RootElement.TryGetProperty("updateRepository", out _) && !document.RootElement.TryGetProperty("effectiveUpdateRepository", out _) && !document.RootElement.TryGetProperty("usesOfficialUpdateRepository", out _), "Legacy custom update source is ignored and omitted on the next profile save");
        Check(reloaded.State.Packs.Single().Terrain == TerrainMode.Voxy && reloaded.State.Packs.Single().Token == "local-test-token" && reloaded.State.Hosts.Single() == host, "Existing pack and host preferences survive encrypted save and reload");
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("local-test-token", StringComparison.Ordinal), "Saved profile does not expose the member credential as plaintext");
    }
    private static void CheckStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "Hs-" + Guid.NewGuid().ToString("N")[..8]);
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using var before = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey);
        var normalBefore = before?.GetValue("Hearth");
        try
        {
            Check(!WindowsStartup.IsEnabled(root), "Windows startup is opt-in for a new profile");
            var command = WindowsStartup.BuildCommand(@"C:\Profiles\Test player", @"C:\Program Files\Hearth\Hearth.App.exe");
            Check(command == "\"C:\\Program Files\\Hearth\\Hearth.App.exe\" --startup --profile \"C:\\Profiles\\Test player\"", "Startup command quotes spaced paths and passes the selected profile");
            Check(WindowsStartup.GetValueName(root) != "Hearth" && WindowsStartup.GetValueName(root) == WindowsStartup.GetValueName(root + "\\"), "Test startup registration is separate and normalizes trailing separators");
            WindowsStartup.SetEnabled(root, true);
            Check(WindowsStartup.IsEnabled(root), "Enabling startup registers the executable for the current Windows user");
            WindowsStartup.SetEnabled(root, false);
            Check(!WindowsStartup.IsEnabled(root), "Disabling startup removes the profile's registration");
            using var after = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey);
            Check(Equals(normalBefore, after?.GetValue("Hearth")), "Startup testing leaves the real player's registration unchanged");
        }
        finally { WindowsStartup.SetEnabled(root, false); }
    }
    private static async Task CheckHostingSetup(string taskRoot)
    {
        var store = new ProfileStore(taskRoot);
        const string url = "http://127.0.0.1:17842";
        var directory = Path.Combine(taskRoot, "Host");
        var other = new HostConnection("other", "Other host", "http://127.0.0.1:19123", "other-key");
        store.State.Hosts.Add(other); store.Save();
        string pendingKey = "";
        Task Cancel(string key, CancellationToken cancellation)
        {
            pendingKey = key;
            Check(new ProfileStore(taskRoot).State.LocalHostOwnerToken == key, "Owner key is durable before host initialization");
            Check(!store.State.LocalHostingEnabled && store.State.Hosts.SequenceEqual([other]), "Starting a host does not prematurely enable it or add a connection");
            throw new OperationCanceledException();
        }
        try { await LocalHostingSetup.EnableAsync(store, url, directory, Cancel); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        Check(!new ProfileStore(taskRoot).State.LocalHostingEnabled && store.State.Hosts.SequenceEqual([other]), "Canceled startup leaves hosting disabled without a broken host entry");
        try { await LocalHostingSetup.EnableAsync(store, url, directory, (_, _) => throw new IOException("failed")); throw new Exception("Expected failure"); }
        catch (IOException) { }
        Check(new ProfileStore(taskRoot).State.LocalHostOwnerToken == pendingKey && store.State.Hosts.Count == 1, "Failed startup preserves its recovery key without adding a host");
        var ready = await LocalHostingSetup.EnableAsync(store, url, directory, (key, _) =>
        {
            Check(key == pendingKey, "Retry reuses the original owner key"); return Task.CompletedTask;
        });
        Check(store.State.LocalHostingEnabled && store.State.LocalHostConnectionId == ready.Id && store.State.Hosts.Count == 2, "Verified startup enables hosting and adds exactly one connection");
        await LocalHostingSetup.EnableAsync(store, url, directory, (_, _) => Task.CompletedTask);
        Check(store.State.Hosts.Count == 2 && new ProfileStore(taskRoot).State.LocalHostConnectionId == ready.Id, "Repeated startup reuses the saved connection");
        var encrypted = File.ReadAllBytes(Path.Combine(taskRoot, "profile.bin"));
        Check(!Encoding.UTF8.GetString(encrypted).Contains(pendingKey), "Pending and active host credentials remain encrypted");

        var legacy = new ProfileStore(Path.Combine(taskRoot, "legacy"));
        var oldHost = new HostConnection("old", "This PC", url, "original-owner-key");
        legacy.State.Hosts.Add(oldHost);
        await LocalHostingSetup.EnableAsync(legacy, url, directory, (key, _) =>
        {
            Check(key == oldHost.OwnerToken, "Existing saved hosts retain their original owner credential"); return Task.CompletedTask;
        });
        Check(legacy.State.Hosts.Single().Id == oldHost.Id, "Existing host migration does not duplicate its connection");
        var lost = new ProfileStore(Path.Combine(taskRoot, "lost"));
        Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "host.json"), "{}");
        try { await LocalHostingSetup.EnableAsync(lost, url, directory, (_, _) => throw new Exception("Must not start")); throw new Exception("Expected missing key failure"); }
        catch (InvalidOperationException) { }
        Check(lost.State.LocalHostOwnerToken == "" && lost.State.Hosts.Count == 0, "Existing data without its owner key is never reinitialized with a new key");

        var migrated = new ProfileStore(Path.Combine(taskRoot, "migrated"));
        migrated.State.Hosts.Add(oldHost);
        var descriptor = Path.Combine(taskRoot, "retired-host.json");
        File.WriteAllText(descriptor, JsonSerializer.Serialize(new { formatVersion = 1, ownerSid = "fixture-sid", dataDirectory = directory, port = 17842, wasRunning = false, wasEnabled = true }));
        LegacyHostingMigration.AdoptDescriptor(migrated, descriptor, "different-sid");
        Check(!migrated.State.LegacyHostingMigrated, "Legacy data adoption is restricted to the original Windows user");
        LegacyHostingMigration.AdoptDescriptor(migrated, descriptor, "fixture-sid");
        var reloaded = new ProfileStore(migrated.Root).State;
        Check(reloaded.LocalHostingEnabled && reloaded.LocalHostDataDirectory == directory && reloaded.LocalHostOwnerToken == oldHost.OwnerToken && reloaded.Hosts.Single() == oldHost,
            "Service migration adopts its original data, port, owner key, and enabled preference");
        migrated.State.LocalHostingEnabled = false; migrated.Save();
        LegacyHostingMigration.AdoptDescriptor(migrated, descriptor, "fixture-sid");
        Check(!migrated.State.LocalHostingEnabled, "Reopening after migration respects the user's disabled preference");
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
            var options = Descendants(empty).OfType<CheckBox>().ToArray();
            Check(options.Length == 2 && options.All(option => option.IsChecked == false), "Settings exposes both startup and closing choices, initially off");
            var closeOption = options.Single(option => Equals(option.Content, "Close to system tray"));
            closeOption.IsChecked = true; closeOption.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Check(new ProfileStore(Path.Combine(taskRoot, "empty")).State.CloseToTray, "Close-to-tray setting saves immediately from Settings");
            closeOption.IsChecked = false; closeOption.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
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
