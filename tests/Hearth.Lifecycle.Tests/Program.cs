using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Hearth.App;
using LauncherApp = Hearth.App.App;

internal static class Program
{
    private static int checks;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--activate-only")) return RunActivation();
        var scenario = ValueAfter(args, "--scenario");
        if (scenario != null) return RunScenario(scenario, ValueAfter(args, "--profile")!);
        var taskRoot = Path.Combine(Path.GetTempPath(), "Hearth-lifecycle-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(taskRoot);
        try
        {
            foreach (var name in new[] { "normal-close", "tray-restore", "startup-tray", "startup-visible", "quit-busy", "session-ending" })
            {
                var root = Path.Combine(taskRoot, name);
                var profile = new ProfileStore(root);
                profile.State.CloseToTray = name != "normal-close" && name != "startup-visible";
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
            Console.WriteLine("PASS: 6 isolated launcher lifecycle scenarios.");
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
        var app = new LauncherApp(); app.InitializeComponent();
        Exception? failure = null;
        var exerciseCompleted = false;
        var operationStopped = false;
        app.Exit += (_, _) =>
        {
            try
            {
                Check(app.IsExiting && !app.TrayAvailable, scenario + ": exit disposes the tray icon");
                if (scenario == "quit-busy") Check(operationStopped, "Quit waits for an in-progress operation to stop safely");
            }
            catch (Exception error) { failure = error; }
        };
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            try
            {
                var window = (MainWindow)app.MainWindow;
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
                        _ = (Task)typeof(MainWindow).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, ["Lifecycle test operation", operation])!;
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
        if (failure != null) { Console.Error.WriteLine(failure); return 1; }
        if (!exerciseCompleted) { Console.Error.WriteLine("The lifecycle scenario did not run."); return 1; }
        Console.WriteLine($"PASS {scenario}: {checks} lifecycle checks");
        return 0;
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
