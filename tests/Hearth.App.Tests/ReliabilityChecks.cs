using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Hearth.App;
using Hearth.Core;

internal static class ReliabilityChecks
{
    public static void Run(string root, Action<bool, string> check, Action<Func<bool>, string> wait)
    {
        var profileRoot = Path.Combine(root, "profile");
        var profile = new ProfileStore(profileRoot);
        profile.State.DisplayName = "First saved profile"; profile.State.LocalHostOwnerToken = "owner-key-fixture"; profile.Save();
        profile.State.DisplayName = "Second saved profile"; profile.Save();
        var file = Path.Combine(profileRoot, "profile.bin");
        check(File.Exists(file + ".bak") && new ProfileStore(profileRoot).State.DisplayName == "Second saved profile", "Profile replacement retains an encrypted previous backup");
        File.WriteAllText(file, "damaged primary");
        var recovered = new ProfileStore(profileRoot);
        check(recovered.State.DisplayName == "First saved profile" && recovered.State.LocalHostOwnerToken == "owner-key-fixture" && recovered.RecoveryNotice != null, "Corrupt primary recovers owner credentials from backup with a notice");
        check(Directory.GetFiles(profileRoot, "profile.bin.unreadable-*").Length == 1 && new ProfileStore(profileRoot).State.DisplayName == "First saved profile", "Recovery preserves damaged bytes and restores a readable primary");
        File.WriteAllText(file, "damaged primary again"); File.WriteAllText(file + ".bak", "damaged backup");
        var empty = new ProfileStore(profileRoot);
        check(empty.State.Hosts.Count == 0 && empty.RecoveryNotice!.Contains("kept for recovery") && Directory.GetFiles(profileRoot, "*.unreadable-*").Length == 3, "Two damaged profiles are preserved and a usable new profile opens");
        empty.Save(); check(new ProfileStore(profileRoot).RecoveryNotice == null, "A recovered empty profile can be saved normally");

        empty.State.LocalHostOwnerToken = "backup-key"; empty.Save(); empty.Save();
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var lockedRecovery = new ProfileStore(profileRoot);
            check(lockedRecovery.State.LocalHostOwnerToken == "backup-key" && lockedRecovery.RecoveryNotice!.Contains(file), "Locked primary still opens the valid backup and reports where the original remains");
        }

        var attempts = 0; var clipboardStatus = ""; var uiThread = Environment.CurrentManagedThreadId;
        var clipboard = ClipboardService.SetAsync(new DataObject(), value => clipboardStatus = value, "Copied", _ =>
        {
            check(Environment.CurrentManagedThreadId == uiThread, "Clipboard retry stays on the UI dispatcher");
            if (++attempts < 4) throw new ExternalException("Clipboard busy");
        });
        wait(() => clipboard.IsCompleted, "Clipboard retry completes");
        check(clipboard.GetAwaiter().GetResult() && attempts == 4 && clipboardStatus == "Copied", "A briefly locked clipboard retries successfully");
        clipboard = ClipboardService.SetAsync(new DataObject(), value => clipboardStatus = value, "Copied", _ => throw new ExternalException("Clipboard busy"));
        wait(() => clipboard.IsCompleted, "Persistent clipboard lock completes without throwing");
        check(!clipboard.GetAwaiter().GetResult() && clipboardStatus.Contains("Try Copy again"), "A held clipboard produces a status message instead of killing hosting");

        var worker = RunWorkerChecks(root, uiThread, check);
        wait(() => worker.IsCompleted, "File work finishes while the dispatcher keeps pumping"); worker.GetAwaiter().GetResult();

        var installation = InstallResponsivenessChecks.RunAsync(Path.Combine(root, "large-install"), check);
        wait(() => installation.IsCompleted, "The 150-mod installation and verification responsiveness checks finish"); installation.GetAwaiter().GetResult();

        using var fixture = new LibraryFixture();
        var local = new ProfileStore(Path.Combine(root, "local-off"));
        local.State.LocalHostConnectionId = "local"; local.State.LocalHostOwnerToken = "local-secret";
        local.State.Hosts.Add(new("local", "This PC", fixture.Url, "local-secret", "local-host"));
        local.State.Packs.Add(new(fixture.Url, "fixture-pack", "Owned pack", "local-secret", "owner", HostId: "local-host"));
        var window = new MainWindow(local);
        try
        {
            check(((TextBlock)window.FindName("PackStatusText")).Text == "Hosting is off on this PC" && fixture.Requests == 0, "Stopped local hosting never sends the owner key to a loopback listener");
            var localJoin = PackJoining.JoinAsync(local, new Invite(fixture.Url, "fixture-pack", "forwarded-code"), "Owner");
            wait(() => localJoin.IsCompleted, "Stopped local invite check completes");
            check(localJoin.IsFaulted && localJoin.Exception!.GetBaseException().Message.Contains("Hosting is off") && fixture.Requests == 0,
                "A pasted local invite cannot forward the stopped host's owner key to an unrelated loopback listener");
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var run = (Task)typeof(MainWindow).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window,
                ["Downloading test pack", new Func<CancellationToken, Task>(ct => finish.Task.WaitAsync(ct)), true])!;
            check(((FrameworkElement)window.FindName("SidebarPanel")).IsEnabled && ((FrameworkElement)window.FindName("MainContent")).IsEnabled, "A pack download leaves browsing controls enabled");
            ((Button)window.FindName("SettingsNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(((TextBlock)window.FindName("PageTitle")).Text == "Settings", "The player can open Settings during a download");
            finish.SetResult(); wait(() => run.IsCompleted, "Background operation finishes without changing the selected page");
            check(((TextBlock)window.FindName("PageTitle")).Text == "Settings", "Finishing background work preserves navigation");
        }
        finally { window.Close(); }

        fixture.Access = "approved";
        var setup = new ProfileStore(Path.Combine(root, "setup-cancellation"));
        setup.State.Packs.Add(new(fixture.Url, "fixture-pack", "Fixture pack", "player-token", "player"));
        var setupWindow = new MainWindow(setup);
        try
        {
            var play = (Button)setupWindow.FindName("PlayButton");
            wait(() => Equals(play.Content, "Set up Prism") && play.IsEnabled, "The setup-required pack is ready for its primary action");
            var run = (Task)typeof(MainWindow).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(setupWindow,
                ["Downloading Prism", new Func<CancellationToken, Task>(_ => Task.FromCanceled(new CancellationToken(true))), false])!;
            wait(() => run.IsCompleted, "Canceled foreground setup finishes"); run.GetAwaiter().GetResult();
            wait(() => Equals(play.Content, "Set up Prism") && play.IsEnabled, "Canceling foreground setup restores its primary action without leaving the library");
            check(((FrameworkElement)setupWindow.FindName("Page")).IsEnabled,
                "Foreground cancellation also restores the page controls");
        }
        finally { setupWindow.Close(); }

        var emptyWindow = new MainWindow(new ProfileStore(Path.Combine(root, "empty-resume")));
        try
        {
            var run = (Task)typeof(MainWindow).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(emptyWindow,
                ["Resuming hosting", new Func<CancellationToken, Task>(_ => Task.CompletedTask), true])!;
            wait(() => run.IsCompleted, "Background hosting resume finishes on an empty library"); run.GetAwaiter().GetResult();
            var play = (Button)emptyWindow.FindName("PlayButton");
            check(Equals(play.Content, "Add modpack") && play.IsEnabled,
                "An empty library restores Add modpack after background hosting resume");
        }
        finally { emptyWindow.Close(); }
    }
    private static async Task RunWorkerChecks(string root, int uiThread, Action<bool, string> check)
    {
        var source = Path.Combine(root, "instances"); Directory.CreateDirectory(Path.Combine(source, "hearth-test", ".minecraft"));
        File.WriteAllText(Path.Combine(source, "hearth-test", ".minecraft", "options.txt"), "personal settings");
        var guardThreads = new List<int>();
        await InstanceStorage.MoveAsync(Path.Combine(root, "move-profile"), source, Path.Combine(root, "moved"), _ => { }, () => guardThreads.Add(Environment.CurrentManagedThreadId));
        check(guardThreads.Count >= 2 && guardThreads.All(id => id != uiThread), "Instance move and process guards run outside the UI thread");
        var jar = Path.Combine(root, "large.jar");
        using (var archive = ZipFile.Open(jar, ZipArchiveMode.Create))
        {
            using (var metadata = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open())) metadata.Write("{\"schemaVersion\":1,\"id\":\"large_fixture\",\"version\":\"1.0\"}");
            using var payload = archive.CreateEntry("payload", CompressionLevel.NoCompression).Open();
            var block = new byte[1024 * 1024]; for (var i = 0; i < 32; i++) payload.Write(block);
        }
        var watch = Stopwatch.StartNew(); var import = DraftModImport.StageAsync([jar], root, "voxy", CancellationToken.None); var returnTime = watch.Elapsed;
        var staged = await import;
        check(returnTime < TimeSpan.FromMilliseconds(200) && staged.Single().File.Group == "voxy", "Large JAR staging yields immediately and preserves the optional group");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await DraftModImport.StageAsync([jar], root, "base", cancel.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { check(true, "Worker-based JAR staging still honors cancellation"); }
    }
}


