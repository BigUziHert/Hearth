using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hearth.Core;

namespace Hearth.App;

public partial class MainWindow
{
    private readonly Dictionary<string, List<PackSummary>> ownerPackCache = [];
    private readonly Dictionary<string, bool> ownerAdminCache = [];
    private readonly Dictionary<string, HostConnection> ownerPackConnections = [];
    private readonly Dictionary<string, (StackPanel Panel, Button Create)> ownerPanels = [];
    private readonly CancellationTokenSource ownerRefreshCancellation = new();
    private DispatcherTimer? ownerRefreshTimer;
    private bool ownerRefreshBusy;

    private void InitializeOwnerRefresh()
    {
        ownerRefreshTimer = new() { Interval = TimeSpan.FromSeconds(60) };
        ownerRefreshTimer.Tick += (_, _) => _ = RefreshOwnerPackCountsAsync();
        ownerRefreshTimer.Start();
        _ = RefreshOwnerPackCountsAsync();
    }
    private void StopOwnerRefresh() { ownerRefreshTimer?.Stop(); ownerRefreshCancellation.Cancel(); }
    private int OwnerPendingCount(JoinedPack pack) => store.State.Hosts.Where(h => pack.HostId != null && h.HostId == pack.HostId
            && string.Equals(h.CertificatePin, pack.CertificatePin, StringComparison.OrdinalIgnoreCase) || h.Url == pack.HostUrl)
        .SelectMany(h => ownerPackCache.GetValueOrDefault(h.Id, [])).Where(p => p.Id == pack.PackId).Select(x => x.PendingCount).DefaultIfEmpty().Max();

    private void AddOwnerPacksToHostCard(HostConnection host, StackPanel card)
    {
        var create = Button("New pack", () => { selectedHost = host; ShowCreatePack(); });
        create.Visibility = Visibility.Collapsed;
        card.Children.Add(Row(create, Button("People", () => _ = ShowPeople(host), true)));
        var panel = new StackPanel(); card.Children.Add(panel);
        ownerPanels[host.Id] = (panel, create);
        if (ownerPackCache.TryGetValue(host.Id, out var cached)) RenderOwnerPackRows(host, panel, cached);
        else panel.Children.Add(Text("Loading packs…", 13, false, "#ADBAB1"));
        _ = LoadOwnerPackList(host, panel, create);
    }

    private async Task RefreshOwnerPackCountsAsync()
    {
        if (ownerRefreshBusy || exitRequested || ownerRefreshCancellation.IsCancellationRequested) return;
        ownerRefreshBusy = true;
        try
        {
            await Task.WhenAll(store.State.Hosts.ToArray().Select(async host =>
            {
                if (IsOwnedHost(host) && (Application.Current as App)?.LocalHost?.IsRunning != true) return;
                try
                {
                    var (packs, admin) = await QueryOwnerPacks(host, ownerRefreshCancellation.Token);
                    if (ownerRefreshCancellation.IsCancellationRequested) return;
                    ownerPackCache[host.Id] = packs; ownerAdminCache[host.Id] = admin;
                    if (ownerPanels.TryGetValue(host.Id, out var controls) && controls.Panel.IsDescendantOf(Page))
                    { controls.Create.Visibility = admin ? Visibility.Visible : Visibility.Collapsed; RenderOwnerPackRows(host, controls.Panel, packs); }
                }
                catch (Exception) { /* Periodic refresh keeps the last useful state; explicit actions show connection errors. */ }
            }));
            if (!ownerRefreshCancellation.IsCancellationRequested) RefreshSidebar();
        }
        finally { ownerRefreshBusy = false; }
    }

    private async Task<(List<PackSummary> Packs, bool Admin)> QueryOwnerPacks(HostConnection host, CancellationToken ct)
    {
        var connections = new List<HostConnection> { host };
        if (host.IsDelegated)
            connections.AddRange(store.State.Packs.Where(p => CanUsePackCredential(host, p))
                .Select(p => host with { OwnerToken = p.Token }));
        var found = new Dictionary<string, PackSummary>(); bool administrator = false;
        foreach (var connection in connections.DistinctBy(x => x.OwnerToken))
        {
            try
            {
                using var api = CreateOwnerApi(connection);
                foreach (var pack in await api.ListPacksAsync(ct))
                { found[pack.Id] = pack; ownerPackConnections[host.Id + "/" + pack.Id] = connection; }
                if (!host.IsDelegated) administrator = await IsHostAdministrator(connection, ct);
            }
            catch (HostApiException ex) when (host.IsDelegated && ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized) { }
        }
        return (found.Values.OrderBy(x => x.Name).ToList(), administrator);
    }

    private static bool CanUsePackCredential(HostConnection host, JoinedPack pack)
    {
        var sameAddress = HearthApi.NormalizeHost(host.Url) == HearthApi.NormalizeHost(pack.HostUrl);
        if (host.CertificatePin != null || pack.CertificatePin != null)
        {
            if (host.CertificatePin == null || pack.CertificatePin == null
                || !host.CertificatePin.Equals(pack.CertificatePin, StringComparison.OrdinalIgnoreCase)) return false;
            return host.HostId != null && pack.HostId != null ? host.HostId == pack.HostId : sameAddress;
        }
        return sameAddress && (host.HostId == null || pack.HostId == null || host.HostId == pack.HostId);
    }

    private async Task LoadOwnerPackList(HostConnection host, StackPanel panel, Button create)
    {
        if (IsOwnedHost(host) && (Application.Current as App)?.LocalHost?.IsRunning != true)
        { panel.Children.Clear(); panel.Children.Add(Text("Enable hosting on this PC to manage its packs.", 13, false, "#ADBAB1")); return; }
        try
        {
            var (packs, administrator) = await QueryOwnerPacks(host, ownerRefreshCancellation.Token);
            ownerPackCache[host.Id] = packs; ownerAdminCache[host.Id] = administrator;
            if (!panel.IsDescendantOf(Page)) return;
            create.IsEnabled = administrator; create.Visibility = administrator ? Visibility.Visible : Visibility.Collapsed;
            RenderOwnerPackRows(host, panel, packs); RefreshSidebar();
        }
        catch (Exception)
        {
            if (!panel.IsDescendantOf(Page)) return;
            panel.Children.Clear(); panel.Children.Add(Text("Can't reach " + host.Name + ". Check that its host is running.", 13, false, "#ADBAB1"));
        }
    }

    private void RenderOwnerPackRows(HostConnection host, StackPanel panel, List<PackSummary> packs)
    {
        panel.Children.Clear();
        if (packs.Count == 0) { panel.Children.Add(Text("No packs yet.", 13, false, "#ADBAB1")); return; }
        foreach (var pack in packs)
        {
            var row = new DockPanel { Margin = new(0, 14, 0, 0) };
            var open = Button("Open", () => { selectedHost = ownerPackConnections.GetValueOrDefault(host.Id + "/" + pack.Id, host); _ = OpenEditor(pack); }, true);
            DockPanel.SetDock(open, Dock.Right); row.Children.Add(open);
            var details = new StackPanel(); details.Children.Add(Text(pack.Name + (pack.PendingCount > 0 ? $" · {pack.PendingCount} pending" : ""), 16, true));
            details.Children.Add(Text($"Minecraft {pack.MinecraftVersion} · {LoaderName(pack.Loader)} · {(pack.ReleaseId == null ? "Draft only" : "Published")}", 12, false, "#ADBAB1"));
            row.Children.Add(details); panel.Children.Add(row);
        }
    }
}
