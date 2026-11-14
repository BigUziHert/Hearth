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
            if (!first)
            {
                using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(2000); using var writer = new StreamWriter(client); writer.WriteLine(invite ?? "activate"); writer.Flush(); Shutdown(); return;
            }
            var window = new MainWindow(new ProfileStore(root)); MainWindow = window; window.Show();
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
                await Dispatcher.InvokeAsync(() => { window.WindowState = WindowState.Normal; window.Activate(); if (value?.StartsWith("hearth://") == true) window.OpenInvite(value); });
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { }
        }
    }
    protected override void OnExit(ExitEventArgs e) { pipeCancel.Cancel(); singleInstance?.Dispose(); base.OnExit(e); }
}
