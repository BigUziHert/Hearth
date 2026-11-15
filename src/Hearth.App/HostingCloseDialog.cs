using System.Windows;
using System.Windows.Controls;

namespace Hearth.App;
public partial class MainWindow
{
    private bool hostingClosePrompt;
    public void ConfirmHostingClose()
    {
        if (hostingClosePrompt) return;
        hostingClosePrompt = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var dialog = new Window { Title = "Keep hosting?", Owner = this, Width = 580, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var body = new StackPanel { Margin = new(24) };
                body.Children.Add(Text("This PC is hosting packs", 22, true));
                body.Children.Add(Text("Keep Hearth in the system tray so friends can still download packs, or quit and stop hosting."));
                body.Children.Add(Row(Button("Keep hosting in the tray", () => { dialog.Close(); (Application.Current as App)?.KeepHostingInTray(); }),
                    Button("Quit and stop hosting", () => { dialog.Close(); RequestQuit(); }, true)));
                dialog.Content = body; dialog.ShowDialog();
            }
            finally { hostingClosePrompt = false; }
        }));
    }
}
