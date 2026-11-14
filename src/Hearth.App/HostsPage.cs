using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Hearth.Core;

namespace Hearth.App;
public partial class MainWindow
{
    private void ShowHosts() => ShowHosts(expandLocal: false);
    private void ShowHosts(bool expandLocal)
    {
        PageStart("hosts", "Hosting", "");
        if (store.State.Hosts.Count == 0)
        {
            var intro = Card("Choose where your packs live", "Connect to your Windows server, or host from this PC.");
            intro.Children.Add(Text("Players don't need to set up hosting.", 13, false, "#ADBAB1"));
        }
        else
        {
            foreach (var host in store.State.Hosts.ToArray())
            {
                var local = new Uri(host.Url).IsLoopback;
                var card = Card(host.Name, host.Url);
                var health = Text("Checking connection…", 13, false, "#ADBAB1"); card.Children.Add(health);
                _ = CheckHostConnection(host, health);
                if (local) card.Children.Add(Text("Local address. Friends on other networks need a reachable host address.", 12, false, "#D8B36B"));
                card.Children.Add(Row(Button("Manage packs", () =>
                {
                    if (selectedHost?.Id != host.Id) { selectedPack = null; draft = null; published = null; }
                    selectedHost = host; ShowPublishing();
                }), Button("Check connection", () => _ = CheckHostConnection(host, health), true)));
                var details = new StackPanel { Margin = new(0, 10, 0, 0) };
                details.Children.Add(Text("Use the owner key to connect from another PC. It gives management access.", 12, false, "#ADBAB1"));
                var connectionActions = Row(Button("Copy owner key", () => { Clipboard.SetText(host.OwnerToken); Status("Owner key copied. Keep it private."); }, true));
                if (!local) connectionActions.Children.Add(Button("Forget connection", () =>
                {
                    if (MessageBox.Show(this, $"Remove {host.Name} from this app? Copy its owner key first if you need to reconnect. Its packs, host service, and joined packs will remain.", "Forget host connection", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
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
        remoteContent.Children.Add(Text("Manage packs on an existing host, including your always-on server.", 13, false, "#ADBAB1"));
        var name = Field(remoteContent, "Name", "My server");
        var url = Field(remoteContent, "Host address", "https://");
        remoteContent.Children.Add(Text("Owner key", 12, true)); var key = new PasswordBox { Margin = new(0, 5, 0, 12) }; remoteContent.Children.Add(key);
        remoteContent.Children.Add(Text("Copy the owner key from Hosting on the other PC.", 12, false, "#ADBAB1"));
        remoteContent.Children.Add(Row(Button("Connect", () => _ = Run("Connecting to host…", async ct =>
        {
            using var api = new HearthApi(url.Text, key.Password); var health = await api.HealthAsync(ct); await api.ListPacksAsync(ct);
            var host = new HostConnection(Guid.NewGuid().ToString("N"), string.IsNullOrWhiteSpace(name.Text) ? health.Name : name.Text.Trim(), api.HostUrl, key.Password);
            var existing = store.State.Hosts.FindIndex(h => h.Url == host.Url && h.OwnerToken == host.OwnerToken);
            if (existing >= 0) host = host with { Id = store.State.Hosts[existing].Id };
            if (existing >= 0) store.State.Hosts[existing] = host; else store.State.Hosts.Add(host);
            store.Save(); selectedHost = host; selectedPack = null; published = null; draft = null;
            ShowOwnerPackList(); Status("Host connected.");
        }))));
        add.Children.Add(new Expander { Header = "Connect an existing host", Content = remoteContent, IsExpanded = false, Margin = new(0, 3, 0, 13) });
        var localContent = new StackPanel { Margin = new(0, 12, 0, 8) };
        localContent.Children.Add(Text("The host shares modpack files. Your Minecraft world server stays separate.", 13, false, "#ADBAB1"));
        var serviceState = Text("Checking the Windows service…", 13, true); localContent.Children.Add(serviceState);
        localContent.Children.Add(Text("Keeps running when Hearth closes and starts with Windows. Setup requires administrator access.", 12, false, "#ADBAB1"));
        var enable = Button("Enable hosting", () => _ = EnableHosting());
        var stop = Button("Stop hosting", () =>
        {
            if (MessageBox.Show(this, "Stop sharing packs from this PC? Players won't be able to download updates until hosting is enabled again.", "Stop hosting", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _ = Run("Stopping host…", async _ =>
            {
                await ServiceScript("Disable-Hosting.ps1", []);
                ShowHosts(); Status("Hosting stopped. Pack data was kept.");
            });
        }, true);
        stop.IsEnabled = false;
        localContent.Children.Add(Row(enable, stop));
        _ = UpdateServiceStatus(serviceState, enable, stop);
        add.Children.Add(new Expander { Header = "Host on this PC", Content = localContent, IsExpanded = expandLocal, Margin = new(0, 2, 0, 5) });
    }

    private async Task CheckHostConnection(HostConnection host, TextBlock label)
    {
        label.Text = "Checking connection…";
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
            using var api = new HearthApi(host.Url, host.OwnerToken, http);
            _ = await api.HealthAsync();
            var text = "Reachable from this PC";
            using var request = new HttpRequestMessage(HttpMethod.Get, host.Url + "/owner/status");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.OwnerToken);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var data = json.RootElement;
                if (data.TryGetProperty("lastPlayerAccessAt", out var last) && last.ValueKind == JsonValueKind.String)
                    text += $"\nLast player request: {data.GetProperty("lastPlayerName").GetString()} · {last.GetDateTimeOffset().LocalDateTime:g}";
                else text += "\nNo approved player requests since the host started.";
            }
            else if (response.StatusCode is HttpStatusCode.Unauthorized) text += "\nReconnect with a valid owner key.";
            label.Text = text;
        }
        catch (Exception ex) { label.Text = "Could not reach this host. Check its connection and try again."; label.ToolTip = ex.Message; }
    }
    private async Task UpdateServiceStatus(TextBlock label, Button? enable = null, Button? stop = null)
    {
        try
        {
            var info = new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("query"); info.ArgumentList.Add("HearthHost");
            using var process = Process.Start(info)!; var output = await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync();
            var exists = process.ExitCode == 0; var running = exists && output.Contains("RUNNING");
            label.Text = !exists ? "Hosting is not enabled" : running ? "Host service is running" : "Host service is stopped";
            if (enable != null) enable.Content = running ? "Update host service" : exists ? "Start hosting" : "Enable hosting";
            if (stop != null) stop.IsEnabled = exists;
        }
        catch (Exception ex) { label.Text = "Service status unavailable"; label.ToolTip = ex.Message; }
    }
    private string InstallDirectory => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    private async Task ServiceScript(string name, string[] arguments)
    {
        var script = Path.Combine(InstallDirectory, "scripts", name);
        if (!File.Exists(script)) throw new FileNotFoundException("Use the installed Hearth app to enable Windows hosting. For a development host, see the guide.");
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(arguments)) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Windows could not open the hosting setup."); await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException("Hosting setup did not finish. Confirm administrator access and see the hosting log in ProgramData\\Hearth, then retry.");
    }
    private async Task EnableHosting() => await Run("Setting up host service…", async ct =>
    {
        var local = await LocalHostingSetup.EnableAsync(store,
            temp => ServiceScript("Enable-Hosting.ps1", ["-InstallDir", InstallDirectory, "-TokenFile", temp]),
            async (host, cancellation) =>
            {
                using var api = new HearthApi(host.Url, host.OwnerToken);
                _ = await api.HealthAsync(cancellation); await api.ListPacksAsync(cancellation);
            }, ct);
        if (local == null)
        {
            ShowHosts(expandLocal: true);
            Status("Hosting setup canceled. No changes were made to hosting.");
            return;
        }
        selectedHost = local; selectedPack = null; draft = null; published = null; ShowHosts();
        Status("Hosting enabled. Open Manage packs to create a pack.");
    });
    private void OpenGuide()
    {
        var guide = Path.Combine(InstallDirectory, "docs", "FIRST-RUN.md");
        if (!File.Exists(guide)) guide = Path.Combine(Environment.CurrentDirectory, "docs", "FIRST-RUN.md");
        if (File.Exists(guide)) Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true, ArgumentList = { guide } });
        else MessageBox.Show(this, "The first-run guide is included beside the installer in docs.", "Hosting guide");
    }
}
