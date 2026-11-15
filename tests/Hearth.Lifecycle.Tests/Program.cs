using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using Hearth.App;
using Hearth.Core;
using LauncherApp = Hearth.App.App;

internal static class Program
{
    private static int checks;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--diagnostics"))
            AppDomain.CurrentDomain.FirstChanceException += (_, error) => Console.Error.WriteLine(error.Exception.GetType().FullName + Environment.NewLine + error.Exception.StackTrace);
        if (args.Contains("--activate-only")) return RunActivation();
        var scenario = ValueAfter(args, "--scenario");
        if (scenario != null) return RunScenario(scenario, ValueAfter(args, "--profile")!);
        var taskRoot = Path.Combine(Path.GetTempPath(), "Hearth-lifecycle-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(taskRoot);
        try
        {
            OwnedHostChecks.RunAsync(Path.Combine(taskRoot, "runtime"), Check).GetAwaiter().GetResult();
            SecureHostingLifecycleChecks.RunAsync(Path.Combine(taskRoot, "secure-runtime"), Check).GetAwaiter().GetResult();
            foreach (var name in new[] { "normal-close", "tray-restore", "startup-tray", "startup-visible", "quit-busy", "session-ending" })
            {
                var root = Path.Combine(taskRoot, name);
                var profile = new ProfileStore(root);
                profile.State.CloseToTray = name != "normal-close" && name != "startup-visible";
                profile.State.LegacyHostingMigrated = true;
                profile.Save();
                var childArgs = new List<string> { "--scenario", name, "--profile", root };
                if (name.StartsWith("startup-", StringComparison.Ordinal)) childArgs.Add("--startup");
                using var child = StartSelf(childArgs);
                var output = child.StandardOutput.ReadToEndAsync();
                var errors = child.StandardError.ReadToEndAsync();
                if (!child.WaitForExit(30000)) { child.Kill(entireProcessTree: true); throw new TimeoutException(name + " did not exit."); }
                Console.Write(output.GetAwaiter().GetResult());
                if (child.ExitCode != 0) throw new InvalidOperationException(name + ": " + errors.GetAwaiter().GetResult());
            }
            foreach (var name in new[] { "host-window-close", "host-close-busy", "host-invalid-settings", "host-retry", "host-tray-quit", "host-startup-tray", "host-disabled", "host-persistence-first", "host-persistence-reopen", "host-persistence-disabled" })
            {
                var root = Path.Combine(taskRoot, name.StartsWith("host-persistence-", StringComparison.Ordinal) ? "host-persistence" : name);
                if (name is not "host-persistence-reopen" and not "host-persistence-disabled") PrepareHostingProfile(root, name);
                var childArgs = new List<string> { "--scenario", name, "--profile", root };
                if (name == "host-startup-tray") childArgs.Add("--startup");
                using var child = StartSelf(childArgs);
                var output = child.StandardOutput.ReadToEndAsync();
                var errors = child.StandardError.ReadToEndAsync();
                if (!child.WaitForExit(40000)) { child.Kill(entireProcessTree: true); throw new TimeoutException(name + " did not exit."); }
                Console.Write(output.GetAwaiter().GetResult());
                if (child.ExitCode != 0) throw new InvalidOperationException(name + ": " + errors.GetAwaiter().GetResult());
                var persisted = new ProfileStore(root);
                if (persisted.State.LocalHostingPort >= 1024)
                    Check(!OwnedHostChecks.PortOpenAsync(persisted.State.LocalHostingPort).GetAwaiter().GetResult(), name + ": the launcher exit closes the host listener");
            }
            CheckCrashCleanupAsync(Path.Combine(taskRoot, "host-crash")).GetAwaiter().GetResult();
            Console.WriteLine("PASS: 17 isolated launcher lifecycle scenarios and app-owned host runtime checks.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            var resolved = Path.GetFullPath(taskRoot);
            if (Path.GetDirectoryName(resolved)!.Equals(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(resolved).StartsWith("Hearth-lifecycle-tests-", StringComparison.Ordinal))
                Directory.Delete(resolved, recursive: true);
        }
    }

    private static int RunActivation()
    {
        var app = new LauncherApp(); app.InitializeComponent(); return app.Run();
    }

    private static int RunScenario(string scenario, string profileRoot)
    {
        var app = new FixtureApp();
        Exception? failure = null;
        var exerciseCompleted = false;
        var operationStopped = false;
        int? ownedProcess = null;
        string? ownedUrl = null;
        app.Exit += (_, _) =>
        {
            try
            {
                Check(app.IsExiting && !app.TrayAvailable, scenario + ": exit disposes the tray icon");
                if (scenario is "quit-busy" or "host-close-busy") Check(operationStopped, "Exiting waits for an in-progress operation to stop safely");
            }
            catch (Exception error) { failure = error; }
        };
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            try
            {
                var window = (MainWindow)app.MainWindow;
                if (scenario.StartsWith("host-", StringComparison.Ordinal))
                {
                    exerciseCompleted = true; // Close can end the dispatcher before an await continuation runs.
                    await ExerciseHostingAsync(scenario, profileRoot, app, window, (process, url) => { ownedProcess = process; ownedUrl = url; }, () => operationStopped = true);
                    return;
                }
                switch (scenario)
                {
                    case "normal-close":
                    case "startup-visible":
                        Check(window.IsVisible && !app.TrayAvailable, scenario + ": launcher opens normally when tray mode is off");
                        exerciseCompleted = true;
                        window.Close(); // Must end Run without an explicit Shutdown call.
                        break;
                    case "tray-restore":
                    case "startup-tray":
                        Check(app.TrayAvailable, scenario + ": real tray icon is available");
                        if (scenario == "startup-tray") Check(!window.IsVisible, "Windows startup opens quietly when tray mode is on");
                        else
                        {
                            window.WindowState = WindowState.Maximized;
                            window.Close();
                            Check(!window.IsVisible && !app.IsExiting, "Close hides the launcher without shutting it down");
                        }
                        await Activate(profileRoot, startup: true);
                        Check(!window.IsVisible, "A duplicate startup launch leaves the existing app hidden");
                        await Activate(profileRoot, startup: false);
                        Check(window.IsVisible, "Launching Hearth again restores its existing hidden window");
                        if (scenario == "tray-restore") Check(window.WindowState == WindowState.Maximized, "Restoring from the tray preserves the maximized window");
                        exerciseCompleted = true;
                        window.RequestQuit();
                        break;
                    case "quit-busy":
                        var operation = new Func<CancellationToken, Task>(async cancellation =>
                        {
                            try { await Task.Delay(Timeout.Infinite, cancellation); }
                            finally { await Task.Delay(50); operationStopped = true; }
                        });
                        _ = (Task)typeof(MainWindow).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, ["Lifecycle test operation", operation, false])!;
                        window.Close();
                        Check(!window.IsVisible, "Closing during an operation keeps it running in the tray");
                        exerciseCompleted = true;
                        window.RequestQuit();
                        Check(!app.IsExiting, "Quit allows cancellation cleanup before application shutdown");
                        break;
                    case "session-ending":
                        SimulateSessionEnding(app);
                        Check(app.IsExiting, "Windows session ending bypasses close-to-tray behavior");
                        exerciseCompleted = true;
                        window.Close(); // Must not be hidden/cancelled after session ending.
                        break;
                    default: throw new ArgumentException("Unknown lifecycle scenario.");
                }
            }
            catch (Exception error) { failure = error; app.ExitApplication(); }
        }));
        app.Run();
        if (ownedUrl != null)
        {
            try
            {
                OwnedHostChecks.AssertStoppedAsync(ownedUrl, ownedProcess).GetAwaiter().GetResult();
                Check(true, scenario + ": exit disposes the owned host process and listener");
            }
            catch (Exception error) { failure = error; }
        }
        if (failure != null) { Console.Error.WriteLine(failure); return 1; }
        if (!exerciseCompleted) { Console.Error.WriteLine("The lifecycle scenario did not run."); return 1; }
        Console.WriteLine($"PASS {scenario}: {checks} lifecycle checks");
        return 0;
    }

    private static void PrepareHostingProfile(string root, string scenario)
    {
        var profile = new ProfileStore(root);
        profile.State.CloseToTray = scenario is "host-tray-quit" or "host-startup-tray" or "host-crash";
        profile.State.LocalHostingEnabled = scenario != "host-disabled";
        profile.State.LocalHostingPort = scenario == "host-invalid-settings" ? 0 : OwnedHostChecks.FreePort();
        profile.State.LocalHostDataDirectory = Path.Combine(root, "Host");
        profile.State.LegacyHostingMigrated = true;
        profile.State.LocalHostOwnerToken = OwnedHostChecks.NewOwnerToken();
        // Keep a previously registered connection even in the disabled fixture:
        // saving the owner's pack access must not implicitly restart hosting.
        if (scenario == "host-disabled")
        {
            profile.State.LocalHostConnectionId = "fixture-local";
            profile.State.Hosts.Add(new("fixture-local", "This PC", "http://127.0.0.1:" + profile.State.LocalHostingPort, profile.State.LocalHostOwnerToken));
        }
        profile.Save();
    }

    private static async Task ExerciseHostingAsync(string scenario, string profileRoot, FixtureApp app, MainWindow window, Action<int?, string> captureHost, Action operationStopped)
    {
        var profile = new ProfileStore(profileRoot);
        if (scenario == "host-invalid-settings")
        {
            Check(window.IsVisible && app.LocalHost == null && !string.IsNullOrWhiteSpace(app.HostingError)
                && !Directory.Exists(Path.Combine(profileRoot, "Host")),
                "Invalid local host settings keep the launcher open with an explanation and no host child");
            ((Button)window.FindName("LibraryNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((FrameworkElement)window.FindName("LibraryView")).Visibility == Visibility.Visible
                && ((FrameworkElement)window.FindName("MainContent")).IsEnabled,
                "A local hosting setup failure leaves the player's library usable");
            ((Button)window.FindName("AddPackNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((TextBlock)window.FindName("PageTitle")).Text == "Add modpack",
                "Players can still navigate to joining a pack after local hosting setup fails");
            await app.DisableHostingAsync();
            var disabled = new ProfileStore(profileRoot);
            Check(!disabled.State.LocalHostingEnabled && disabled.State.LocalHostOwnerToken == profile.State.LocalHostOwnerToken,
                "Disabling failed hosting saves the preference and keeps its owner key even without a host controller");
            window.Close();
            return;
        }
        if (scenario is "host-disabled" or "host-persistence-disabled")
        {
            await Task.Delay(300);
            Check(app.LocalHost?.IsRunning != true && !await OwnedHostChecks.PortOpenAsync(profile.State.LocalHostingPort),
                "A saved connection with hosting disabled does not start a background host");
            window.Close();
            return;
        }

        var url = "http://127.0.0.1:" + profile.State.LocalHostingPort;
        using var api = new HearthApi(url, profile.State.LocalHostOwnerToken);
        await OwnedHostChecks.WaitUntilAsync(async () =>
        {
            if (app.LocalHost?.IsRunning != true) return false;
            try { await api.HealthAsync(); return true; }
            catch (System.Net.Http.HttpRequestException) { return false; }
        }, "The launcher's enabled host did not start.");
        captureHost(app.LocalHost!.ProcessId, app.LocalHost.HostUrl);
        var identity = await api.HealthAsync();
        await api.ListPacksAsync();
        Check(true, scenario + ": the open launcher serves authenticated hosting requests");
        // Startup now permits navigation, so enabled controls are no longer a completion signal.
        // Wait for the verified connection commit before testing the post-startup lifecycle.
        await OwnedHostChecks.WaitUntilAsync(() => Task.FromResult(new ProfileStore(profileRoot).State.Hosts.Any(host => host.HostId == identity.HostId)
            && ((FrameworkElement)window.FindName("MainContent")).IsEnabled), "Startup hosting did not commit the verified connection.");
        var saved = new ProfileStore(profileRoot);
        Check(saved.State.Hosts.Single().OwnerToken == profile.State.LocalHostOwnerToken && saved.State.LocalHostingEnabled,
            scenario + ": startup saves one verified local host and preserves the owner key");
        switch (scenario)
        {
            case "host-window-close":
                Check(window.IsVisible && !app.TrayAvailable, "Host is running with close-to-tray disabled");
                await ChooseHostingCloseAsync(window, "Keep hosting in the tray");
                Check(!window.IsVisible && app.TrayAvailable && app.LocalHost.IsRunning, "Close offers temporary tray hosting without changing the saved preference");
                await Activate(profileRoot, startup: false);
                await ChooseHostingCloseAsync(window, "Quit and stop hosting");
                break;
            case "host-close-busy":
                var operation = new Func<CancellationToken, Task>(async cancellation =>
                {
                    try { await Task.Delay(Timeout.Infinite, cancellation); }
                    finally { await Task.Delay(50); operationStopped(); }
                });
                _ = (Task)typeof(MainWindow).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, ["Lifecycle test operation", operation, false])!;
                await ChooseHostingCloseAsync(window, "Quit and stop hosting");
                Check(!app.IsExiting, "Closing a busy window allows cancellation cleanup before exiting");
                break;
            case "host-retry":
                ((Button)window.FindName("PublishNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Descendants(window).OfType<TextBlock>().Any(text => text.Text == "Hosting is running")
                    && Descendants(window).OfType<Button>().Any(button => Equals(button.Content, "Enable hosting") && button.Visibility == Visibility.Collapsed),
                    "The hosting page initially reports the running host");
                var stoppedPid = app.LocalHost.ProcessId;
                using (var stoppedHost = Process.GetProcessById(stoppedPid!.Value))
                {
                    stoppedHost.Kill(entireProcessTree: false);
                    await stoppedHost.WaitForExitAsync();
                }
                await OwnedHostChecks.WaitUntilAsync(() => Task.FromResult(
                    Descendants(window).OfType<Button>().Any(button => Equals(button.Content, "Enable hosting") && button.IsEnabled && button.Visibility == Visibility.Visible)
                    && Descendants(window).OfType<TextBlock>().Any(text => text.Text == "Hosting is off on this PC")),
                    "The current hosting page did not notice an unexpected host exit.");
                Check(true, "Unexpected host exit updates the visible status and offers Enable hosting without leaving the page");
                var retry = Descendants(window).OfType<Button>().Single(button => Equals(button.Content, "Enable hosting"));
                retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await OwnedHostChecks.WaitUntilAsync(() => Task.FromResult(app.LocalHost.IsRunning
                    && ((FrameworkElement)window.FindName("MainContent")).IsEnabled), "Retry did not restore the host.");
                captureHost(app.LocalHost.ProcessId, app.LocalHost.HostUrl);
                Check(app.LocalHost.ProcessId != stoppedPid && (await api.HealthAsync()).HostId == identity.HostId
                    && (await api.ListPacksAsync()).Count == 0, "Retry starts a new child while preserving host identity and owner access");
                window.RequestQuit();
                break;
            case "host-tray-quit":
            case "host-startup-tray":
                Check(app.TrayAvailable, "Hosting can use the real launcher tray icon");
                if (scenario == "host-tray-quit") window.Close();
                Check(!window.IsVisible && !app.IsExiting && (await api.HealthAsync()).HostId == identity.HostId,
                    "A hidden launcher continues serving its host in the system tray");
                await Activate(profileRoot, startup: false);
                Check(window.IsVisible && (await api.HealthAsync()).HostId == identity.HostId,
                    "Restoring the launcher keeps the same running host");
                window.RequestQuit();
                break;
            case "host-persistence-first":
                var pack = await api.CreatePackAsync(new("Remembered app pack", "1.21.1", "fabric", "0.16.10"));
                await File.WriteAllTextAsync(Path.Combine(profileRoot, "expected-host.json"), JsonSerializer.Serialize(new ExpectedHost(identity.HostId, pack.Id, pack.InviteCode)));
                window.RequestQuit();
                break;
            case "host-persistence-reopen":
                var expected = JsonSerializer.Deserialize<ExpectedHost>(await File.ReadAllTextAsync(Path.Combine(profileRoot, "expected-host.json")))!;
                var restored = (await api.ListPacksAsync()).Single();
                Check(identity.HostId == expected.HostId && restored.Id == expected.PackId && restored.InviteCode == expected.Invite,
                    "A new launcher process restores enabled hosting with the same owner, host, pack, and invite");
                var stoppingProcess = app.LocalHost.ProcessId;
                await app.DisableHostingAsync();
                await OwnedHostChecks.AssertStoppedAsync(url, stoppingProcess);
                var disabled = new ProfileStore(profileRoot);
                Check(!disabled.State.LocalHostingEnabled && disabled.State.Hosts.Single() == saved.State.Hosts.Single()
                    && disabled.State.LocalHostOwnerToken == saved.State.LocalHostOwnerToken,
                    "Disabling hosting stops its listener and saves the preference while preserving the owner's connection and key");
                window.RequestQuit();
                break;
            case "host-crash":
                window.Close();
                Check(!window.IsVisible && (await api.HealthAsync()).HostId == identity.HostId, "The crash fixture hosts from the tray");
                Console.WriteLine("HOST_READY " + app.LocalHost.ProcessId);
                Console.Out.Flush();
                break; // The parent terminates only this launcher process.
            default: throw new ArgumentException("Unknown hosting scenario.");
        }
    }

    private static async Task ChooseHostingCloseAsync(MainWindow window, string choice)
    {
        window.Close();
        await OwnedHostChecks.WaitUntilAsync(() => Task.FromResult(window.OwnedWindows.OfType<Window>().Any(w => w.Title == "Keep hosting?")), "Hosting close choice was not shown.");
        var dialog = window.OwnedWindows.OfType<Window>().Single(w => w.Title == "Keep hosting?");
        Check(window.IsVisible, "Closing does not stop hosting before the user chooses");
        Descendants(dialog).OfType<Button>().Single(b => Equals(b.Content, choice)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task CheckCrashCleanupAsync(string root)
    {
        PrepareHostingProfile(root, "host-crash");
        var profile = new ProfileStore(root);
        var url = "http://127.0.0.1:" + profile.State.LocalHostingPort;
        using var child = StartSelf(["--scenario", "host-crash", "--profile", root]);
        var errors = child.StandardError.ReadToEndAsync();
        int? hostProcess = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                var line = await child.StandardOutput.ReadLineAsync(timeout.Token);
                if (line == null) throw new IOException("The crash fixture exited before hosting: " + await errors);
                if (line.StartsWith("HOST_READY ", StringComparison.Ordinal)) { hostProcess = int.Parse(line[11..]); break; }
                Console.WriteLine(line);
            }
            Check(await OwnedHostChecks.PortOpenAsync(profile.State.LocalHostingPort) && OwnedHostChecks.ProcessAlive(hostProcess),
                "A launcher subprocess owns a live host before abrupt termination");
            // Kill(false) is intentional: tree-killing here would conceal a
            // broken Windows Job lifetime contract in the product itself.
            child.Kill(entireProcessTree: false);
            await child.WaitForExitAsync();
            await OwnedHostChecks.AssertStoppedAsync(url, hostProcess);
            Check(true, "Abruptly terminating only the launcher also kills its host and closes the port");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            if (OwnedHostChecks.ProcessAlive(hostProcess))
            {
                using var orphan = Process.GetProcessById(hostProcess!.Value);
                orphan.Kill(entireProcessTree: true);
                await orphan.WaitForExitAsync();
            }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed record ExpectedHost(string HostId, string PackId, string Invite);

    private sealed class FixtureApp : LauncherApp
    {
        public FixtureApp()
        {
            // WPF's generated Application.LoadComponent rejects an App subclass.
            // Load the same application resource dictionary while exercising the
            // real OnStartup/OnExit methods with only host paths isolated.
            var theme = XDocument.Load(Path.Combine(OwnedHostChecks.RepositoryRoot, "src", "Hearth.App", "App.xaml"));
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var dictionary = new XElement(xaml + "ResourceDictionary",
                new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
                theme.Root!.Element(xaml + "Application.Resources")!.Elements());
            Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
            ShutdownMode = Enum.Parse<ShutdownMode>(theme.Root.Attribute("ShutdownMode")!.Value);
        }

        protected override AppLocalHost CreateLocalHost(ProfileStore profile) => new(profile.Root, OwnedHostChecks.HostExecutable,
            profile.State.LocalHostingPort, profile.State.LocalHostDataDirectory);
    }

    private static async Task Activate(string profileRoot, bool startup)
    {
        var arguments = new List<string> { "--activate-only", "--profile", profileRoot };
        if (startup) arguments.Add("--startup");
        using var process = StartSelf(arguments);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        if (process.ExitCode != 0) throw new IOException(await process.StandardError.ReadToEndAsync());
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static Process StartSelf(IEnumerable<string> arguments)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("Cannot locate the test process.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Could not start the lifecycle test process.");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        checks++; Console.WriteLine("PASS " + description);
    }

    private static string? ValueAfter(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void SimulateSessionEnding(LauncherApp app)
    {
        // Exercise the application handler without sending a Windows logoff.
        var args = (SessionEndingCancelEventArgs)Activator.CreateInstance(typeof(SessionEndingCancelEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [ReasonSessionEnding.Logoff], culture: null)!;
        typeof(LauncherApp).GetMethod("OnSessionEnding", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, [args]);
        if (args.Cancel) throw new InvalidOperationException("Hearth tried to cancel Windows session ending.");
    }
}
