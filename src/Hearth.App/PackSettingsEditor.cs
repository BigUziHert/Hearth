using System.Windows;
using System.Windows.Controls;
using Hearth.Core;

namespace Hearth.App;

public partial class MainWindow
{
    private Button? publishButton;
    private void AddPackSettings()
    {
        var current = draft!; var pack = selectedPack!;
        current.Metadata ??= new(pack.Name, pack.MinecraftVersion, pack.Loader, pack.LoaderVersion);
        var data = current.Metadata;
        var settings = Card("Pack settings", "Players need a Minecraft server using the matching game and loader versions. Changing these values does not update your world server.");
        var name = Field(settings, "Pack name", data.Name);
        settings.Children.Add(Text("Minecraft version", 12, true));
        var minecraft = new ComboBox { IsEditable = true, Text = data.MinecraftVersion }; settings.Children.Add(minecraft);
        var loader = Select(settings, "Mod loader", ["Fabric", "Forge", "NeoForge", "Quilt"], LoaderName(data.Loader));
        settings.Children.Add(Text("Loader version", 12, true));
        var version = new ComboBox { IsEditable = true, Text = data.LoaderVersion }; settings.Children.Add(version);
        var hint = Text("", 12, false, "#ADBAB1"); settings.Children.Add(hint);
        bool populating = false;
        CancellationTokenSource? request = null;
        void Save()
        {
            if (populating || !ReferenceEquals(draft, current)) return;
            current.Metadata = new(name.Text.Trim(), minecraft.Text.Trim(), loader.SelectedItem!.ToString()!.ToLowerInvariant(), version.Text.Trim());
            SaveDraftSafely(); RefreshDraft();
        }
        async Task LoadVersions(bool clear)
        {
            request?.Cancel(); request?.Dispose(); request = new(); var ct = request.Token;
            var game = minecraft.Text.Trim(); var kind = loader.SelectedItem!.ToString()!.ToLowerInvariant();
            if (clear) { version.ItemsSource = null; version.Text = ""; Save(); }
            hint.Text = "Loading matching loader versions…";
            try
            {
                await Task.Delay(300, ct);
                using var catalog = new VersionCatalog(); var versions = await catalog.LoaderAsync(kind, game, ct);
                if (ct.IsCancellationRequested || !settings.IsDescendantOf(Page)) return;
                populating = true; var entered = version.Text; version.ItemsSource = versions; version.Text = entered;
                if (string.IsNullOrWhiteSpace(entered) && versions.Count > 0) version.SelectedIndex = 0;
                populating = false; Save(); hint.Text = versions.Count == 0 ? "No matching loader version found. Enter a version you have tested or choose another Minecraft version." : "Matching loader versions loaded.";
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (settings.IsDescendantOf(Page)) hint.Text = "Couldn't load versions. You can enter the versions used by your server."; }
        }
        name.TextChanged += (_, _) => Save();
        version.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => Save()));
        minecraft.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => { if (!populating) { Save(); _ = LoadVersions(true); } }));
        loader.SelectionChanged += (_, _) => { if (!populating) { Save(); _ = LoadVersions(true); } };
        _ = LoadMinecraft(); _ = LoadVersions(false);
        async Task LoadMinecraft()
        {
            try
            {
                using var catalog = new VersionCatalog(); var versions = await catalog.MinecraftAsync();
                if (!settings.IsDescendantOf(Page)) return;
                populating = true; var entered = minecraft.Text; minecraft.ItemsSource = versions; minecraft.Text = entered; populating = false;
            }
            catch (Exception) { }
        }
    }

    private void ConfirmArchivePack()
    {
        var pack = selectedPack!; var host = selectedHost!;
        if (MessageBox.Show(this, $"Archive {pack.Name}? It will disappear from this host's pack list and members will lose download access. Its files and history are kept in the host's archive folder.", "Archive pack", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _ = Run("Archiving pack…", async ct =>
        {
            using var api = CreateOwnerApi(host);
            await api.ArchivePackAsync(pack.Id, ct);
            ownerPackCache.Remove(host.Id); selectedPack = null; published = null; draft = null;
            ShowOwnerPackList(); Status("Pack archived. Its files and release history are preserved on the host.");
        });
    }
}
