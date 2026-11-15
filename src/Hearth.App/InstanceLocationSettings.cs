using System.IO;
using Hearth.Core;
using Microsoft.Win32;

namespace Hearth.App;

public partial class MainWindow
{
    private void AddInstanceLocationSettings()
    {
        var location = Card("Instance location", "Choose where Hearth installs your Minecraft packs. This includes each pack's worlds, mods, screenshots, and settings.");
        location.Children.Add(Text(sync.InstanceDirectory, 13, false, "#ADBAB1"));
        location.Children.Add(Row(Button("Change folder…", ChooseInstanceLocation, true), Button("Open folder", () =>
        {
            Directory.CreateDirectory(sync.InstanceDirectory); OpenFolder(sync.InstanceDirectory);
        }, true)));
        location.Children.Add(Text("Changing folders copies and verifies your existing instances before switching. Close Minecraft and Prism first. The previous folder is kept as a backup.", 12, false, "#ADBAB1"));
        if (!string.IsNullOrWhiteSpace(store.State.PreviousInstanceDirectory) && Directory.Exists(store.State.PreviousInstanceDirectory))
        {
            location.Children.Add(Text("Previous folder (backup): " + store.State.PreviousInstanceDirectory, 12, false, "#ADBAB1"));
            location.Children.Add(Row(Button("Open previous folder", () => OpenFolder(store.State.PreviousInstanceDirectory), true)));
        }
    }

    private void ChooseInstanceLocation()
    {
        var picker = new OpenFolderDialog { Title = "Choose an empty folder for your Minecraft instances", Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        _ = ChangeInstanceLocation(picker.FolderName);
    }

    private Task ChangeInstanceLocation(string destination) => Run("Moving Minecraft instances…", async ct =>
    {
        var source = sync.InstanceDirectory;
        var oldPreference = store.State.InstanceDirectory;
        var oldBackup = store.State.PreviousInstanceDirectory;
        void CheckRunning()
        {
            // Match the whole instance library, plus Prism's unchanged account/data directory.
            PrismService.EnsureNotRunning(source, prism.PrismDataDirectory, ct);
            PrismService.EnsureNotRunning(destination, prism.PrismDataDirectory, ct);
        }
        await InstanceStorage.MoveAsync(store.Root, source, destination, target => Dispatcher.Invoke(() =>
        {
            store.State.InstanceDirectory = target;
            store.State.PreviousInstanceDirectory = Directory.Exists(source) ? source : "";
            try { store.Save(); }
            catch { store.State.InstanceDirectory = oldPreference; store.State.PreviousInstanceDirectory = oldBackup; throw; }
            sync = new(store.Root, target); prism = new(store.Root, target);
        }), CheckRunning, new Progress<SyncProgress>(p => Status(p.TotalBytes > 0 ? $"{p.Message} {p.Percent:0}%" : p.Message)), ct);
        ShowSettings();
        Status("Instance location saved. Your packs will use this folder the next time you press Play.");
    });
}
