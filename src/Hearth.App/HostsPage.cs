using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hearth.Core;

namespace Hearth.App;
public partial class MainWindow
{
    private DispatcherTimer? hostingStatusTimer;
    private string pendingHostLink = "";
    private readonly Dictionary<string, HostEndpoints> remoteHostEndpoints = [];
    private bool CacheHostEndpoints(HostConnection host, HostEndpoints endpoints)
    {
        if (host.HostId == null || host.HostId != endpoints.HostId) return false;
        if (host.CertificatePin != null && endpoints.CertificatePin != null
            && !host.CertificatePin.Equals(endpoints.CertificatePin, StringComparison.OrdinalIgnoreCase)) return false;
        if ((endpoints.InternetAddress != null || endpoints.HomeNetworkAddresses.Count > 0) && endpoints.CertificatePin == null) return false;
        remoteHostEndpoints[host.Id] = endpoints; return true;
    }
    private bool IsOwnedHost(HostConnection host) => host.Id == store.State.LocalHostConnectionId;
    private static bool IsInternetAddress(string value)
    {
        var uri = new Uri(value);
        if (uri.IsLoopback) return false;
        if (IPAddress.TryParse(uri.Host, out var ip))
            return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? HostNetworkSetup.IsPublicIPv4(ip) : !ip.IsIPv6LinkLocal && !ip.IsIPv6SiteLocal && !ip.IsIPv6Multicast && !ip.IsIPv4MappedToIPv6 && (ip.GetAddressBytes()[0] & 0xfe) != 0xfc && !ip.Equals(IPAddress.IPv6Any);
        return uri.Host.Contains('.') && !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase);
    }
    private HostLink? GetLanSharingEndpoint(HostConnection host)
    {
        if (!IsOwnedHost(host))
        {
            if (remoteHostEndpoints.TryGetValue(host.Id, out var advertised)) return advertised.HomeNetworkAddresses.FirstOrDefault() is { } home
                ? new(home, advertised.CertificatePin, advertised.HostId) : null;
            return !new Uri(host.Url).IsLoopback && !IsInternetAddress(host.Url) ? new(host.Url, host.CertificatePin, host.HostId) : null;
        }
        var local = (Application.Current as App)?.LocalHost;
        var result = local?.Network?.Result;
        return local?.IsSharing == true && result?.LocalAddresses.FirstOrDefault() is { } address
            ? new(address, local.CertificatePin, local.HostId) : null;
    }
    private HostLink? GetPublicSharingEndpoint(HostConnection host)
    {
        if (!IsOwnedHost(host))
        {
            if (remoteHostEndpoints.TryGetValue(host.Id, out var advertised)) return advertised.InternetAddress is { } internet
                ? new(internet, advertised.CertificatePin, advertised.HostId) : null;
            return IsInternetAddress(host.Url) ? new(host.Url, host.CertificatePin, host.HostId) : null;
        }
        var local = (Application.Current as App)?.LocalHost;
        var result = local?.Network?.Result;
        return local?.IsSharing == true && local.InternetAddress is { } address
            ? new(address, local.CertificatePin, local.HostId) : null;
    }
    private HostLink? GetSharingEndpoint(HostConnection host) => GetPublicSharingEndpoint(host) ?? GetLanSharingEndpoint(host);
    private string GetSharingDescription(HostConnection host)
    {
        if (!IsOwnedHost(host)) return new Uri(host.Url).IsLoopback
            ? "This connection works only on this PC. Enable hosting to set up sharing."
            : "Friends use the address saved for this host.";
        return (Application.Current as App)?.LocalHost?.Network?.Result?.Status ?? "Enable hosting to set up secure sharing.";
    }
    private void CopyHostLink(HostLink link)
    {
        CopyText(link.ToString(), "Host connection link copied. On your other PC, paste it into Connect an existing host and enter the owner key.");
    }
    private void ShowHosts() => ShowHosts(expandLocal: false);
    private void ShowHosts(bool expandLocal)
    {
        HostConnectionMigration.NormalizeDelegated(store);
        PageStart("hosts", "Share packs", "");
        var app = Application.Current as App;
        var owned = store.State.Hosts.FirstOrDefault(IsOwnedHost);
        var local = Card("This PC", "Host pack files while Hearth is open or in the system tray. Your Minecraft world server stays separate.");
        AddLocalHostingControls(local, app);
        if (owned != null)
        {
            AddHostStatusAndLinks(owned, local);
            AddOwnerPacksToHostCard(owned, local);
        }
        foreach (var host in store.State.Hosts.Where(h => !IsOwnedHost(h)).ToArray())
        {
            var card = Card(host.IsDelegated && !host.Name.StartsWith("Shared with me", StringComparison.OrdinalIgnoreCase)
                ? "Shared with me · " + host.Name : host.Name, host.Url);
            AddHostStatusAndLinks(host, card);
            AddOwnerPacksToHostCard(host, card);
            var options = new StackPanel();
            var actions = Row(Button("Rename", () => RenameHost(host), true));
            if (!host.IsDelegated)
            {
                actions.Children.Add(Button("Copy owner key", () => CopyText(host.OwnerToken, "Owner key copied. Keep it private."), true));
                actions.Children.Add(Button("Copy host connection link", () => CopyHostLink(new(host.Url, host.CertificatePin, host.HostId)), true));
            }
            actions.Children.Add(Button("Forget connection", () =>
            {
                if (MessageBox.Show(this, $"Remove {host.Name} from this app? Its packs, your drafts, and joined packs will remain.", "Forget host connection", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                store.State.Hosts.RemoveAll(x => x.Id == host.Id); ownerPackCache.Remove(host.Id); ownerAdminCache.Remove(host.Id); remoteHostEndpoints.Remove(host.Id);
                if (selectedHost?.Id == host.Id) { selectedHost = null; selectedPack = null; draft = null; published = null; }
                store.Save(); ShowHosts();
            }, true));
            options.Children.Add(actions);
            card.Children.Add(new Expander { Header = "Connection options", Content = options, Margin = new(0, 10, 0, 0) });
        }
        AddHostConnectionForm();
        var priorRunning = app?.LocalHost?.IsRunning == true;
        var priorResult = app?.LocalHost?.Network?.Result;
        hostingStatusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        hostingStatusTimer.Tick += (_, _) =>
        {
            if (!busy && (priorRunning != (app?.LocalHost?.IsRunning == true) || !ReferenceEquals(priorResult, app?.LocalHost?.Network?.Result))) ShowHosts(true);
        };
        hostingStatusTimer.Start();
    }

    private void AddHostStatusAndLinks(HostConnection host, StackPanel card)
    {
        var health = Text("Checking connection…", 13, false, "#ADBAB1"); card.Children.Add(health);
        _ = CheckHostConnection(host, health);
        card.Children.Add(Row(Button("Check connection", () => _ = CheckHostConnection(host, health), true)));
        if (IsOwnedHost(host))
        {
            card.Children.Add(Text(GetSharingDescription(host), 13, false, "#ADBAB1"));
            if (GetPublicSharingEndpoint(host) is { } internet)
            {
                card.Children.Add(Text("Internet address · " + internet.HostUrl, 13, true));
                card.Children.Add(Row(Button("Copy host connection link", () =>
                {
                    if (GetPublicSharingEndpoint(host) is { } current) CopyHostLink(current);
                    else Status("The host address changed. Refresh this page before copying a link.");
                }, true)));
            }
            if (GetLanSharingEndpoint(host) is { } home)
            {
                card.Children.Add(Text("Home-network address · " + home.HostUrl, 13, true));
                card.Children.Add(Row(Button("Copy home-network link", () =>
                {
                    if (GetLanSharingEndpoint(host) is { } current) CopyHostLink(current);
                    else Status("Enable hosting before copying its host connection link.");
                }, true)));
            }
            card.Children.Add(Row(Button("Copy owner key", () => CopyText(host.OwnerToken, "Owner key copied. Keep it private."), true)));
        }
    }

    private void AddLocalHostingControls(StackPanel card, App? app)
    {
        var running = app?.LocalHost?.IsRunning == true;
        card.Children.Add(Text(running ? "Hosting is running" : "Hosting is off on this PC", 14, true));
        var error = !string.IsNullOrWhiteSpace(app?.HostingError) ? app.HostingError : app?.LocalHost?.LastError;
        if (!string.IsNullOrWhiteSpace(error)) card.Children.Add(Text(error, 13, false, "#D8B36B"));
        PasswordBox? recoveryKey = null;
        if (!running && app?.LocalHost is { } controller && File.Exists(Path.Combine(controller.DataDirectory, "host.json")))
        {
            var recovery = new StackPanel();
            recovery.Children.Add(Text("If the saved owner key was rejected or lost, paste this host's original owner key. It is saved only after the host accepts it.", 12, false, "#ADBAB1"));
            recovery.Children.Add(Text("Original owner key", 12, true));
            recoveryKey = new PasswordBox { Margin = new(0, 5, 0, 12) }; recovery.Children.Add(recoveryKey);
            card.Children.Add(new Expander { Header = "Recover or replace owner key", Content = recovery,
                IsExpanded = string.IsNullOrWhiteSpace(store.State.LocalHostOwnerToken) || error?.Contains("owner key", StringComparison.OrdinalIgnoreCase) == true,
                Margin = new(0, 5, 0, 8) });
        }
        var enable = Button("Enable hosting", () => _ = EnableHosting(recoveryKey?.Password));
        var retry = Button("Retry connection setup", () => _ = Run("Setting up connections…", async ct =>
        {
            if (Application.Current is not App current) return;
            try { await current.RetryHostNetworkAsync(ct); }
            finally { ShowHosts(true); }
        }), true);
        retry.ToolTip = "Retry Windows permission and network setup. Cancelling Windows permission keeps the current host running.";
        var stop = Button("Disable hosting", () => _ = Run("Stopping hosting…", async _ =>
        {
            if (Application.Current is not App current) return;
            await current.DisableHostingAsync(); ShowHosts(); Status("Hosting disabled. Your packs and owner key were kept.");
        }), true);
        var effective = app?.LocalHost?.Network?.Result;
        if (effective != null && app?.LocalHost?.InternetAddress is { } custom) effective = effective with { PublicAddress = custom };
        UpdateHostingActions(running, store.State.LocalHostingEnabled, effective, enable, retry, stop);
        card.Children.Add(Row(enable, retry, stop));
        var own = new StackPanel { Margin = new(0, 8, 0, 0) };
        var address = Field(own, "Address and port", store.State.LocalHostAdvertisedAddress,
            "Use a DNS, dynamic DNS, or VPN address already routed to this PC, such as https://packs.example.com:17843. Hearth includes its certificate fingerprint in the link.");
        own.Children.Add(Row(Button("Use this address", () => _ = Run("Saving host address…", _ =>
        {
            var value = AppLocalHost.NormalizeAdvertisedAddress(address.Text);
            var previous = store.State.LocalHostAdvertisedAddress;
            store.State.LocalHostAdvertisedAddress = value;
            try { store.Save(); } catch { store.State.LocalHostAdvertisedAddress = previous; throw; }
            app?.LocalHost?.SetAdvertisedAddress(value); ShowHosts(); Status("Host address saved. A friend's connection will confirm it is reachable."); return Task.CompletedTask;
        }), true), Button("Use automatic address", () => _ = Run("Restoring automatic host address…", _ =>
        {
            var previous = store.State.LocalHostAdvertisedAddress; store.State.LocalHostAdvertisedAddress = "";
            try { store.Save(); } catch { store.State.LocalHostAdvertisedAddress = previous; throw; }
            app?.LocalHost?.SetAdvertisedAddress(null); ShowHosts(); return Task.CompletedTask;
        }), true)));
        card.Children.Add(new Expander { Header = "Use my own address", Content = own, Margin = new(0, 8, 0, 0), IsExpanded = store.State.LocalHostAdvertisedAddress.Length > 0 });
    }

    private void RenameHost(HostConnection host)
    {
        var window = new Window { Title = "Rename host", Owner = this, Width = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var content = new StackPanel { Margin = new(24) }; var name = Field(content, "Host name", host.Name); name.MaxLength = 80;
        content.Children.Add(Row(Button("Save", () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) return;
            var replacement = host with { Name = name.Text.Trim() };
            var index = store.State.Hosts.FindIndex(x => x.Id == host.Id); if (index < 0) { window.Close(); return; }
            store.State.Hosts[index] = replacement;
            try { store.Save(); } catch { store.State.Hosts[index] = host; throw; }
            if (selectedHost?.Id == host.Id) selectedHost = replacement;
            window.Close(); ShowHosts();
        }), Button("Cancel", window.Close, true)));
        window.Content = content; window.ShowDialog();
    }

    private void AddHostConnectionForm()
    {
        var add = Card("Connect a host");
        var form = new StackPanel { Margin = new(0, 8, 0, 0) };
        form.Children.Add(Text("A host connection link is for managing packs as an owner. Players use a pack invite from the pack's Members tab.", 13, false, "#ADBAB1"));
        var name = Field(form, "Host name (optional)");
        var url = Field(form, "Host connection link", pendingHostLink);
        form.Children.Add(Text("On the host PC, open Share packs and copy its host connection link and owner key.", 12, false, "#ADBAB1"));
        form.Children.Add(Text("Owner key", 12, true)); var key = new PasswordBox { Margin = new(0, 5, 0, 12) }; form.Children.Add(key);
        form.Children.Add(Row(Button("Connect", () => _ = Run("Connecting to host…", async ct =>
        {
            if (url.Text.Trim().StartsWith("hearth://join", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This is a pack invite. Open Add modpack to request access to that pack.");
            var ownerKey = key.Password.Trim();
            if (string.IsNullOrEmpty(ownerKey)) throw new InvalidOperationException("Paste the owner key from Share packs on the host PC.");
            var link = HostLink.Parse(url.Text);
            var known = store.State.Hosts.FirstOrDefault(h => h.Url == link.HostUrl)
                ?? store.State.Hosts.FirstOrDefault(h => h.HostId == link.HostId && h.CertificatePin == link.CertificatePin && link.HostId != null && link.CertificatePin != null);
            if (known?.CertificatePin is { } savedPin && link.CertificatePin is { } offeredPin && savedPin != offeredPin)
                throw new InvalidOperationException("This host's fingerprint changed. Check with its owner before forgetting the old connection and reconnecting.");
            if (known?.HostId is { } savedId && link.HostId is { } offeredId && savedId != offeredId)
                throw new InvalidOperationException("This link identifies a different host. Copy a fresh host connection link from the host PC.");
            var pin = known?.CertificatePin ?? link.CertificatePin;
            using var api = new HearthApi(link.HostUrl, certificatePin: pin);
            var health = await api.HealthAsync(ct);
            var expectedId = known?.HostId ?? link.HostId;
            if (expectedId != null && expectedId != health.HostId) throw new InvalidOperationException("This link reached a different host. Copy a fresh host connection link from the host PC.");
            using var owner = new HearthApi(link.HostUrl, ownerKey, certificatePin: pin);
            await owner.ListPacksAsync(ct);
            if (!await owner.IsAdministratorAsync(ct))
                throw new InvalidOperationException("This is a member credential, not the host's owner key. Use Manage as owner from that pack's library page, or copy the owner key from the host PC.");
            var defaultName = string.IsNullOrWhiteSpace(health.Name) || health.Name.Equals("Hearth Host", StringComparison.OrdinalIgnoreCase)
                ? new Uri(api.HostUrl).Authority : health.Name;
            var host = new HostConnection(known?.Id ?? Guid.NewGuid().ToString("N"), string.IsNullOrWhiteSpace(name.Text) ? defaultName : name.Text.Trim(), api.HostUrl, ownerKey, pin, health.HostId);
            var existing = store.State.Hosts.FindIndex(h => h.Id == host.Id);
            if (existing >= 0) store.State.Hosts[existing] = host; else store.State.Hosts.Add(host);
            store.Save(); pendingHostLink = ""; selectedHost = host; selectedPack = null; published = null; draft = null;
            ShowHosts(); Status("Host connected.");
        }))));
        add.Children.Add(new Expander { Header = "Connect an existing host", Content = form, IsExpanded = pendingHostLink.Length > 0 });
    }

    internal static void UpdateHostingActions(bool running, bool enabled, HostNetworkResult? network, Button enable, Button retry, Button stop)
    {
        enable.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        retry.Visibility = running && (network?.FirewallReady != true || string.IsNullOrWhiteSpace(network?.PublicAddress))
            ? Visibility.Visible : Visibility.Collapsed;
        // A failed automatic start must still let the owner disable future automatic attempts.
        stop.Visibility = running || enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task CheckHostConnection(HostConnection host, TextBlock label)
    {
        if (IsOwnedHost(host) && (Application.Current as App)?.LocalHost?.IsRunning != true)
        {
            label.Text = "Hosting is off on this PC. Choose Enable hosting to manage and share packs.";
            return;
        }
        label.Text = "Checking connection…";
        try
        {
            using var http = HearthApi.CreateClient(host.CertificatePin, TimeSpan.FromSeconds(12));
            using var api = new HearthApi(host.Url, host.OwnerToken, http, host.CertificatePin);
            _ = await api.HealthAsync();
            var text = "Reachable from this PC";
            using var request = new HttpRequestMessage(HttpMethod.Get, host.Url + "/owner/status");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.OwnerToken);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                if (!IsOwnedHost(host))
                {
                    try { CacheHostEndpoints(host, await api.EndpointsAsync()); }
                    catch (HostApiException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden) { remoteHostEndpoints.Remove(host.Id); }
                }
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var data = json.RootElement;
                if (data.TryGetProperty("lastInternetPlayerAccessAt", out var outside) && outside.ValueKind == JsonValueKind.String)
                    text += $"\nLast confirmed outside connection: {data.GetProperty("lastInternetPlayerName").GetString()} · {outside.GetDateTimeOffset().LocalDateTime:g}";
                else text += "\nNo outside player connection verified since hosting started.";
                if (data.TryGetProperty("lastPlayerAccessAt", out var last) && last.ValueKind == JsonValueKind.String)
                    text += $"\nLast player request: {data.GetProperty("lastPlayerName").GetString()} · {last.GetDateTimeOffset().LocalDateTime:g}";
                else text += "\nNo approved player requests since the host started.";
            }
            else if (!host.IsDelegated && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                text += "\nThe saved owner key was rejected. Connect again with the host's original owner key.";
            label.Text = IsOwnedHost(host) && (Application.Current as App)?.LocalHost?.IsRunning != true
                ? "Hosting is off on this PC. Choose Enable hosting." : text;
        }
        catch (Exception ex) { label.Text = $"Can't reach {host.Name}. Check its connection and try again."; label.ToolTip = ex.Message; }
    }
    private string InstallDirectory => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    private async Task EnableHosting(string? recoveryKey = null) => await Run("Starting hosting…", async ct =>
    {
        if (Application.Current is not App app) throw new InvalidOperationException("Open Hearth to enable hosting.");
        try
        {
            var local = await app.EnableHostingAsync(ct, recoveryKey: string.IsNullOrWhiteSpace(recoveryKey) ? null : recoveryKey.Trim());
            selectedHost = local; selectedPack = null; draft = null; published = null;
            Status("Hosting is running. Create a pack below to share with friends.");
        }
        finally { ShowHosts(expandLocal: true); }
    });
    public async void StartConfiguredHosting()
    {
        await Run("Resuming hosting…", async ct =>
        {
            if (Application.Current is not App app) return;
            try
            {
                await app.EnableHostingAsync(ct, interactive: false);
                RefreshAfterHostChange();
                Status("Hosting is running while Hearth is open or in the tray.");
            }
            catch (Exception exception)
            {
                if (exitRequested || app.IsExiting) return;
                // A failed background startup must not leave a modal dialog hidden
                // behind the tray. Keep the launcher usable with a visible retry.
                ShowHosts(expandLocal: true);
                Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                Status("Hosting did not start. " + exception.Message);
            }
        }, allowNavigation: true);
    }
    public void ShowHostingStartupIssue(string message)
    {
        ShowHosts(expandLocal: true);
        Show();
        Status(message);
    }
    private void OpenGuide()
    {
        var guide = Path.Combine(InstallDirectory, "docs", "FIRST-RUN.md");
        if (!File.Exists(guide)) guide = Path.Combine(Environment.CurrentDirectory, "docs", "FIRST-RUN.md");
        if (File.Exists(guide)) Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true, ArgumentList = { guide } });
        else MessageBox.Show(this, "The first-run guide is included beside the installer in docs.", "Hosting guide");
    }
}
