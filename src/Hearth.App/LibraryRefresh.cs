using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hearth.Core;

namespace Hearth.App;
public partial class MainWindow
{
    private DispatcherTimer? libraryRefreshTimer;
    private void InitializeLibraryRefresh()
    {
        libraryRefreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
        libraryRefreshTimer.Tick += (_, _) => { if (!busy && activePage == "library" && primaryPackAction == PackPrimaryAction.CheckApproval) _ = RefreshSelectedStatus(); };
        libraryRefreshTimer.Start();
        Activated += (_, _) => RefreshAfterHostChange();
        if ((Application.Current as App)?.LocalHost is { } local)
            local.StateChanged += (_, _) => Dispatcher.BeginInvoke(new Action(RefreshAfterHostChange));
    }
    public void RefreshAfterHostChange()
    {
        if (!exitRequested && activePage == "library" && (!busy || navigationDuringOperation)) _ = RefreshSelectedStatus();
    }
    private bool IsThisPcPack(JoinedPack pack)
    {
        var local = store.State.Hosts.FirstOrDefault(h => h.Id == store.State.LocalHostConnectionId);
        return (local != null && (pack.HostUrl == local.Url || (pack.HostId != null && pack.HostId == local.HostId)))
            || (Uri.TryCreate(pack.HostUrl, UriKind.Absolute, out var uri) && uri.IsLoopback && uri.Port == store.State.LocalHostingPort
                && pack.Token == store.State.LocalHostOwnerToken && pack.Token.Length > 0);
    }
    private bool LocalHostOff(JoinedPack pack) => IsThisPcPack(pack) && (Application.Current as App)?.LocalHost?.IsRunning != true;
    private void EnsureLocalHostAvailable(JoinedPack pack)
    {
        if (LocalHostOff(pack)) throw new InvalidOperationException("Hosting is off on this PC. Open Share packs and enable hosting first.");
    }
    private void ShowPendingFingerprint(JoinedPack pack)
    {
        LibraryDetails.Children.Clear();
        var panel = new StackPanel();
        panel.Children.Add(Text("Send this fingerprint to the pack owner", 14, true));
        var fingerprint = Text(Fingerprint(pack), 20, true); fingerprint.Margin = new(0, 0, 14, 9);
        panel.Children.Add(Row(fingerprint, Button("Copy fingerprint", () => CopyText(Fingerprint(pack), "Member fingerprint copied."), true)));
        panel.Children.Add(Text("The owner uses it to approve the correct person. Hearth checks approval automatically every 30 seconds.", 12, false, "#ADBAB1"));
        LibraryDetails.Children.Add(panel);
    }
    private void ShowReleaseNotes(PackManifest manifest, bool updateAvailable)
    {
        LibraryDetails.Children.Clear();
        if (!updateAvailable || string.IsNullOrWhiteSpace(manifest.Notes)) return;
        LibraryDetails.Children.Add(new Expander { Header = "What's new", IsExpanded = true,
            Content = new ScrollViewer { MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = Text(manifest.Notes, 13, false, "#ADBAB1") } });
    }
}
