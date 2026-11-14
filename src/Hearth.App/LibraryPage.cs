using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Hearth.Core;

namespace Hearth.App;
public partial class MainWindow
{
    private CancellationTokenSource? packStatusCancellation;
    private bool renderingTerrain;
    private enum PackPrimaryAction { AddPack, Checking, CheckApproval, SetUpPrism, Play, PlayInstalled, RequestAccess, CheckAgain, ChooseTerrain }
    private PackPrimaryAction primaryPackAction = PackPrimaryAction.AddPack;
    private string primaryPackKey = "";
    private long packStatusRequest;
    private PackManifest? selectedStatusManifest;
    private readonly Dictionary<string, string> knownPackRoles = new(StringComparer.Ordinal);
    private JoinedPack? CurrentPack => store.State.Packs.FirstOrDefault(p => p.HostUrl == store.State.SelectedPackHost && p.PackId == store.State.SelectedPackId);
    private bool IsSelected(JoinedPack pack) => store.State.SelectedPackHost == pack.HostUrl && store.State.SelectedPackId == pack.PackId;
    private static string Fingerprint(JoinedPack pack) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pack.Token))).ToLowerInvariant()[..12];
    private static string PackKey(JoinedPack pack) => pack.HostUrl + "\n" + pack.PackId + "\n" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pack.Token)));
    private bool IsCurrentPack(JoinedPack pack) => activePage == "library" && CurrentPack is { } current && PackKey(current) == PackKey(pack);
    private bool IsKnownOwner(JoinedPack pack) => knownPackRoles.TryGetValue(PackKey(pack), out var role) ? role == "owner" : store.State.Hosts.Any(host => host.Url == pack.HostUrl && host.OwnerToken == pack.Token);
    private void SelectPack(JoinedPack pack)
    {
        store.State.SelectedPackHost = pack.HostUrl; store.State.SelectedPackId = pack.PackId; store.Save(); ShowLibrary();
    }
    private void RefreshSidebar()
    {
        PackSidebar.Children.Clear();
        if (store.State.Packs.Count == 0) { var empty = Text("Your packs will appear here.", 13, false, "#8F9D95"); empty.Margin = new(14, 15, 10, 5); PackSidebar.Children.Add(empty); return; }
        var index = 0;
        foreach (var pack in store.State.Packs)
        {
            var content = new DockPanel { LastChildFill = true };
            var icon = PackArtwork.Create(index++, 35); icon.Margin = new(0, 0, 12, 0); DockPanel.SetDock(icon, Dock.Left); content.Children.Add(icon);
            content.Children.Add(new TextBlock { Text = pack.Name, FontSize = 16, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var selected = activePage == "library" && IsSelected(pack);
            var button = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(13, 8, 12, 8), Margin = new(3, 0, 3, 3), BorderThickness = new(0), Background = Color(selected ? "#3D5E27" : "#1B2022"), Foreground = Color("#F3F5F3"), ToolTip = pack.Name + " · " + new Uri(pack.HostUrl).Authority };
            System.Windows.Automation.AutomationProperties.SetName(button, pack.Name);
            button.Click += (_, _) => SelectPack(pack); PackSidebar.Children.Add(button);
        }
    }
    private InstalledPack? LocalInstallation(JoinedPack pack)
    {
        try { return sync.GetInstalled(pack.HostUrl, pack.PackId); }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }
    private void ShowLibrary()
    {
        if (CurrentPack == null && store.State.Packs.Count > 0)
        {
            store.State.SelectedPackHost = store.State.Packs[0].HostUrl; store.State.SelectedPackId = store.State.Packs[0].PackId;
        }
        PageStart("library", "Modpacks", ""); var pack = CurrentPack;
        selectedStatusManifest = null;
        PrismNotice.Visibility = Visibility.Collapsed;
        PackOptionsButton.IsEnabled = pack != null; PackOptionsButton.Visibility = pack == null ? Visibility.Collapsed : Visibility.Visible;
        TerrainPanel.Visibility = pack == null ? Visibility.Collapsed : Visibility.Visible;
        PlayPanel.Margin = new Thickness(16, 16, 16, pack == null ? 16 : 0);
        TerrainSelector.IsEnabled = false;
        renderingTerrain = true;
        TerrainOff.IsChecked = pack == null || pack.Terrain == TerrainMode.Off; TerrainVoxy.IsChecked = pack?.Terrain == TerrainMode.Voxy; TerrainDh.IsChecked = pack?.Terrain == TerrainMode.DistantHorizons;
        TerrainVoxy.IsEnabled = TerrainDh.IsEnabled = false;
        TerrainVoxy.ToolTip = TerrainDh.ToolTip = "Check the pack's available release first.";
        renderingTerrain = false;
        if (pack == null)
        {
            HeroTitle.Text = "Add your first modpack"; HeroTitle.ToolTip = null;
            HeroSubtitle.Text = "Paste an invite from your friend to get started.";
            SetPrimaryAction(null, PackPrimaryAction.AddPack, "Add modpack");
            SetPackStatus("No modpacks yet", "#93A88C", "Start with your friend's invite link."); return;
        }
        HeroTitle.Text = pack.Name; HeroTitle.ToolTip = pack.Name; HeroSubtitle.Text = new Uri(pack.HostUrl).Authority;
        SetPrimaryAction(pack, PackPrimaryAction.Checking, "Checking…", false);
        SetPackStatus("Checking access", "#A9BAA1", "Checking this pack's latest release.");
        _ = RefreshSelectedStatus();
    }
    private void SetPrimaryAction(JoinedPack? pack, PackPrimaryAction action, string label, bool enabled = true)
    {
        primaryPackAction = action; primaryPackKey = pack == null ? "" : PackKey(pack);
        PlayButton.Content = action is PackPrimaryAction.Play or PackPrimaryAction.PlayInstalled ? IconLabel(label, "M4,2 L17,10 4,18 Z") : label;
        PlayButton.FontSize = action == PackPrimaryAction.Play ? 22 : 17;
        PlayButton.IsEnabled = enabled;
        System.Windows.Automation.AutomationProperties.SetName(PlayButton, label);
    }
    private void SetPackStatus(string message, string color, string? hint = null)
    {
        PackStatusText.Text = message; PackStatusText.ToolTip = message; PackStatusDot.Fill = Color(color);
        if (hint != null) { PackStatusHint.Text = hint; PackStatusHint.ToolTip = hint; PackStatusHint.Visibility = hint.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
    }
    private void RenderApprovedPack(JoinedPack pack, PackManifest manifest)
    {
        if (!IsCurrentPack(pack)) return;
        selectedStatusManifest = manifest;
        var installed = LocalInstallation(pack);
        HeroSubtitle.Text = $"Minecraft {manifest.MinecraftVersion}  ·  {manifest.Loader} {manifest.LoaderVersion}  ·  {new Uri(pack.HostUrl).Authority}";
        TerrainSelector.IsEnabled = TerrainOff.IsEnabled = true;
        TerrainVoxy.IsEnabled = manifest.Files.Any(file => file.Group == "voxy"); TerrainDh.IsEnabled = manifest.Files.Any(file => file.Group == "distanthorizons");
        TerrainVoxy.ToolTip = TerrainVoxy.IsEnabled ? "Voxy and its dependencies, supplied by the owner." : "The owner hasn't offered Voxy for this release.";
        TerrainDh.ToolTip = TerrainDh.IsEnabled ? "Distant Horizons and its dependencies, supplied by the owner." : "The owner hasn't offered Distant Horizons for this release.";
        if (AvailablePrismExe() == null)
        {
            PrismNotice.Visibility = Visibility.Collapsed;
            SetPackStatus("Minecraft setup needed", "#D8B36B", "Install Prism, then sign in with your Microsoft account.");
            SetPrimaryAction(pack, PackPrimaryAction.SetUpPrism, "Set up Prism"); return;
        }
        PrismNotice.Visibility = Visibility.Collapsed;
        if ((pack.Terrain == TerrainMode.Voxy && !TerrainVoxy.IsEnabled) || (pack.Terrain == TerrainMode.DistantHorizons && !TerrainDh.IsEnabled))
        {
            SetPackStatus("Choose a terrain option", "#D8B36B", "Your saved option is unavailable. Choose Off or an available option below.");
            SetPrimaryAction(pack, PackPrimaryAction.ChooseTerrain, "Choose terrain"); return;
        }
        SetPrimaryAction(pack, PackPrimaryAction.Play, "Play");
        if (installed == null) SetPackStatus("Ready to install", "#62C629", "Play downloads this pack, then opens Minecraft in Prism.");
        else if (installed.ReleaseId != manifest.ReleaseId) SetPackStatus("Update available", "#D8B36B", "Play installs the latest release before Minecraft starts.");
        else if (installed.Terrain != pack.Terrain) SetPackStatus("Terrain change ready", "#D8B36B", "Play applies your choice before Minecraft starts.");
        else SetPackStatus("Ready to play", "#62C629", "Your pack is up to date.");
    }
    private async Task RefreshSelectedStatus()
    {
        var pack = CurrentPack; if (pack == null || activePage != "library") return;
        packStatusCancellation?.Cancel(); packStatusCancellation?.Dispose(); packStatusCancellation = new(); var ct = packStatusCancellation.Token;
        var request = ++packStatusRequest;
        bool IsCurrentRequest() => request == packStatusRequest && !ct.IsCancellationRequested && IsCurrentPack(pack);
        selectedStatusManifest = null; TerrainSelector.IsEnabled = false; PrismNotice.Visibility = Visibility.Collapsed;
        SetPrimaryAction(pack, PackPrimaryAction.Checking, "Checking…", false);
        SetPackStatus("Checking access", "#BECAA5", "Checking this pack's latest release.");
        var accessApproved = false;
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
            using var api = new HearthApi(pack.HostUrl, pack.Token, http);
            var access = await api.AccessAsync(pack.PackId, ct);
            if (!IsCurrentRequest()) return;
            knownPackRoles[PackKey(pack)] = access.Status == "approved" ? access.Role : "player";
            if (access.Status != "approved")
            {
                if (access.Status == "pending") { SetPackStatus("Waiting for approval", "#D8B36B", "Ask the owner to approve your request, then check again."); SetPrimaryAction(pack, PackPrimaryAction.CheckApproval, "Check approval"); }
                else { SetPackStatus("Access needs approval", "#D8B36B", "Use a current invite to send a new request."); SetPrimaryAction(pack, PackPrimaryAction.RequestAccess, "Request access"); }
                return;
            }
            accessApproved = true;
            var manifest = await api.ManifestAsync(pack.PackId, ct);
            if (!IsCurrentRequest()) return;
            RenderApprovedPack(CurrentPack!, manifest);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsCurrentRequest()) return;
            if (ex is HostApiException host && host.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                knownPackRoles[PackKey(pack)] = "player";
                SetPackStatus("Access needs approval", "#D8B36B", "Ask the owner for a current invite and request access again."); SetPrimaryAction(pack, PackPrimaryAction.RequestAccess, "Request access");
            }
            else if (ex is HostApiException { StatusCode: HttpStatusCode.NotFound })
            {
                if (accessApproved) { SetPackStatus("No release yet", "#D8B36B", "The owner needs to publish this pack before you can install it."); SetPrimaryAction(pack, PackPrimaryAction.CheckAgain, "Check again"); }
                else { SetPackStatus("Pack not found", "#D8B36B", "Ask the owner for a current invite."); SetPrimaryAction(pack, PackPrimaryAction.RequestAccess, "Request access"); }
            }
            else if (LocalInstallation(pack) != null)
            {
                if (AvailablePrismExe() == null) { SetPackStatus("Host unavailable · pack installed", "#D8B36B", "Set up Prism to play the copy already on this PC."); SetPrimaryAction(pack, PackPrimaryAction.SetUpPrism, "Set up Prism"); }
                else { SetPackStatus("Host unavailable", "#D8B36B", "Play the installed copy. No update or terrain change will be applied."); SetPrimaryAction(pack, PackPrimaryAction.PlayInstalled, "Play installed"); }
            }
            else { SetPackStatus("Host unavailable", "#D8B36B", "Ask the owner to check their host, then try again."); SetPrimaryAction(pack, PackPrimaryAction.CheckAgain, "Check again"); }
            PackStatusHint.ToolTip = ex.Message;
        }
    }
    private void Terrain_Checked(object sender, RoutedEventArgs e)
    {
        if (renderingTerrain || !TerrainSelector.IsEnabled || selectedStatusManifest == null || CurrentPack is not { } pack || primaryPackKey != PackKey(pack)) return;
        var mode = ReferenceEquals(sender, TerrainVoxy) ? TerrainMode.Voxy : ReferenceEquals(sender, TerrainDh) ? TerrainMode.DistantHorizons : TerrainMode.Off;
        var index = store.State.Packs.FindIndex(p => p.HostUrl == pack.HostUrl && p.PackId == pack.PackId); if (index < 0) return;
        store.State.Packs[index] = pack with { Terrain = mode }; store.Save();
        RenderApprovedPack(store.State.Packs[index], selectedStatusManifest); Status("Terrain choice saved for this pack.");
    }
    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPack is not { } pack) { ShowAddPack(); return; }
        if (!IsCurrentPack(pack) || primaryPackKey != PackKey(pack)) { _ = RefreshSelectedStatus(); return; }
        switch (primaryPackAction)
        {
            case PackPrimaryAction.Play: _ = Play(pack); break;
            case PackPrimaryAction.PlayInstalled: _ = PlayInstalled(pack); break;
            case PackPrimaryAction.SetUpPrism: _ = InstallPrism(); break;
            case PackPrimaryAction.CheckApproval:
            case PackPrimaryAction.CheckAgain: _ = RefreshSelectedStatus(); break;
            case PackPrimaryAction.RequestAccess: pendingInvite = ""; ShowAddPack(); Status("Paste a current invite for " + pack.Name + " to request access again."); break;
            case PackPrimaryAction.ChooseTerrain: TerrainOff.Focus(); Status("Choose Off or an available terrain option below."); break;
            case PackPrimaryAction.AddPack: ShowAddPack(); break;
        }
    }
    private void PackOptions_Click(object sender, RoutedEventArgs e)
    {
        var pack = CurrentPack; if (pack == null) return;
        var menu = new ContextMenu { PlacementTarget = PackOptionsButton, Placement = PlacementMode.Bottom };
        void Item(string name, Action action, bool enabled = true) { var item = new MenuItem { Header = name, IsEnabled = enabled }; item.Click += (_, _) => { if (IsCurrentPack(pack)) action(); }; menu.Items.Add(item); }
        Item("Check access & updates", () => _ = RefreshSelectedStatus());
        Item("Member details & fingerprint", () => ShowPackDetails(pack));
        if (Directory.Exists(sync.GetInstanceDirectory(pack.HostUrl, pack.PackId)))
            Item("Open instance folder", () => OpenFolder(sync.GetInstanceDirectory(pack.HostUrl, pack.PackId)));
        if (LocalInstallation(pack) != null)
        {
            Item("Play installed release", () => _ = PlayInstalled(pack), AvailablePrismExe() != null);
        }
        if (IsKnownOwner(pack)) { menu.Items.Add(new Separator()); Item("Manage as owner", () => _ = ManageJoinedPack(pack)); }
        menu.Items.Add(new Separator()); Item("Remove from library", () => RemoveJoinedPack(pack));
        // Do not attach a menu that captures this pack to the reusable button:
        // native right-click could otherwise reopen it after another pack is selected.
        menu.IsOpen = true;
    }
    private void RemoveJoinedPack(JoinedPack pack)
    {
        if (MessageBox.Show(this, $"Remove {pack.Name} from your library?\n\nYour downloaded Minecraft instance, worlds, and settings will be kept on this PC. To add the pack again, paste a current invite and request access.", "Remove modpack", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        packStatusCancellation?.Cancel(); ++packStatusRequest; knownPackRoles.Remove(PackKey(pack));
        store.State.Packs.RemoveAll(item => item.HostUrl == pack.HostUrl && item.PackId == pack.PackId);
        if (IsSelected(pack)) { store.State.SelectedPackHost = ""; store.State.SelectedPackId = ""; }
        store.Save(); ShowLibrary(); Status("Removed from your library. The downloaded instance and personal files were kept.");
    }
    private void ShowPackDetails(JoinedPack pack)
    {
        var window = new Window { Title = "Member details · Hearth", Width = 570, Height = 390, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = Color("#1C2224"), Foreground = Color("#F0F4F0"), FontFamily = FontFamily };
        var panel = new StackPanel { Margin = new(26) }; panel.Children.Add(Text(pack.Name, 24, true)); panel.Children.Add(Text(pack.HostUrl, 13, false, "#ADBAB1"));
        panel.Children.Add(Text("Your member fingerprint", 13, true)); panel.Children.Add(new TextBox { Text = Fingerprint(pack), IsReadOnly = true, FontSize = 21, FontFamily = new("Consolas") });
        panel.Children.Add(Text("Share this fingerprint with the owner so they can approve the right request.", 13, false, "#ADBAB1"));
        panel.Children.Add(Text("Member ID: " + pack.MemberId, 11, false, "#96A49C"));
        panel.Children.Add(Row(Button("Copy fingerprint", () => { Clipboard.SetText(Fingerprint(pack)); Status("Member fingerprint copied."); }, true), Button("Done", () => window.Close()))); window.Content = panel; window.ShowDialog();
    }
    private async Task PlayInstalled(JoinedPack pack) => await Run("Checking the installed release…", async ct =>
    {
        await sync.VerifyInstalledAsync(pack.HostUrl, pack.PackId, ct);
        ct.ThrowIfCancellationRequested();
        prism.Launch(PrismExe(), sync.GetInstanceId(pack.HostUrl, pack.PackId)); Status("Prism opened your previous complete release. No pack update was applied.");
    });
    private async Task ManageJoinedPack(JoinedPack pack) => await Run("Checking owner access…", async ct =>
    {
        using var api = new HearthApi(pack.HostUrl, pack.Token); var owned = await api.ListPacksAsync(ct); var match = owned.FirstOrDefault(p => p.Id == pack.PackId) ?? throw new InvalidOperationException("Ask the host administrator to make you an owner of this pack first.");
        var ownerHost = store.State.Hosts.FirstOrDefault(h => h.Url == pack.HostUrl && h.OwnerToken == pack.Token);
        if (ownerHost == null) { ownerHost = new(Guid.NewGuid().ToString("N"), pack.Name + " · owner", pack.HostUrl, pack.Token); store.State.Hosts.Add(ownerHost); store.Save(); }
        var latest = match.ReleaseId == null ? null : await api.ManifestAsync(match.Id, ct);
        var path = DraftPath(ownerHost.Id, match.Id); var loaded = File.Exists(path) ? JsonSerializer.Deserialize<DraftState>(File.ReadAllText(path), JsonDefaults.Options) : null;
        loaded ??= new() { BaseReleaseId = latest?.ReleaseId, Files = latest?.Files.Select(f => new DraftItem(f, null)).ToList() ?? [] };
        selectedHost = ownerHost; selectedPack = match; published = latest; draft = loaded; ShowPublishing(); Status("Owner workspace opened.");
    });
    private void ShowAddPack()
    {
        PageStart("join", "Add modpack", "");
        var join = Card("Join your friends", "Paste their invite. The pack owner approves your request before you can play.");
        var inviteBox = Field(join, "Invite link", pendingInvite); inviteBox.MaxLength = 4096;
        var name = Field(join, "Your name", store.State.DisplayName); name.MaxLength = 80;
        join.Children.Add(Row(Button("Request access", () => _ = Run("Requesting access…", async ct =>
        {
            var invite = Invite.Parse(inviteBox.Text); if (string.IsNullOrWhiteSpace(name.Text)) throw new InvalidOperationException("Enter your name so the owner knows who to approve.");
            var existing = store.State.Packs.FirstOrDefault(p => p.HostUrl == invite.HostUrl && p.PackId == invite.PackId);
            if (existing != null)
            {
                using var current = new HearthApi(existing.HostUrl, existing.Token);
                try { var access = await current.AccessAsync(existing.PackId, ct); if (access.Status is "approved" or "pending") { pendingInvite = ""; SelectPack(existing); Status(access.Status == "pending" ? "Your request is waiting for the owner's approval." : "This pack is already in your library."); return; } }
                catch (HostApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { }
            }
            using var api = new HearthApi(invite.HostUrl); var result = await api.JoinAsync(invite.PackId, new(invite.Code, name.Text.Trim()), ct);
            var joined = new JoinedPack(invite.HostUrl, result.PackId, result.Name, result.Token, result.MemberId, existing?.Terrain ?? TerrainMode.Off);
            if (existing != null) store.State.Packs.Remove(existing);
            store.State.DisplayName = name.Text.Trim(); ProfileLabel.Text = store.State.DisplayName.ToUpperInvariant(); store.State.Packs.Add(joined); pendingInvite = ""; SelectPack(joined); Status("Request sent. Ask your friend to approve you.");
        }))));
        inviteBox.Focus();
    }
}
