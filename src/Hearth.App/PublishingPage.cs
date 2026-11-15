using System.IO;
using System.Net;
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
    private void ShowPublishing()
    {
        if (selectedPack != null && draft != null && selectedHost != null) { RenderEditor(); return; }
        ShowOwnerPackList();
    }
    private void ShowOwnerPackList()
    {
        selectedPack = null; published = null; draft = null; modFilter = "";
        ShowHosts();
    }
    private Task LoadOwnerPacks() { ShowOwnerPackList(); return Task.CompletedTask; }
    private static string LoaderName(string loader) => loader.ToLowerInvariant() switch { "fabric" => "Fabric", "forge" => "Forge", "neoforge" => "NeoForge", "quilt" => "Quilt", _ => loader };
    private static string GroupName(string group) => group switch { "voxy" => "Voxy", "distanthorizons" => "Distant Horizons", _ => "Required mods" };
    private static string FileName(PackFile file) => Path.GetFileName(file.Path);
    private HearthApi CreateOwnerApi(HostConnection host)
    {
        EnsureLocalHostAvailable(host);
        return new HearthApi(host.Url, host.OwnerToken, certificatePin: host.CertificatePin, hostName: host.Name);
    }
    private void EnsureLocalHostAvailable(HostConnection host)
    {
        if (IsOwnedHost(host) && (Application.Current as App)?.LocalHost?.IsRunning != true)
            throw new InvalidOperationException("Hosting is off on this PC. Enable hosting in Share packs before managing this pack.");
    }
    private async Task<bool> IsHostAdministrator(HostConnection host, CancellationToken ct)
    {
        using var api = CreateOwnerApi(host);
        return await api.IsAdministratorAsync(ct);
    }

    private void ShowCreatePack()
    {
        if (selectedHost == null) return;
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
            using var api = CreateOwnerApi(host);
            var created = await api.CreatePackAsync(new(name.Text.Trim(), mc.Text.Trim(), loader.SelectedItem!.ToString()!.ToLowerInvariant(), lv.Text.Trim()), ct);
            selectedHost = host; selectedPack = created; published = null; draft = DraftState.From(created, null); modFilter = ""; SaveDraft(); AddOwnedPackToLibrary(host, created);
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
        _ = LoadLoaders();
        async Task LoadMinecraft()
        {
            try
            {
                using var catalog = new VersionCatalog(); var versions = await catalog.MinecraftAsync();
                if (!form.IsDescendantOf(Page)) return;
                populating = true; var current = mc.Text; mc.ItemsSource = versions; mc.Text = current; populating = false;
            }
            catch (Exception) { if (form.IsDescendantOf(Page)) versionStatus.Text = "Enter your server's Minecraft and loader versions."; }
        }
    }

    private string DraftPath() => DraftPath(selectedHost!.HostId ?? selectedHost.Id, selectedPack!.Id);
    private string DraftPath(string hostId, string packId)
    {
        var dir = Path.Combine(store.Root, "Drafts"); Directory.CreateDirectory(dir);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hostId + "\n" + packId))).ToLowerInvariant();
        return Path.Combine(dir, key + ".json");
    }
    private void SaveDraft()
    {
        if (draft == null || selectedPack == null) return;
        var path = DraftPath();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(draft, JsonDefaults.Options);
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { stream.Write(bytes); stream.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
    private void SaveDraftSafely()
    {
        try { SaveDraft(); }
        catch (Exception ex) { Status("Your latest draft changes could not be saved. Keep Hearth open and retry. " + ex.Message); }
    }
    private async Task OpenEditor(PackSummary pack) => await Run("Opening pack…", async ct =>
    {
        var host = selectedHost!;
        using var api = CreateOwnerApi(host);
        var latestPack = (await api.ListPacksAsync(ct)).FirstOrDefault(p => p.Id == pack.Id) ?? throw new InvalidOperationException("This pack is no longer available to this owner.");
        var latest = latestPack.ReleaseId == null ? null : await api.ManifestAsync(pack.Id, ct);
        var path = DraftPath(host.HostId ?? host.Id, pack.Id);
        var legacy = DraftPath(host.Id, pack.Id);
        var loaded = await Task.Run(() => File.Exists(path) ? JsonSerializer.Deserialize<DraftState>(File.ReadAllText(path), JsonDefaults.Options)
            : File.Exists(legacy) ? JsonSerializer.Deserialize<DraftState>(File.ReadAllText(legacy), JsonDefaults.Options) : null, ct);
        if (loaded != null)
        {
            if (loaded.Baseline == null && loaded.BaseReleaseId != null)
            {
                try { loaded.Baseline = loaded.BaseReleaseId == latest?.ReleaseId ? latest : await api.ReleaseManifestAsync(pack.Id, loaded.BaseReleaseId, ct); }
                catch (HostApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
                // Older Hearth reset notes to empty after publishing. A draft with no local sources,
                // identical baseline files and empty notes is its unchanged post-publish snapshot.
                if (loaded.Baseline != null && loaded.Notes.Length == 0 && loaded.Files.All(x => x.LocalPath == null)
                    && loaded.Files.Select(x => x.File).OrderBy(x => x.Group + x.Path).SequenceEqual(loaded.Baseline.Files.OrderBy(x => x.Group + x.Path)))
                    loaded.Notes = loaded.Baseline.Notes;
            }
            loaded.Metadata ??= loaded.Baseline is {} original ? new(original.Name, original.MinecraftVersion, original.Loader, original.LoaderVersion)
                : new(latestPack.Name, latestPack.MinecraftVersion, latestPack.Loader, latestPack.LoaderVersion);
            if (!loaded.HasChanges) loaded = DraftState.From(latestPack, latest);
        }
        loaded ??= DraftState.From(latestPack, latest);
        selectedHost = host; selectedPack = latestPack; published = latest; draft = loaded; modFilter = "";
        selectedGroup = draft.SelectedGroup; SaveDraft();
        RenderEditor(); Status(draft.HasChanges ? "Your unpublished changes are ready to edit." : "Showing the latest release.");
    });
    private void PackEditorHeader(string tab)
    {
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
        {
            editor.Children.Add(Text("A newer release is available. Discard your changes to use it, or keep your edits on top of that release before publishing.", 13, false, "#D8B36B"));
            editor.Children.Add(Row(Button("Discard my changes", () => _ = ResetDraft(), true),
                Button("Keep editing", KeepEditingLatestRelease, true)));
        }
        var modActions = Row(Button("Add files…", () => ChooseMods("base")), Button("Paste mods", PasteMods, true)); editor.Children.Add(modActions);
        var filter = Field(editor, "Search imported mods", modFilter);
        filter.MinHeight = 0; filter.Height = 38; filter.Padding = new Thickness(10, 6, 10, 6);
        filter.ToolTip = "Filter this pack's imported mods by filename or mod group.";
        filter.TextChanged += (_, _) => { modFilter = filter.Text.Trim(); RefreshDraft(); };
        fileList = new ListBox { MinHeight = 130, MaxHeight = 330, SelectionMode = SelectionMode.Extended };
        AttachModImport(editor.Parent as FrameworkElement ?? editor);
        editor.Children.Add(fileList);
        editor.Children.Add(Text("Drop .jar files anywhere in this card, or copy files in Explorer and press Ctrl+V here. Custom JARs work too.", 12, false, "#ADBAB1"));
        var replace = Button("Replace…", () =>
        {
            if (SelectedDraftItems().SingleOrDefault() is not DraftItem item) return;
            var dialog = new OpenFileDialog { Filter = "Minecraft mods|*.jar" };
            if (dialog.ShowDialog(this) == true) _ = AddFiles([dialog.FileName], item);
        }, true);
        var remove = Button("Remove", () => _ = Run("Removing mods from the draft…", _ =>
        {
            var selected = SelectedDraftItems();
            SaveDraftFiles(draft.Files.Except(selected).ToList()); RefreshDraft();
            Status($"Removed {selected.Count} mod file(s) from the draft. Review & publish when ready.");
            return Task.CompletedTask;
        }), true);
        var copy = Button("Copy", () => _ = CopySelectedMods(), true);
        copy.ToolTip = "Copy selected JAR files to paste into another pack or folder (Ctrl+C).";
        replace.IsEnabled = remove.IsEnabled = copy.IsEnabled = false;
        fileList.SelectionChanged += (_, _) => { var count = SelectedDraftItems().Count; replace.IsEnabled = count == 1; remove.IsEnabled = copy.IsEnabled = count > 0; };
        editor.Children.Add(Row(copy, replace, remove));
        var optional = new StackPanel { Margin = new(0, 10, 0, 5) };
        optional.Children.Add(Text("Let players choose Voxy, Distant Horizons, or Off. Downloads include required dependencies.", 13, false, "#ADBAB1"));
        optional.Children.Add(Row(Button("Add Voxy", () => _ = AddTerrain(TerrainMode.Voxy), true), Button("Add Distant Horizons", () => _ = AddTerrain(TerrainMode.DistantHorizons), true)));
        var group = Select(optional, "Add your own option files", ["Voxy", "Distant Horizons"], draft.SelectedGroup == "distanthorizons" ? "Distant Horizons" : "Voxy");
        group.SelectionChanged += (_, _) => { selectedGroup = draft.SelectedGroup = group.SelectedIndex == 1 ? "distanthorizons" : "voxy"; SaveDraftSafely(); };
        optional.Children.Add(Row(Button("Choose .jar files…", () => ChooseMods(group.SelectedIndex == 1 ? "distanthorizons" : "voxy"), true)));
        optional.Children.Add(Text("Include every required dependency. Keep dependencies also needed by the main pack under Required mods.", 12, false, "#ADBAB1"));
        var optionalExpander = new Expander { Header = "Optional terrain mods", Content = optional, Margin = new(0, 13, 0, 15), IsExpanded = draft.OptionalExpanded };
        optionalExpander.Expanded += (_, _) => { draft.OptionalExpanded = true; SaveDraftSafely(); };
        optionalExpander.Collapsed += (_, _) => { draft.OptionalExpanded = false; SaveDraftSafely(); };
        editor.Children.Add(optionalExpander);
        AddPackSettings();
        var release = Card("Publish changes");
        var notesBody = new StackPanel { Margin = new(0, 8, 0, 0) };
        var notes = Field(notesBody, "Release notes", draft.Notes);
        notes.TextChanged += (_, _) => { draft!.Notes = notes.Text; SaveDraftSafely(); RefreshDraft(); };
        draftSummary = Text("", 13, false, "#ADBAB1"); release.Children.Add(draftSummary);
        release.Children.Add(new Expander { Header = "Release notes (optional)", Content = notesBody, Margin = new(0, 0, 0, 7) });
        var publish = Button("Review & publish", ReviewPublish);
        publish.IsEnabled = draft.BaseReleaseId == published?.ReleaseId && draft.HasChanges;
        publishButton = publish;
        publish.Margin = new Thickness(14, 0, 0, 0);
        PageActions.Children.Add(publish);
        release.Children.Add(Row(Button("Open in my library", OpenOwnedPack, true)));
        var options = new StackPanel { Margin = new(0, 10, 0, 0) };
        options.Children.Add(Row(Button("Discard draft", () =>
        {
            if (MessageBox.Show(this, "Discard your unpublished changes and reopen the current release?", "Discard draft", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _ = ResetDraft();
        }, true)));
        release.Children.Add(new Expander { Header = "Pack options", Content = options, Margin = new(0, 15, 0, 0) });
        if (ownerAdminCache.GetValueOrDefault(selectedHost.Id)) options.Children.Add(Row(Button("Archive pack", ConfirmArchivePack, true)));
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
        AddOwnedPackToLibrary(host, pack); ShowLibrary();
    }
    private void AddOwnedPackToLibrary(HostConnection host, PackSummary pack)
    {
        var localAlias = LocalHostIdentity.FindPinnedPack(store, host, pack.Id);
        var index = store.State.Packs.FindIndex(p => p == localAlias || (p.PackId == pack.Id && (p.HostUrl == host.Url
            || (host.CertificatePin is not null && p.CertificatePin is not null && host.HostId is not null
                && host.CertificatePin.Equals(p.CertificatePin, StringComparison.OrdinalIgnoreCase) && host.HostId == p.HostId))));
        var joined = new JoinedPack(host.Url, pack.Id, pack.Name, host.OwnerToken, host.IsDelegated && index >= 0 ? store.State.Packs[index].MemberId : "owner",
            index < 0 ? TerrainMode.Off : store.State.Packs[index].Terrain, host.CertificatePin,
            index < 0 ? null : store.State.Packs[index].InstallationUrl, host.HostId);
        // A host owner may already have joined through their own invitation.
        // Replace that pending/revoked credential without changing the instance.
        if (index < 0) store.State.Packs.Add(joined); else store.State.Packs[index] = joined;
        store.State.SelectedPackHost = host.Url; store.State.SelectedPackId = pack.Id; store.Save();
    }
    private async Task ResetDraft() => await Run("Loading the current release…", async ct =>
    {
        var host = selectedHost!; var pack = selectedPack!;
        using var api = CreateOwnerApi(host);
        var latestPack = (await api.ListPacksAsync(ct)).FirstOrDefault(p => p.Id == pack.Id)
            ?? throw new InvalidOperationException("This pack is no longer available to this owner.");
        var latest = latestPack.ReleaseId == null ? null : await api.ManifestAsync(pack.Id, ct);
        // Do not discard the only saved draft until its replacement is available.
        selectedPack = latestPack; published = latest;
        draft = DraftState.From(latestPack, latest);
        SaveDraft(); RenderEditor(); Status("Draft reset to the current release.");
    });
    private void KeepEditingLatestRelease()
    {
        if (draft == null || published == null) return;
        var conflicts = draft.ConflictsWith(published);
        var detail = "Use the latest release as your draft's starting point and keep your local edits? Changes from the latest release that you did not edit are retained.";
        if (conflicts.Count > 0) detail += "\n\nBoth owners changed these items. Your draft's version will be used:\n" + string.Join("\n", conflicts);
        detail += "\n\nReview the result before publishing. Nothing is published by this action.";
        if (MessageBox.Show(this, detail, "Keep editing on the latest release", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var before = draft;
        try { draft = draft.KeepChangesOn(published); SaveDraft(); RenderEditor(); Status("Your edits are based on the latest release. Review them before publishing."); }
        catch (Exception ex) { draft = before; ReportRecoverableError(ex); }
    }
    private async Task AddFiles(string[] paths, DraftItem? replacing = null, string? groupOverride = null) => await Run("Checking mods…", async ct =>
    {
        if (draft == null) return;
        var staged = await Task.Run(() => DraftModImport.StageAsync(paths, store.Root, replacing?.File.Group ?? groupOverride ?? "base", ct), ct);
        ct.ThrowIfCancellationRequested();
        SaveDraftFiles(DraftModImport.Apply(draft.Files, staged, replacing));
        modFilter = ""; RenderEditor(); Status($"Added {staged.Count} mod file(s) to the draft. Review & publish when ready.");
    });
    private async Task AddTerrain(TerrainMode mode) => await Run("Finding compatible terrain mods…", async ct =>
    {
        var host = selectedHost!; var pack = selectedPack!; var current = draft!;
        var metadata = current.Metadata!; var group = mode == TerrainMode.Voxy ? "voxy" : "distanthorizons";
        var progress = new Progress<SyncProgress>(p => Status(p.Message));
        var entries = await Task.Run(async () =>
        {
            using var resolver = new ModrinthTerrainResolver();
            using var api = CreateOwnerApi(host);
            var required = new List<(PackFile File, IReadOnlyList<string> Ids)>();
            foreach (var item in current.Files.Where(x => x.File.Group == "base"))
            {
                var source = await DraftModImport.GetSourceAsync(item, store.Root, api, pack.Id, ct);
                required.Add((item.File, ModFiles.ReadModIds(source)));
            }
            var paths = await resolver.ResolveAsync(mode, metadata.MinecraftVersion, metadata.Loader, Path.Combine(store.Root, "Downloads", "Terrain"), progress, ct);
            var candidates = await DraftModImport.StageAsync(paths, store.Root, group, ct);
            var selected = ModIdentityValidation.SelectOptional(required, candidates.Select(x => (x.File, ModFiles.ReadModIds(x.LocalPath!))));
            return candidates.Where(x => selected.Contains(x.File)).ToList();
        }, ct);
        SaveDraftFiles(current.Files.Where(x => x.File.Group != group).Concat(entries).ToList());
        selectedGroup = current.SelectedGroup = group; current.OptionalExpanded = true;
        SaveDraft(); RenderEditor(); Status($"Added {GroupName(group)}. Required mods keep their existing shared dependencies. Review before publishing.");
    });
    private List<string> Changes()
    {
        if (draft == null) return [];
        var old = draft.Baseline?.Files ?? []; var next = draft.Files.Select(f => f.File).ToList(); var changes = new List<string>();
        foreach (var file in next)
        {
            var before = old.FirstOrDefault(x => x.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase) && x.Group == file.Group);
            if (before == null) changes.Add($"Add {FileName(file)} · {GroupName(file.Group)}");
            else if (before.Sha256 != file.Sha256) changes.Add($"Replace {FileName(file)} · {GroupName(file.Group)}");
        }
        foreach (var file in old.Where(f => !next.Any(n => n.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase) && n.Group == f.Group)))
            changes.Add($"Remove {FileName(file)} · {GroupName(file.Group)}");
        var baseline = draft.Baseline;
        if (draft.Notes != (baseline?.Notes ?? "")) changes.Add("Update release notes");
        if (draft.Metadata is {} metadata && baseline != null && metadata != new CreatePackRequest(baseline.Name, baseline.MinecraftVersion, baseline.Loader, baseline.LoaderVersion))
            changes.Add($"Update pack settings: {metadata.Name} · Minecraft {metadata.MinecraftVersion} · {LoaderName(metadata.Loader)} {metadata.LoaderVersion}");
        if (draft.BaseReleaseId == null && changes.Count == 0) changes.Add("Publish first release");
        return changes;
    }
    private void RefreshDraft()
    {
        if (fileList == null || draft == null) return;
        fileList.Items.Clear();
        var visible = draft.Files.Where(x => FileName(x.File).Contains(modFilter, StringComparison.OrdinalIgnoreCase)
            || GroupName(x.File.Group).Contains(modFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (visible.Count == 0) fileList.Items.Add(new ListBoxItem { Content = Text(draft.Files.Count == 0 ? "No mods added yet." : "No mods match your search.", 13, false, "#ADBAB1"), IsEnabled = false });
        foreach (var group in new[] { "base", "voxy", "distanthorizons" })
        {
            var files = visible.Where(x => x.File.Group == group).OrderBy(x => x.File.Path, StringComparer.OrdinalIgnoreCase).ToList();
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
        if (draftSummary != null) draftSummary.Text = $"{draft.Files.Count} mod files · {(draft.HasChanges ? Changes().Count + " unpublished changes" : "No unpublished changes")} · Draft saved on this PC";
        if (publishButton != null) publishButton.IsEnabled = draft.BaseReleaseId == published?.ReleaseId && draft.HasChanges;
    }
    private void ReviewPublish()
    {
        if (draft == null || !draft.HasChanges || draft.BaseReleaseId != published?.ReleaseId) return;
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
        var host = selectedHost!; var pack = selectedPack!; var current = draft!;
        if (!current.HasChanges) { Status("There are no changes to publish."); return; }
        using var api = CreateOwnerApi(host);
        if (await RefreshStaleDraft(api, pack, ct)) return;
        Status("Checking mod identities before uploading…");
        await Task.Run(async () =>
        {
            var entries = new List<(PackFile File, IReadOnlyList<string> Ids)>();
            foreach (var item in current.Files)
            {
                var path = await DraftModImport.GetSourceAsync(item, store.Root, api, pack.Id, ct);
                entries.Add((item.File, ModFiles.ReadModIds(path)));
            }
            ModIdentityValidation.ValidateGroups(entries);
        }, ct);
        foreach (var item in current.Files.Where(f => f.LocalPath != null).DistinctBy(f => f.File.Sha256))
        { Status("Uploading " + FileName(item.File) + "…"); await Task.Run(() => api.UploadAsync(pack.Id, item.LocalPath!, item.File, null, ct), ct); }
        var metadata = current.Metadata ?? new(pack.Name, pack.MinecraftVersion, pack.Loader, pack.LoaderVersion);
        try
        {
            published = await api.PublishAsync(pack.Id, new(metadata.Name, metadata.MinecraftVersion, metadata.Loader, metadata.LoaderVersion, current.Files.Select(f => f.File).ToList(), current.Notes, current.BaseReleaseId), ct);
        }
        catch (HostApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        { if (await RefreshStaleDraft(api, pack, ct)) return; throw; }
        selectedPack = pack with { Name = metadata.Name, MinecraftVersion = metadata.MinecraftVersion, Loader = metadata.Loader, LoaderVersion = metadata.LoaderVersion, ReleaseId = published.ReleaseId };
        draft = DraftState.From(selectedPack, published);
        ownerPackCache.Remove(host.Id); SaveDraft(); AddOwnedPackToLibrary(host, selectedPack); RenderEditor(); Status("Release published. Open Members to invite and approve players.");
    });
    private async Task<bool> RefreshStaleDraft(HearthApi api, PackSummary pack, CancellationToken ct)
    {
        var latestPack = (await api.ListPacksAsync(ct)).FirstOrDefault(p => p.Id == pack.Id)
            ?? throw new InvalidOperationException("This pack is no longer available to this owner.");
        if (latestPack.ReleaseId == draft!.BaseReleaseId) return false;
        var latest = latestPack.ReleaseId == null ? null : await api.ManifestAsync(pack.Id, ct);
        selectedPack = latestPack; published = latest;
        ownerPackCache.Remove(selectedHost!.Id);
        RenderEditor(); Status("A newer release is available. Your draft was kept; discard it to start from the current release.");
        return true;
    }
}
