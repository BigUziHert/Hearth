using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace Hearth.App;
public partial class App : Application
{
    private Mutex? singleInstance;
    private CancellationTokenSource pipeCancel = new();
    private ProfileStore? profile;
    private TrayController? tray;
    public bool IsExiting { get; private set; }
    public bool TrayAvailable => tray != null;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var index = Array.IndexOf(e.Args, "--profile");
            var root = index >= 0 && index + 1 < e.Args.Length ? e.Args[index + 1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hearth");
            root = Path.GetFullPath(root);
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
            var window = new MainWindow(profile); MainWindow = window;
            window.Closing += (_, args) =>
            {
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
            _ = ListenAsync(pipe, window);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Hearth could not start", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
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
        tray?.Dispose();
        tray = null;
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
