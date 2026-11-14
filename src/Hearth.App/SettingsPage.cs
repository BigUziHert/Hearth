using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hearth.Core;
using Microsoft.Win32;

namespace Hearth.App;
public partial class MainWindow
{
    private UpdateRelease? availableLauncherUpdate;
    private string? downloadedLauncherUpdatePath;
    private string launcherUpdateMessage = "";

    private void ShowSettings()
    {
        PageStart("settings", "Settings", "HEARTH");
        const string source = AppInfo.DefaultUpdateRepository;
        if (downloadedLauncherUpdatePath != null && (!File.Exists(downloadedLauncherUpdatePath) || !File.Exists(downloadedLauncherUpdatePath + ".sha256"))) downloadedLauncherUpdatePath = null;

        var updates = Card("Updates", $"Hearth {AppInfo.Version}");
        updates.Children.Add(Text("Official Hearth releases", 13, false, "#A4AFAA"));
        var updateMessage = Text("", 14); updates.Children.Add(updateMessage);
        var releaseNotes = new Expander { Header = "What's new", Margin = new(0, 3, 0, 10), Visibility = Visibility.Collapsed };
        updates.Children.Add(releaseNotes);
        var updateActions = Row(); updates.Children.Add(updateActions);

        void RenderUpdate()
        {
            updateMessage.Text = launcherUpdateMessage.Length > 0 ? launcherUpdateMessage : "Check for a newer version of Hearth.";
            releaseNotes.Visibility = availableLauncherUpdate != null && !string.IsNullOrWhiteSpace(availableLauncherUpdate.Notes) ? Visibility.Visible : Visibility.Collapsed;
            releaseNotes.Content = availableLauncherUpdate == null ? null : new ScrollViewer { MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 9, 0, 0), Content = Text(availableLauncherUpdate.Notes, 13, false, "#A4AFAA") };
            updateActions.Children.Clear();
            if (downloadedLauncherUpdatePath != null)
            {
                updateActions.Children.Add(Button("Restart to update", () =>
                {
                    try { GithubUpdater.StartInstaller(downloadedLauncherUpdatePath); Application.Current.Shutdown(); }
                    catch (Exception exception)
                    {
                        launcherUpdateMessage = "The update could not start. Hearth is still open; you can retry."; RenderUpdate(); Status(exception.Message);
                        MessageBox.Show(this, exception.Message, "Update could not start", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }));
                updateActions.Children.Add(Button("Check again", () => _ = CheckUpdate(), true));
            }
            else if (availableLauncherUpdate != null)
            {
                updateActions.Children.Add(Button("Download update", () => _ = DownloadUpdate()));
                updateActions.Children.Add(Button("Check again", () => _ = CheckUpdate(), true));
            }
            else updateActions.Children.Add(Button("Check for updates", () => _ = CheckUpdate()));
        }

        async Task CheckUpdate()
        {
            availableLauncherUpdate = null; downloadedLauncherUpdatePath = null; launcherUpdateMessage = "Checking for updates…"; RenderUpdate();
            await Run("Checking for a Hearth update…", async ct =>
            {
                try
                {
                    using var updater = new GithubUpdater();
                    availableLauncherUpdate = await updater.CheckAsync(source, AppInfo.Version, ct);
                    launcherUpdateMessage = availableLauncherUpdate == null ? "You're up to date." : $"Hearth {availableLauncherUpdate.Version} is available.";
                    Status(launcherUpdateMessage);
                }
                catch (ReleaseUnavailableException)
                {
                    launcherUpdateMessage = "No Hearth release has been published yet. Check again later.";
                    Status(launcherUpdateMessage);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { launcherUpdateMessage = "Update check cancelled. You can try again."; throw; }
                catch (Exception) { launcherUpdateMessage = "Couldn't check for updates. Check your connection and try again."; throw; }
                finally { RenderUpdate(); }
            });
        }

        async Task DownloadUpdate()
        {
            var release = availableLauncherUpdate;
            if (release == null) return;
            launcherUpdateMessage = "Downloading and verifying the update…"; RenderUpdate();
            var downloadInProgress = true;
            await Run(launcherUpdateMessage, async ct =>
            {
                try
                {
                    using var updater = new GithubUpdater();
                    downloadedLauncherUpdatePath = await updater.DownloadAsync(release, Path.Combine(store.Root, "Updates"), new Progress<SyncProgress>(progress =>
                    {
                        if (!downloadInProgress) return;
                        updateMessage.Text = progress.TotalBytes > 0 ? $"Downloading update… {progress.Percent:0}%" : progress.Message;
                        Status(progress.Message);
                    }), ct);
                    launcherUpdateMessage = $"Hearth {release.Version} is ready. Restart when you're ready to install it.";
                    Status("Update verified. Your packs and local data will be kept.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { launcherUpdateMessage = "Download cancelled. You can retry when you're ready."; throw; }
                catch (Exception) { launcherUpdateMessage = "The download didn't finish. Your current app is unchanged; try again."; throw; }
                finally { downloadInProgress = false; RenderUpdate(); }
            });
        }
        RenderUpdate();

        var minecraft = Card("Minecraft", AvailablePrismExe() == null ? "Set up Prism to launch your packs." : "Prism is ready to launch your packs.");
        minecraft.Children.Add(Text("Use your own Microsoft account in Prism. Hearth keeps your packs in separate instances.", 13, false, "#A4AFAA"));
        if (AvailablePrismExe() == null) minecraft.Children.Add(Row(Button("Set up Prism", () => _ = InstallPrism())));
        else minecraft.Children.Add(Row(Button("Open Prism", () => _ = Run("Opening Prism…", _ => { prism.Open(PrismExe()); Status("Manage your Minecraft account in Prism → Settings → Accounts."); return Task.CompletedTask; }), true)));
        minecraft.Children.Add(Row(Button("Choose existing Prism…", () =>
        {
            var dialog = new OpenFileDialog { Filter = "Prism Launcher|prismlauncher.exe" };
            if (dialog.ShowDialog(this) != true) return;
            prism.SetExecutable(dialog.FileName); store.State.PrismExecutable = dialog.FileName; store.Save(); ShowSettings(); Status("Prism installation saved.");
        }, true)));

        var profile = Card("Profile");
        var displayName = Field(profile, "Name shown to pack owners", store.State.DisplayName);
        profile.Children.Add(Row(Button("Save name", () =>
        {
            if (string.IsNullOrWhiteSpace(displayName.Text)) { Status("Enter the name your friends know you by."); displayName.Focus(); return; }
            store.State.DisplayName = displayName.Text.Trim(); store.Save(); ProfileLabel.Text = store.State.DisplayName.ToUpperInvariant();
            Status("Name saved. New access requests will use this name.");
        }, true)));

        var help = Card("Help");
        help.Children.Add(Row(Button("Open guide", OpenGuide, true), Button("Open data folder", () => OpenFolder(store.Root), true)));
    }
}
