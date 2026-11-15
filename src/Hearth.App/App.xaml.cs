using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Hearth.Core;

namespace Hearth.App;
public partial class App : Application
{
    private Mutex? singleInstance;
    private CancellationTokenSource pipeCancel = new();
    private ProfileStore? profile;
    private TrayController? tray;
    private string? hostingSetupError;
    public bool IsExiting { get; private set; }
    public bool TrayAvailable => tray != null;
    public AppLocalHost? LocalHost { get; private set; }
    public string HostingError { get; private set; } = "";
    public App()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            AppDiagnostics.Log(args.Exception);
            if (args.Exception is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException or ArgumentException)
            {
                args.Handled = true;
                (MainWindow as MainWindow)?.ReportRecoverableError(args.Exception);
            }
            else
            {
                MessageBox.Show("Hearth encountered an unexpected error and needs to restart. Your saved files are kept. Details: " + AppDiagnostics.LogPath, "Hearth", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true; ExitApplication();
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) => { AppDiagnostics.Log(args.Exception); args.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => { if (args.ExceptionObject is Exception error) AppDiagnostics.Log(error); };
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            FluentScrollBars.Apply(Resources);
            var index = Array.IndexOf(e.Args, "--profile");
            var root = index >= 0 && index + 1 < e.Args.Length ? e.Args[index + 1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hearth");
            root = Path.GetFullPath(root);
            AppDiagnostics.Configure(root);
            var pipe = "Hearth-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..20];
            singleInstance = new Mutex(true, "Local\\" + pipe, out var first);
            var invite = e.Args.FirstOrDefault(a => a.StartsWith("hearth://", StringComparison.OrdinalIgnoreCase));
            var startup = e.Args.Contains("--startup", StringComparer.Ordinal);
            if (!first)
            {
                // A second Windows startup invocation should not interrupt an
                // already-running launcher. Invites still bring it forward.
                if (startup && invite == null) { Shutdown(); return; }
                using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(2000); using var writer = new StreamWriter(client); writer.WriteLine(invite ?? "activate"); writer.Flush(); Shutdown(); return;
            }
            profile = new ProfileStore(root);
            try
            {
                LegacyHostingMigration.Adopt(profile);
                LocalHost = CreateLocalHost(profile);
            }
            catch (Exception ex)
            {
                // An unreadable old-host descriptor or invalid host setting must
                // not prevent someone from opening the player side of Hearth.
                hostingSetupError = "Hearth couldn't restore local hosting. You can still play joined packs. Check your host-data folder or restore its backup, then reopen Hearth. " + ex.Message;
                HostingError = hostingSetupError;
            }
            var window = new MainWindow(profile); MainWindow = window;
            window.Closing += (_, args) =>
            {
                if (!args.Cancel && !IsExiting && !profile.State.CloseToTray && LocalHost?.IsRunning == true)
                {
                    args.Cancel = true;
                    window.ConfirmHostingClose();
                    return;
                }
                if (!args.Cancel && !IsExiting && profile.State.CloseToTray && tray != null)
                {
                    args.Cancel = true;
                    tray.HideWindow();
                }
            };
            try { ApplyTraySettings(); }
            catch (Exception ex)
            {
                // Keep a visible, usable window if the tray could not initialize.
                window.Show();
                MessageBox.Show(window, "Hearth could not start its tray icon. The window will stay open.\n\n" + ex.Message,
                    "System tray unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            if (!startup || invite != null || !TrayAvailable) window.Show();
            if (invite != null) window.OpenInvite(invite);
            if (profile.RecoveryNotice is { } notice) MessageBox.Show(window, notice, "Profile recovered", MessageBoxButton.OK, MessageBoxImage.Information);
            _ = ListenAsync(pipe, window);
            if (hostingSetupError != null) window.ShowHostingStartupIssue(hostingSetupError);
            else if (profile.State.LocalHostingEnabled) window.StartConfiguredHosting();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Hearth could not start", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
    protected virtual AppLocalHost CreateLocalHost(ProfileStore store) => new(store.Root,
        port: store.State.LocalHostingPort,
        dataDirectory: string.IsNullOrWhiteSpace(store.State.LocalHostDataDirectory) ? null : store.State.LocalHostDataDirectory,
        httpsPort: store.State.LocalHostingPort == 65535 ? 17843 : store.State.LocalHostingPort + 1,
        advertisedAddress: store.State.LocalHostAdvertisedAddress);

    public async Task<HostConnection> EnableHostingAsync(CancellationToken cancellationToken = default, bool interactive = true, string? recoveryKey = null)
    {
        if (hostingSetupError != null) throw new InvalidOperationException(hostingSetupError);
        if (profile == null || LocalHost == null || IsExiting) throw new InvalidOperationException("Hearth is closing.");
        HostingError = hostingSetupError ?? "";
        try
        {
            if (LegacyHostingMigration.ServiceInstalled())
                throw new InvalidOperationException("An older Hearth Windows service is installed. Run the latest Hearth installer to migrate it before enabling hosting.");
            return await LocalHostingSetup.EnableAsync(profile, LocalHost.HostUrl, LocalHost.DataDirectory,
                (key, ct) => LocalHost.StartAsync(key, interactive, ct), cancellationToken, () => LocalHost.HostId, recoveryKey: recoveryKey);
        }
        catch (Exception exception)
        {
            await LocalHost.StopAsync();
            HostingError = exception is OperationCanceledException ? "Hosting startup was canceled. Retry to start hosting." : exception.Message;
            throw;
        }
    }

    public async Task RetryHostNetworkAsync(CancellationToken cancellationToken = default)
    {
        if (LocalHost == null || profile == null || IsExiting) throw new InvalidOperationException("Hearth is closing.");
        try { await LocalHost.RetryNetworkAsync(profile.State.LocalHostOwnerToken, cancellationToken); HostingError = ""; }
        catch (Exception ex) { HostingError = ex is OperationCanceledException ? "Connection setup was canceled. Existing hosting was kept." : ex.Message; throw; }
    }

    public async Task DisableHostingAsync()
    {
        if (profile == null) return;
        var previous = profile.State.LocalHostingEnabled;
        profile.State.LocalHostingEnabled = false;
        try { profile.Save(); }
        catch { profile.State.LocalHostingEnabled = previous; throw; }
        if (LocalHost != null) await LocalHost.StopAsync();
        HostingError = hostingSetupError ?? "";
    }
    private async Task ListenAsync(string name, MainWindow window)
    {
        while (!pipeCancel.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(pipeCancel.Token);
                using var reader = new StreamReader(server); var value = await reader.ReadLineAsync(pipeCancel.Token);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (IsExiting) return;
                    if (tray != null) tray.Restore();
                    else { if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; window.Show(); window.Activate(); }
                    if (value?.StartsWith("hearth://", StringComparison.OrdinalIgnoreCase) == true) window.OpenInvite(value);
                });
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { }
        }
    }
    public void ApplyTraySettings()
    {
        Dispatcher.VerifyAccess();
        if (MainWindow is not MainWindow window || profile == null) return;
        if (profile.State.CloseToTray) tray ??= new TrayController(window, window.RequestQuit);
        else if (tray != null)
        {
            tray.Restore();
            tray.Dispose();
            tray = null;
        }
    }

    public void KeepHostingInTray()
    {
        if (MainWindow is not MainWindow window) return;
        tray ??= new TrayController(window, window.RequestQuit);
        tray.HideWindow();
    }

    public void ExitApplication()
    {
        IsExiting = true;
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Closing to the tray must never prevent Windows shutdown or sign-out.
        IsExiting = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        pipeCancel.Cancel();
        LocalHost?.Dispose();
        tray?.Dispose();
        tray = null;
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
