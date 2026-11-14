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
        if (!IsOwnedHost(host)) return !new Uri(host.Url).IsLoopback && !IsInternetAddress(host.Url) ? new(host.Url, host.CertificatePin, host.HostId) : null;
        var local = (Application.Current as App)?.LocalHost;
        var result = local?.Network?.Result;
        return local?.IsRunning == true && result?.FirewallReady == true && result.LocalAddresses.FirstOrDefault() is { } address
            ? new(address, local.CertificatePin, local.HostId) : null;
    }
    private HostLink? GetPublicSharingEndpoint(HostConnection host)
    {
        if (!IsOwnedHost(host)) return IsInternetAddress(host.Url) ? new(host.Url, host.CertificatePin, host.HostId) : null;
        var local = (Application.Current as App)?.LocalHost;
        var result = local?.Network?.Result;
        return local?.IsRunning == true && result?.FirewallReady == true && result.PublicAddress is { } address
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
        Clipboard.SetText(link.ToString());
        Status("Host connection link copied. On your other PC, paste it into Connect an existing host and enter the owner key.");
    }
    private void ShowHosts() => ShowHosts(expandLocal: false);
    private void ShowHosts(bool expandLocal)
    {
        PageStart("hosts", "Hosting", "");
        var ownedHostCards = new List<(HostConnection Host, TextBlock Health, Button Manage)>();
        if (store.State.Hosts.Count == 0)
        {
            var intro = Card("Choose where your packs live", "Connect to your Windows server, or host from this PC.");
            intro.Children.Add(Text("Players don't need to set up hosting.", 13, false, "#ADBAB1"));
        }
        else
        {
            foreach (var host in store.State.Hosts.ToArray())
            {
                var card = Card(host.Name, IsOwnedHost(host) ? "Your modpack files are hosted on this PC." : host.Url);
                var health = Text("Checking connection…", 13, false, "#ADBAB1"); card.Children.Add(health);
                _ = CheckHostConnection(host, health);
                if (IsOwnedHost(host))
                {
                    card.Children.Add(Text(GetSharingDescription(host), 13, false, "#ADBAB1"));
                    if (GetPublicSharingEndpoint(host) is { } publicLink)
                    {
                        card.Children.Add(Text("Internet address · " + publicLink.HostUrl, 13, true));
                        card.Children.Add(Text("Secure sharing is configured. Check connection shows whether an outside player has connected since hosting started.", 12, false, "#ADBAB1"));
                        card.Children.Add(Row(Button("Copy host connection link", () =>
                        {
                            if (GetPublicSharingEndpoint(host) is { } current) CopyHostLink(current);
                            else { ShowHosts(expandLocal: true); Status("Internet sharing changed. Retry connection setup before copying a link."); }
                        }, true)));
                    }
                    if (GetLanSharingEndpoint(host) is { } lanLink)
                    {
                        card.Children.Add(Text("On your home network · " + lanLink.HostUrl, 13, true));
                        card.Children.Add(Row(Button("Copy home network link", () =>
                        {
                            if (GetLanSharingEndpoint(host) is { } current) CopyHostLink(current);
                            else { ShowHosts(expandLocal: true); Status("Start hosting before copying a connection link."); }
                        }, true)));
                    }
                    card.Children.Add(Text("Friends join with a pack invite from Manage packs → Members. Host connection links are for managing packs from another PC.", 12, false, "#ADBAB1"));
                }
                var manage = Button("Manage packs", () =>
                {
                    if (selectedHost?.Id != host.Id) { selectedPack = null; draft = null; published = null; }
                    selectedHost = host; ShowPublishing();
                });
                manage.IsEnabled = !IsOwnedHost(host) || (Application.Current as App)?.LocalHost?.IsRunning == true;
                if (IsOwnedHost(host)) ownedHostCards.Add((host, health, manage));
                card.Children.Add(Row(manage, Button("Check connection", () => _ = CheckHostConnection(host, health), true)));
                var details = new StackPanel { Margin = new(0, 10, 0, 0) };
                details.Children.Add(Text("Use the owner key to connect from another PC. It gives management access.", 12, false, "#ADBAB1"));
                var connectionActions = Row(Button("Copy owner key", () => { Clipboard.SetText(host.OwnerToken); Status("Owner key copied. Keep it private."); }, true));
                if (!IsOwnedHost(host) && !new Uri(host.Url).IsLoopback)
                    connectionActions.Children.Add(Button("Copy host connection link", () => CopyHostLink(new(host.Url, host.CertificatePin, host.HostId)), true));
                if (!IsOwnedHost(host)) connectionActions.Children.Add(Button("Forget connection", () =>
                {
                    if (MessageBox.Show(this, $"Remove {host.Name} from this app? Copy its owner key first if you need to reconnect. Its packs and joined packs will remain.", "Forget host connection", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    store.State.Hosts.RemoveAll(x => x.Id == host.Id); ownerPackCache.Remove(host.Id); ownerAdminCache.Remove(host.Id);
                    if (selectedHost?.Id == host.Id) { selectedHost = null; selectedPack = null; draft = null; published = null; }
                    store.Save(); ShowHosts();
                }, true));
                details.Children.Add(connectionActions);
                card.Children.Add(new Expander { Header = "Connection options", Content = details, Margin = new(0, 10, 0, 0) });
            }
        }
        var add = Card("Add a host");
        var remoteContent = new StackPanel { Margin = new(0, 12, 0, 8) };
        remoteContent.Children.Add(Text("On the host PC, open Hosting and copy its host connection link and owner key. Paste them here to manage the same packs from this PC.", 13, false, "#ADBAB1"));
        var name = Field(remoteContent, "Name", "My server");
        var url = Field(remoteContent, "Host connection link", pendingHostLink);
        remoteContent.Children.Add(Text("Starts with hearth://host. An existing HTTPS host address is also supported.", 12, false, "#ADBAB1"));
        remoteContent.Children.Add(Text("Owner key", 12, true)); var key = new PasswordBox { Margin = new(0, 5, 0, 12) }; remoteContent.Children.Add(key);
        remoteContent.Children.Add(Text("Copy the owner key from Hosting on the other PC.", 12, false, "#ADBAB1"));
        remoteContent.Children.Add(Row(Button("Connect", () => _ = Run("Connecting to host…", async ct =>
        {
            var ownerKey = key.Password.Trim();
            if (string.IsNullOrEmpty(ownerKey)) throw new InvalidOperationException("Paste the owner key from Hosting on the host PC.");
            var link = HostLink.Parse(url.Text);
            var known = store.State.Hosts.FirstOrDefault(h => h.Url == link.HostUrl);
            if (known?.CertificatePin is { } savedPin && link.CertificatePin is { } offeredPin && savedPin != offeredPin)
                throw new InvalidOperationException("This host's security identity changed. Check with its owner before forgetting the old connection and reconnecting.");
            if (known?.HostId is { } savedId && link.HostId is { } offeredId && savedId != offeredId)
                throw new InvalidOperationException("This link identifies a different host. Copy a fresh connection link from the host PC.");
            // Preserve the transport trust already saved for this address before sending an owner key.
            var pin = known != null ? known.CertificatePin : link.CertificatePin;
            using var api = new HearthApi(link.HostUrl, certificatePin: pin);
            var health = await api.HealthAsync(ct);
            var expectedId = known?.HostId ?? link.HostId;
            if (expectedId != null && expectedId != health.HostId) throw new InvalidOperationException("This link reached a different host. Copy a fresh connection link from the host PC.");
            using var owner = new HearthApi(link.HostUrl, ownerKey, certificatePin: pin);
            await owner.ListPacksAsync(ct);
            var host = new HostConnection(Guid.NewGuid().ToString("N"), string.IsNullOrWhiteSpace(name.Text) ? health.Name : name.Text.Trim(), api.HostUrl, ownerKey, pin, health.HostId);
            var existing = store.State.Hosts.FindIndex(h => h.OwnerToken == host.OwnerToken &&
                (h.Url == host.Url || (host.CertificatePin != null && h.CertificatePin == host.CertificatePin && h.HostId == host.HostId)));
            if (existing >= 0 && store.State.Hosts[existing].CertificatePin is { } oldPin && oldPin != host.CertificatePin)
                throw new InvalidOperationException("The host's security identity changed. Check with its owner before forgetting the old connection and reconnecting.");
            if (existing >= 0) host = host with { Id = store.State.Hosts[existing].Id };
            if (existing >= 0) store.State.Hosts[existing] = host; else store.State.Hosts.Add(host);
            store.Save(); pendingHostLink = ""; selectedHost = host; selectedPack = null; published = null; draft = null;
            ShowOwnerPackList(); Status("Host connected.");
        }))));
        add.Children.Add(new Expander { Header = "Connect an existing host", Content = remoteContent, IsExpanded = pendingHostLink.Length > 0, Margin = new(0, 3, 0, 13) });
        var localContent = new StackPanel { Margin = new(0, 12, 0, 8) };
        localContent.Children.Add(Text("The host shares modpack files. Your Minecraft world server stays separate.", 13, false, "#ADBAB1"));
        localContent.Children.Add(Text("Hearth creates secure connection links and asks your router to allow friends in. Windows may ask permission to allow Hearth through its firewall.", 12, false, "#ADBAB1"));
        var app = Application.Current as App;
        var running = app?.LocalHost?.IsRunning == true;
        var localStatus = Text("", 13, true); localContent.Children.Add(localStatus);
        var localError = Text("", 12, false, "#D8B36B"); localContent.Children.Add(localError);
        localContent.Children.Add(Text("Runs while Hearth is open or in the system tray. Quitting Hearth stops hosting. Reopening Hearth resumes it.", 12, false, "#ADBAB1"));
        localContent.Children.Add(Text("Choose close to tray and start with Windows in Settings.", 12, false, "#ADBAB1"));
        PasswordBox? recoveryKey = null;
        if (app?.LocalHost is { } controller && string.IsNullOrEmpty(store.State.LocalHostConnectionId)
            && string.IsNullOrEmpty(store.State.LocalHostOwnerToken)
            && File.Exists(Path.Combine(controller.DataDirectory, "host.json"))
            && !store.State.Hosts.Any(host => host.Url == controller.HostUrl))
        {
            localContent.Children.Add(Text("Existing pack data was found. Paste its original owner key to resume hosting.", 13, false, "#D8B36B"));
            localContent.Children.Add(Text("Original owner key", 12, true));
            recoveryKey = new PasswordBox { Margin = new(0, 5, 0, 12) }; localContent.Children.Add(recoveryKey);
        }
        var enable = Button("Enable hosting", () => _ = EnableHosting(recoveryKey?.Password));
        var retry = Button("Retry connection setup", () => _ = Run("Setting up secure connections…", async ct =>
        {
            if (Application.Current is not App current) return;
            // Repair applies only to a running host. A stopped host has one startup action.
            if (current.LocalHost?.IsRunning != true)
            {
                ShowHosts(expandLocal: true); Status("Hosting is stopped. Choose Enable hosting to start it."); return;
            }
            try { await current.RetryHostNetworkAsync(ct); }
            finally { ShowHosts(expandLocal: true); }
        }), true);
        retry.ToolTip = "Restart hosting and retry Windows firewall and router setup. Active mod downloads may need to retry.";
        var stop = Button("Disable hosting", () => _ = Run("Stopping host…", async _ =>
        {
            if (Application.Current is not App current) return;
            await current.DisableHostingAsync();
            ShowHosts(expandLocal: true); Status("Hosting disabled. Your packs and owner key were kept.");
        }), true);
        localContent.Children.Add(Row(enable, retry, stop));
        add.Children.Add(new Expander { Header = "Host on this PC", Content = localContent, IsExpanded = expandLocal || running || store.State.LocalHostingEnabled, Margin = new(0, 2, 0, 5) });
        var previousRunning = running;
        var previousNetwork = app?.LocalHost?.Network?.Result;
        void RefreshLocalStatus()
        {
            if (!busy && !ReferenceEquals(previousNetwork, app?.LocalHost?.Network?.Result))
            { ShowHosts(expandLocal: true); return; }
            var currentRunning = app?.LocalHost?.IsRunning == true;
            localStatus.Text = currentRunning ? "Hosting is running" : store.State.LocalHostingEnabled ? "Hosting is stopped — choose Enable hosting to start it" : "Hosting is off";
            var error = !string.IsNullOrEmpty(app?.HostingError) ? app.HostingError : app?.LocalHost?.LastError;
            localError.Text = error ?? "";
            localError.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
            UpdateHostingActions(currentRunning, store.State.LocalHostingEnabled, app?.LocalHost?.Network?.Result, enable, retry, stop);
            foreach (var card in ownedHostCards)
            {
                card.Manage.IsEnabled = currentRunning;
                if (previousRunning != currentRunning) _ = CheckHostConnection(card.Host, card.Health);
            }
            previousRunning = currentRunning;
        }
        RefreshLocalStatus();
        hostingStatusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        hostingStatusTimer.Tick += (_, _) => RefreshLocalStatus();
        hostingStatusTimer.Start();
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
            label.Text = store.State.LocalHostingEnabled ? "Hosting is stopped. Choose Enable hosting under Host on this PC." : "Hosting is off. Enable it below to manage and share packs.";
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
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var data = json.RootElement;
                if (data.TryGetProperty("lastInternetPlayerAccessAt", out var outside) && outside.ValueKind == JsonValueKind.String)
                    text += $"\nLast confirmed outside connection: {data.GetProperty("lastInternetPlayerName").GetString()} · {outside.GetDateTimeOffset().LocalDateTime:g}";
                else text += "\nNo outside player connection verified since hosting started.";
                if (data.TryGetProperty("lastPlayerAccessAt", out var last) && last.ValueKind == JsonValueKind.String)
                    text += $"\nLast player request: {data.GetProperty("lastPlayerName").GetString()} · {last.GetDateTimeOffset().LocalDateTime:g}";
                else text += "\nNo approved player requests since the host started.";
            }
            else if (response.StatusCode is HttpStatusCode.Unauthorized) text += "\nReconnect with a valid owner key.";
            label.Text = IsOwnedHost(host) && (Application.Current as App)?.LocalHost?.IsRunning != true
                ? "Hosting is stopped. Choose Enable hosting under Host on this PC." : text;
        }
        catch (Exception ex) { label.Text = "Could not reach this host. Check its connection and try again."; label.ToolTip = ex.Message; }
    }
    private string InstallDirectory => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    private async Task EnableHosting(string? recoveryKey = null) => await Run("Starting hosting…", async ct =>
    {
        if (Application.Current is not App app) throw new InvalidOperationException("Open Hearth to enable hosting.");
        try
        {
            if (recoveryKey != null)
            {
                if (recoveryKey.Trim().Length is < 32 or > 512) throw new InvalidOperationException("Paste the original owner key before enabling hosting.");
                var previousKey = store.State.LocalHostOwnerToken;
                store.State.LocalHostOwnerToken = recoveryKey.Trim();
                try { store.Save(); }
                catch { store.State.LocalHostOwnerToken = previousKey; throw; }
            }
            var local = await app.EnableHostingAsync(ct);
            selectedHost = local; selectedPack = null; draft = null; published = null;
            Status("Hosting is running while Hearth is open or in the tray. Open Manage packs to create a pack.");
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
        });
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
