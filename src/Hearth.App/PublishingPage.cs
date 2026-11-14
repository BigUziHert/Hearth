using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Hearth.Core;
using Microsoft.Win32;

namespace Hearth.App;
public partial class MainWindow
{
    private readonly Dictionary<string, List<PackSummary>> ownerPackCache = [];
    private readonly Dictionary<string, bool> ownerAdminCache = [];
    private CancellationTokenSource? ownerListRequest;
    private StackPanel? ownerListPanel;

    private void ShowPublishing()
    {
        if (selectedPack != null && draft != null && selectedHost != null) { RenderEditor(); return; }
        ShowOwnerPackList();
    }
    private void ShowOwnerPackList()
    {
        // Draft edits are already saved; Back returns to the list without keeping a hidden editor active.
        selectedPack = null; published = null; draft = null;
        PageStart("publish", "Manage modpacks", "");
        ownerListRequest?.Cancel();
        if (store.State.Hosts.Count == 0)
        {
            var empty = Card("Connect a host", "Choose the computer that will store and share your packs.");
            empty.Children.Add(Row(Button("Set up a host", ShowHosts)));
            return;
        }
        selectedHost ??= store.State.Hosts[0];
        var host = selectedHost;
        var header = Card("Your packs");
        if (store.State.Hosts.Count > 1)
        {
            var picker = new ComboBox { ItemsSource = store.State.Hosts, DisplayMemberPath = "Name", SelectedItem = host };
            header.Children.Add(Text("Host", 12, true)); header.Children.Add(picker);
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedItem is not HostConnection changed || changed.Id == selectedHost?.Id) return;
                selectedHost = changed; selectedPack = null; published = null; draft = null; ShowOwnerPackList();
            };
        }
        else header.Children.Add(Text(host.Name, 13, false, "#ADBAB1"));
        var create = Button("New pack", ShowCreatePack);
        create.IsEnabled = ownerAdminCache.GetValueOrDefault(host.Id);
        create.ToolTip = "Only the host administrator can create new packs.";
        header.Children.Add(Row(create, Button("Refresh", () => _ = LoadOwnerPacks(), true)));
        ownerListPanel = new StackPanel(); header.Children.Add(ownerListPanel);
        if (ownerPackCache.TryGetValue(host.Id, out var cached)) RenderOwnerPackRows(ownerListPanel, cached);
        else ownerListPanel.Children.Add(Text("Loading packs…", 13, false, "#ADBAB1"));
        _ = LoadOwnerPackList(host, ownerListPanel, create);
    }
    private Task LoadOwnerPacks()
    {
        ShowOwnerPackList();
        return Task.CompletedTask;
    }
    private async Task LoadOwnerPackList(HostConnection host, StackPanel panel, Button create)
    {
        ownerListRequest?.Cancel(); ownerListRequest?.Dispose(); ownerListRequest = new();
        var ct = ownerListRequest.Token;
        try
        {
            using var api = new HearthApi(host.Url, host.OwnerToken);
            var packs = await api.ListPacksAsync(ct);
            var administrator = await IsHostAdministrator(host, ct);
            if (ct.IsCancellationRequested || selectedHost?.Id != host.Id || activePage != "publish" || !panel.IsDescendantOf(Page)) return;
            ownerPackCache[host.Id] = packs; ownerAdminCache[host.Id] = administrator;
            create.IsEnabled = administrator; create.Visibility = administrator ? Visibility.Visible : Visibility.Collapsed;
            RenderOwnerPackRows(panel, packs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested || !panel.IsDescendantOf(Page)) return;
            panel.Children.Clear(); panel.Children.Add(Text("Could not load packs", 15, true));
            panel.Children.Add(Text(ex.Message, 13, false, "#ADBAB1"));
            panel.Children.Add(Row(Button("Retry", () => _ = LoadOwnerPackList(host, panel, create), true)));
        }
    }
    private void RenderOwnerPackRows(StackPanel panel, List<PackSummary> packs)
    {
        panel.Children.Clear();
        if (packs.Count == 0) { panel.Children.Add(Text("No packs yet. Create your first pack above.", 13, false, "#ADBAB1")); return; }
        foreach (var pack in packs)
        {
            var row = new DockPanel { Margin = new(0, 14, 0, 0), LastChildFill = true };
            var open = Button("Open", () => _ = OpenEditor(pack), true); DockPanel.SetDock(open, Dock.Right); row.Children.Add(open);
            var details = new StackPanel();
            details.Children.Add(Text(pack.Name, 16, true));
            details.Children.Add(Text($"Minecraft {pack.MinecraftVersion} · {LoaderName(pack.Loader)} · {(pack.ReleaseId == null ? "Draft only" : "Published")}", 12, false, "#ADBAB1"));
            row.Children.Add(details); panel.Children.Add(row);
        }
    }
    private static string LoaderName(string loader) => loader.ToLowerInvariant() switch { "fabric" => "Fabric", "forge" => "Forge", "neoforge" => "NeoForge", "quilt" => "Quilt", _ => loader };
    private static string GroupName(string group) => group switch { "voxy" => "Voxy", "distanthorizons" => "Distant Horizons", _ => "Required mods" };
    private static string FileName(PackFile file) => Path.GetFileName(file.Path);
    private static async Task<bool> IsHostAdministrator(HostConnection host, CancellationToken ct)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.OwnerToken);
        using var response = await http.GetAsync(host.Url + "/owner/status", ct);
        if (response.StatusCode == HttpStatusCode.Forbidden) return false;
        response.EnsureSuccessStatusCode(); return true;
    }

    private void ShowCreatePack()
    {
        if (selectedHost == null) return;
        ownerListRequest?.Cancel();
        PageStart("publish", "New pack", "");
        Page.Children.Add(Row(Button("Back to packs", ShowOwnerPackList, true)));
        var host = selectedHost;
        var form = Card("Pack details", "Hosted on " + host.Name + ". Match these versions to your Minecraft server.");
        var name = Field(form, "Pack name");
        form.Children.Add(Text("Minecraft version", 12, true));
        var mc = new ComboBox { IsEditable = true, Text = "1.21.1" }; form.Children.Add(mc);
        var loader = Select(form, "Mod loader", ["Fabric", "Forge", "NeoForge", "Quilt"], "Fabric");
        form.Children.Add(Text("Loader version", 12, true));
        var lv = new ComboBox { IsEditable = true }; form.Children.Add(lv);
        var versionStatus = Text("Loading available versions…", 12, false, "#ADBAB1"); form.Children.Add(versionStatus);
        var create = Button("Create pack", () => _ = Run("Creating pack…", async ct =>
        {
            using var api = new HearthApi(host.Url, host.OwnerToken);
            var created = await api.CreatePackAsync(new(name.Text.Trim(), mc.Text.Trim(), loader.SelectedItem!.ToString()!.ToLowerInvariant(), lv.Text.Trim()), ct);
            selectedHost = host; selectedPack = created; published = null; draft = new(); SaveDraft();
            ownerPackCache.Remove(host.Id); RenderEditor(); Status("Pack created. Add mods, then publish.");
        }));
        create.IsEnabled = false;
        form.Children.Add(Row(create));
        CancellationTokenSource? loaderRequest = null;
        bool populating = false;
        void CheckCreate() => create.IsEnabled = !string.IsNullOrWhiteSpace(name.Text) && !string.IsNullOrWhiteSpace(mc.Text) && !string.IsNullOrWhiteSpace(lv.Text);
        async Task LoadLoaders()
        {
            loaderRequest?.Cancel(); loaderRequest?.Dispose(); loaderRequest = new(); var ct = loaderRequest.Token;
            var minecraft = mc.Text.Trim(); var kind = loader.SelectedItem?.ToString()?.ToLowerInvariant();
            if (kind == null || minecraft.Length == 0) return;
            lv.ItemsSource = null; lv.Text = ""; CheckCreate();
            versionStatus.Text = "Loading matching loader versions…";
            try
            {
                await Task.Delay(350, ct);
                using var catalog = new VersionCatalog(); var versions = await catalog.LoaderAsync(kind, minecraft, ct);
                if (ct.IsCancellationRequested || !form.IsDescendantOf(Page) || mc.Text.Trim() != minecraft || loader.SelectedItem?.ToString()?.ToLowerInvariant() != kind) return;
                var entered = lv.Text;
                lv.ItemsSource = versions;
                if (!string.IsNullOrWhiteSpace(entered)) lv.Text = entered;
                else if (versions.Count > 0) lv.SelectedIndex = 0;
                versionStatus.Text = versions.Count == 0 ? "No matching versions found. Choose another Minecraft version or enter your tested loader version." : "Matching loader versions loaded. The recommended version is selected.";
                CheckCreate();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { if (form.IsDescendantOf(Page) && !ct.IsCancellationRequested) versionStatus.Text = "Couldn't load versions. You can enter the versions from your server. " + ex.Message; }
        }
        name.TextChanged += (_, _) => CheckCreate();
        lv.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => CheckCreate()));
        loader.SelectionChanged += (_, _) => { if (!populating) _ = LoadLoaders(); };
        mc.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => { if (!populating) _ = LoadLoaders(); }));
        _ = LoadMinecraft();
        async Task LoadMinecraft()
        {
            try
            {
                using var catalog = new VersionCatalog(); var versions = await catalog.MinecraftAsync();
                if (!form.IsDescendantOf(Page)) return;
                populating = true; var current = mc.Text; mc.ItemsSource = versions; mc.Text = current; populating = false;
            }
            catch (Exception) { if (form.IsDescendantOf(Page)) versionStatus.Text = "Enter your server's Minecraft and loader versions."; }
            if (form.IsDescendantOf(Page)) await LoadLoaders();
        }
    }

    private string DraftPath() => DraftPath(selectedHost!.Id, selectedPack!.Id);
    private string DraftPath(string hostId, string packId)
    {
        var dir = Path.Combine(store.Root, "Drafts"); Directory.CreateDirectory(dir);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hostId + "\n" + packId))).ToLowerInvariant();
        return Path.Combine(dir, key + ".json");
    }
    private void SaveDraft()
    {
        if (draft == null || selectedPack == null) return;
        var path = DraftPath(); File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(draft, JsonDefaults.Options)); File.Move(path + ".tmp", path, true);
    }
    private async Task OpenEditor(PackSummary pack) => await Run("Opening pack…", async ct =>
    {
        ownerListRequest?.Cancel(); var host = selectedHost!;
        using var api = new HearthApi(host.Url, host.OwnerToken);
        var latestPack = (await api.ListPacksAsync(ct)).FirstOrDefault(p => p.Id == pack.Id) ?? throw new InvalidOperationException("This pack is no longer available to this owner.");
        var latest = latestPack.ReleaseId == null ? null : await api.ManifestAsync(pack.Id, ct);
        var path = DraftPath(host.Id, pack.Id);
        var loaded = File.Exists(path) ? JsonSerializer.Deserialize<DraftState>(File.ReadAllText(path), JsonDefaults.Options) : null;
        loaded ??= new() { BaseReleaseId = latest?.ReleaseId, Files = latest?.Files.Select(f => new DraftItem(f, null)).ToList() ?? [] };
        selectedHost = host; selectedPack = latestPack; published = latest; draft = loaded;
        RenderEditor(); Status("Draft saved on this PC. Players receive changes after you publish.");
    });
    private void PackEditorHeader(string tab)
    {
        ownerListRequest?.Cancel(); ownerListPanel = null;
        var pack = selectedPack!; var host = selectedHost!;
        PageStart("publish", pack.Name, "");
        Page.Children.Add(Row(Button("Back to packs", ShowOwnerPackList, true), Text(host.Name + " / " + pack.Name, 13, false, "#ADBAB1")));
        var mods = Button("Mods", RenderEditor, true); var members = Button("Members", () => _ = ShowMembers(), true);
        var selected = tab == "mods" ? mods : members;
        selected.Background = Color("#34432F"); selected.BorderBrush = Color("#76905C");
        Page.Children.Add(Row(mods, members));
    }
    private void RenderEditor()
    {
        if (selectedPack == null || selectedHost == null || draft == null) { ShowOwnerPackList(); return; }
        PackEditorHeader("mods"); var pack = selectedPack;
        var editor = Card("Mods", $"Minecraft {pack.MinecraftVersion} · {LoaderName(pack.Loader)} {pack.LoaderVersion} · {(pack.ReleaseId == null ? "Not published" : "Published")}");
        if (draft.BaseReleaseId != published?.ReleaseId)
            editor.Children.Add(Text("A newer release was published on another PC. Discard this draft and reopen the current release before publishing.", 13, false, "#D8B36B"));
        var modActions = Row(Button("Add mods…", () => ChooseMods("base"))); editor.Children.Add(modActions);
        fileList = new ListBox { MinHeight = 130, MaxHeight = 330, SelectionMode = SelectionMode.Extended, AllowDrop = true };
        fileList.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        fileList.Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _ = AddFiles(files, groupOverride: "base"); };
        editor.Children.Add(fileList);
        editor.Children.Add(Text("Drop .jar files here to add required mods, including your own builds.", 12, false, "#ADBAB1"));
        var replace = Button("Replace…", () =>
        {
            if (SelectedDraftItems().SingleOrDefault() is not DraftItem item) return;
            var dialog = new OpenFileDialog { Filter = "Minecraft mods|*.jar" };
            if (dialog.ShowDialog(this) == true) _ = AddFiles([dialog.FileName], item);
        }, true);
        var remove = Button("Remove", () =>
        {
            foreach (var file in SelectedDraftItems()) draft.Files.Remove(file);
            SaveDraft(); RefreshDraft();
        }, true);
        replace.IsEnabled = remove.IsEnabled = false;
        fileList.SelectionChanged += (_, _) => { var count = SelectedDraftItems().Count; replace.IsEnabled = count == 1; remove.IsEnabled = count > 0; };
        modActions.Children.Add(replace); modActions.Children.Add(remove);
        var optional = new StackPanel { Margin = new(0, 10, 0, 5) };
        optional.Children.Add(Text("Let players choose Voxy, Distant Horizons, or Off. Downloads include required dependencies.", 13, false, "#ADBAB1"));
        optional.Children.Add(Row(Button("Add Voxy", () => _ = AddTerrain(TerrainMode.Voxy), true), Button("Add Distant Horizons", () => _ = AddTerrain(TerrainMode.DistantHorizons), true)));
        var group = Select(optional, "Add your own option files", ["Voxy", "Distant Horizons"], selectedGroup == "distanthorizons" ? "Distant Horizons" : "Voxy");
        optional.Children.Add(Row(Button("Choose .jar files…", () => ChooseMods(group.SelectedIndex == 1 ? "distanthorizons" : "voxy"), true)));
        optional.Children.Add(Text("Include every required dependency. Keep dependencies also needed by the main pack under Required mods.", 12, false, "#ADBAB1"));
        editor.Children.Add(new Expander { Header = "Optional terrain mods", Content = optional, Margin = new(0, 13, 0, 15), IsExpanded = false });
        var release = Card("Publish changes");
        var notesBody = new StackPanel { Margin = new(0, 8, 0, 0) };
        var notes = Field(notesBody, "Release notes", draft.Notes);
        notes.TextChanged += (_, _) => { draft!.Notes = notes.Text; SaveDraft(); };
        draftSummary = Text("", 13, false, "#ADBAB1"); release.Children.Add(draftSummary);
        release.Children.Add(new Expander { Header = "Release notes (optional)", Content = notesBody, Margin = new(0, 0, 0, 7) });
        var publish = Button("Review & publish", ReviewPublish);
        publish.IsEnabled = draft.BaseReleaseId == published?.ReleaseId;
        publish.Margin = new Thickness(14, 0, 0, 0);
        PageActions.Children.Add(publish);
        var options = new StackPanel { Margin = new(0, 10, 0, 0) };
        options.Children.Add(Row(Button("Open in my library", OpenOwnedPack, true), Button("Discard draft", () =>
        {
            if (MessageBox.Show(this, "Discard your unpublished changes and reopen the current release?", "Discard draft", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            File.Delete(DraftPath()); _ = OpenEditor(pack);
        }, true)));
        release.Children.Add(new Expander { Header = "Pack options", Content = options, Margin = new(0, 15, 0, 0) });
        RefreshDraft();
    }
    private List<DraftItem> SelectedDraftItems() => fileList?.SelectedItems.OfType<ListBoxItem>().Select(x => x.Tag).OfType<DraftItem>().ToList() ?? [];
    private void ChooseMods(string group)
    {
        var dialog = new OpenFileDialog { Filter = "Minecraft mods|*.jar", Multiselect = true };
        if (dialog.ShowDialog(this) == true) _ = AddFiles(dialog.FileNames, groupOverride: group);
    }
    private void OpenOwnedPack()
    {
        var host = selectedHost!; var pack = selectedPack!;
        if (!store.State.Packs.Any(p => p.HostUrl == host.Url && p.PackId == pack.Id)) store.State.Packs.Add(new(host.Url, pack.Id, pack.Name, host.OwnerToken, "owner"));
        store.State.SelectedPackHost = host.Url; store.State.SelectedPackId = pack.Id; store.Save(); ShowLibrary();
    }
    private async Task AddFiles(string[] paths, DraftItem? replacing = null, string? groupOverride = null) => await Run("Checking mods…", async ct =>
    {
        if (draft == null) return;
        var staged = new List<DraftItem>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested(); var file = await ModFiles.InspectAsync(path, replacing?.File.Group ?? groupOverride ?? "base", ct);
            staged.Add(new(file, path));
        }
        ct.ThrowIfCancellationRequested();
        if (replacing != null) draft.Files.Remove(replacing);
        foreach (var item in staged) { draft.Files.RemoveAll(f => f.File.Path.Equals(item.File.Path, StringComparison.OrdinalIgnoreCase) && f.File.Group == item.File.Group); draft.Files.Add(item); }
        SaveDraft(); RefreshDraft(); Status("Draft saved.");
    });
    private async Task AddTerrain(TerrainMode mode) => await Run("Finding compatible terrain mods…", async ct =>
    {
        var pack = selectedPack!; var group = mode == TerrainMode.Voxy ? "voxy" : "distanthorizons";
        using var resolver = new ModrinthTerrainResolver();
        var paths = await resolver.ResolveAsync(mode, pack.MinecraftVersion, pack.Loader, Path.Combine(store.Root, "Downloads", "Terrain"), new Progress<SyncProgress>(p => Status(p.Message)), ct);
        var entries = new List<DraftItem>();
        foreach (var path in paths)
        {
            var file = await ModFiles.InspectAsync(path, group, ct);
            if (draft!.Files.Any(x => x.File.Group == "base" && x.File.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))) continue;
            var conflicting = draft.Files.FirstOrDefault(x => x.File.Group == "base" && x.File.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase) && x.File.Sha256 != file.Sha256);
            if (conflicting != null) throw new InvalidOperationException($"This option needs a different version of {FileName(file)}. Replace the required mod first.");
            entries.Add(new(file, path));
        }
        draft!.Files.RemoveAll(x => x.File.Group == group); draft.Files.AddRange(entries); selectedGroup = group; SaveDraft(); RenderEditor(); Status($"Added {GroupName(group)}. Review the files before publishing.");
    });
    private List<string> Changes()
    {
        if (draft == null) return [];
        var old = published?.Files ?? []; var next = draft.Files.Select(f => f.File).ToList(); var changes = new List<string>();
        foreach (var file in next)
        {
            var before = old.FirstOrDefault(x => x.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase) && x.Group == file.Group);
            if (before == null) changes.Add($"Add {FileName(file)} · {GroupName(file.Group)}");
            else if (before.Sha256 != file.Sha256) changes.Add($"Replace {FileName(file)} · {GroupName(file.Group)}");
        }
        foreach (var file in old.Where(f => !next.Any(n => n.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase) && n.Group == f.Group)))
            changes.Add($"Remove {FileName(file)} · {GroupName(file.Group)}");
        return changes;
    }
    private void RefreshDraft()
    {
        if (fileList == null || draft == null) return;
        fileList.Items.Clear();
        if (draft.Files.Count == 0) fileList.Items.Add(new ListBoxItem { Content = Text("No mods added yet.", 13, false, "#ADBAB1"), IsEnabled = false });
        foreach (var group in new[] { "base", "voxy", "distanthorizons" })
        {
            var files = draft.Files.Where(x => x.File.Group == group).OrderBy(x => x.File.Path, StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0) continue;
            fileList.Items.Add(new ListBoxItem { Content = Text($"{GroupName(group)} · {files.Count}", 12, true, "#ADBAB1"), IsEnabled = false, Opacity = 1, Padding = new(8, 12, 8, 2) });
            foreach (var item in files)
            {
                var row = new DockPanel();
                var size = Text(item.File.Size >= 1024 * 1024 ? $"{item.File.Size / (1024d * 1024):0.0} MB" : item.File.Size >= 1024 ? $"{item.File.Size / 1024d:0} KB" : $"{item.File.Size} B", 12, false, "#ADBAB1");
                size.Margin = new(14, 0, 0, 0); DockPanel.SetDock(size, Dock.Right); row.Children.Add(size);
                var label = Text(FileName(item.File), 14); label.Margin = new(0); row.Children.Add(label);
                fileList.Items.Add(new ListBoxItem { Content = row, Tag = item, Padding = new(8, 9, 8, 9) });
            }
        }
        if (draftSummary != null) draftSummary.Text = $"{draft.Files.Count} mod files · {Changes().Count} unpublished changes · Draft saved on this PC";
    }
    private void ReviewPublish()
    {
        var changes = Changes();
        var window = new Window { Title = "Publish changes · Hearth", Width = 680, Height = 540, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Color("#1C2224"), Foreground = Color("#F0F4F0"), FontFamily = FontFamily };
        var panel = new StackPanel { Margin = new(25) };
        panel.Children.Add(Text("Publish changes", 24, true));
        panel.Children.Add(Text("Players receive this release the next time they press Play.", 13, false, "#ADBAB1"));
        panel.Children.Add(new TextBox { Text = changes.Count == 0 ? "No mod changes. This release updates the notes." : string.Join(Environment.NewLine, changes), IsReadOnly = true, Height = 285, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Row(Button("Publish", () => { window.Close(); _ = PublishDraft(); }), Button("Back to editing", () => window.Close(), true)));
        window.Content = panel; window.ShowDialog();
    }
    private async Task PublishDraft() => await Run("Publishing changes…", async ct =>
    {
        using var api = new HearthApi(selectedHost!.Url, selectedHost.OwnerToken); var pack = selectedPack!;
        foreach (var item in draft!.Files.Where(f => f.LocalPath != null).DistinctBy(f => f.File.Sha256))
        { Status("Uploading " + FileName(item.File) + "…"); await api.UploadAsync(pack.Id, item.LocalPath!, item.File, null, ct); }
        published = await api.PublishAsync(pack.Id, new(pack.Name, pack.MinecraftVersion, pack.Loader, pack.LoaderVersion, draft.Files.Select(f => f.File).ToList(), draft.Notes, draft.BaseReleaseId), ct);
        selectedPack = pack with { ReleaseId = published.ReleaseId };
        draft = new() { BaseReleaseId = published.ReleaseId, Files = published.Files.Select(f => new DraftItem(f, null)).ToList() };
        ownerPackCache.Remove(selectedHost.Id); SaveDraft(); RenderEditor(); Status("Published. Use Members to invite and approve players.");
    });
    private async Task ShowMembers() => await Run("Loading members…", async ct =>
    {
        var host = selectedHost!; var pack = selectedPack!;
        using var api = new HearthApi(host.Url, host.OwnerToken);
        var members = await api.MembersAsync(pack.Id, ct);
        var admin = await IsHostAdministrator(host, ct); ownerAdminCache[host.Id] = admin;
        RenderMembers(members, admin); Status("Members loaded.");
    });
    private void RenderMembers(List<MemberInfo> members, bool administrator)
    {
        PackEditorHeader("members"); var pack = selectedPack!; var host = selectedHost!;
        var local = new Uri(host.Url).IsLoopback;
        var invite = Card("Invite players", local ? "This host address only works on this PC. Add its public HTTPS address in Hosting before sharing with friends." : "Each person who uses this link still needs your approval.");
        invite.Children.Add(Row(Button(local ? "Copy local test invite" : "Copy invite", () =>
        {
            Clipboard.SetText(new Invite(host.Url, pack.Id, pack.InviteCode).ToString());
            Status(local ? "Local test invite copied. It only works on this PC." : "Invite copied.");
        }), local ? Button("Open Hosting", ShowHosts, true) : Button("Refresh requests", () => _ = ShowMembers(), true)));
        if (local) invite.Children.Add(Row(Button("Refresh requests", () => _ = ShowMembers(), true)));
        var pending = members.Where(x => x.Status == "pending").ToList();
        var approved = members.Where(x => x.Status == "approved").ToList();
        if (pending.Count == 0 && approved.Count == 0) { Card("No members yet", "Share the invite. New requests will appear here."); return; }
        void MemberSection(string title, List<MemberInfo> entries)
        {
            if (entries.Count == 0) return;
            var section = Card(title + $" ({entries.Count})");
            foreach (var member in entries)
            {
                var memberRow = new StackPanel { Margin = new(0, 8, 0, 13) };
                memberRow.Children.Add(Text(member.DisplayName + (member.Role == "owner" ? " · Owner" : ""), 16, true));
                if (member.Status == "pending") memberRow.Children.Add(Text("Confirm this fingerprint with your friend: " + member.Fingerprint, 12, false, "#ADBAB1"));
                var actions = Row();
                if (member.Status == "pending") actions.Children.Add(Button("Approve", () => _ = ChangeMember(member, "approved", "player")));
                if (administrator && member.Status == "approved" && member.Role != "owner")
                    actions.Children.Add(Button("Make owner", () =>
                    {
                        if (MessageBox.Show(this, $"Allow {member.DisplayName} to publish and manage players for this pack?", "Make pack owner", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                            _ = ChangeMember(member, "approved", "owner");
                    }, true));
                if (administrator || member.Role != "owner") actions.Children.Add(Button(member.Status == "pending" ? "Decline" : "Remove", () =>
                {
                    if (MessageBox.Show(this, $"Remove access for {member.DisplayName}? They will need a new request to join again.", "Remove member", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                        _ = ChangeMember(member, "removed", "player");
                }, true));
                memberRow.Children.Add(actions);
                if (member.Status == "approved")
                {
                    var detail = new StackPanel(); detail.Children.Add(Text("Fingerprint: " + member.Fingerprint, 12, false, "#ADBAB1"));
                    detail.Children.Add(Text("Joined request: " + member.RequestedAt.LocalDateTime.ToString("g"), 12, false, "#ADBAB1"));
                    memberRow.Children.Add(new Expander { Header = "Details", Content = detail, FontSize = 12 });
                }
                section.Children.Add(memberRow);
            }
        }
        MemberSection("Requests", pending); MemberSection("Members", approved);
    }
    private async Task ChangeMember(MemberInfo member, string status, string role) => await Run("Updating access…", async ct =>
    {
        var host = selectedHost!; using var api = new HearthApi(host.Url, host.OwnerToken);
        await api.UpdateMemberAsync(selectedPack!.Id, member.Id, new(status, role), ct);
        var members = await api.MembersAsync(selectedPack.Id, ct);
        RenderMembers(members, ownerAdminCache.GetValueOrDefault(host.Id)); Status("Access updated.");
    });
}
