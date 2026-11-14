using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows;
using Hearth.App;
using Hearth.Core;

internal static class InstanceLocationChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var profile = Path.Combine(root, "profile"); var source = InstanceStorage.DefaultDirectory(profile);
        var target = Path.Combine(root, "other drive", "Minecraft instances");
        Directory.CreateDirectory(Path.Combine(source, "hearth-fixture", ".minecraft", "saves", "My world"));
        var relativeFiles = new Dictionary<string, byte[]>
        {
            ["hearth-fixture/.minecraft/saves/My world/level.dat"] = Enumerable.Range(0, 255).Select(x => (byte)x).ToArray(),
            ["hearth-fixture/.minecraft/options.txt"] = "key_key.forward:87\ngraphicsMode:2"u8.ToArray(),
            ["hearth-fixture/.minecraft/mods/custom.jar"] = "custom fixture bytes"u8.ToArray(),
            ["hearth-fixture/.hearth/installed.json"] = "installed manifest"u8.ToArray(),
            ["hearth-fixture/instance.cfg"] = "[General]\r\nMaxMemAlloc=6144"u8.ToArray()
        };
        foreach (var (relative, bytes) in relativeFiles)
        {
            var path = Path.Combine(source, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
        }
        var selected = source; int guardCalls = 0;
        await InstanceStorage.MoveAsync(profile, source, target, value => selected = value, () => guardCalls++);
        check(selected == target && guardCalls >= 2, "Instance migration switches only after checking running games before and after copying");
        check(relativeFiles.All(x => File.ReadAllBytes(Path.Combine(target, x.Key)).SequenceEqual(x.Value)), "Instance migration preserves world, keybind, custom mod, memory settings, and managed-file record bytes");
        check(relativeFiles.All(x => File.ReadAllBytes(Path.Combine(source, x.Key)).SequenceEqual(x.Value)), "Verified instance migration keeps the previous folder as a complete backup");
        check(Directory.Exists(Path.Combine(target, "hearth-fixture", ".minecraft", "saves", "My world")), "Instance migration preserves personal folder hierarchy");
        var newSync = new PackSynchronizer(profile, target); var newPrism = new PrismService(profile, target);
        check(newSync.GetInstanceDirectory("http://127.0.0.1:17842", "pack").StartsWith(target + Path.DirectorySeparatorChar), "Pack synchronizer installs directly under the chosen instance location");
        Directory.CreateDirectory(newPrism.PrismDataDirectory);
        var config = Path.Combine(newPrism.PrismDataDirectory, "prismlauncher.cfg");
        var accounts = Path.Combine(newPrism.PrismDataDirectory, "accounts.json");
        File.WriteAllText(accounts, "account fixture kept in place");
        File.WriteAllText(config, "[General]\r\nInstanceDir=old\r\nMaxMemAlloc=6144\r\n[Other]\r\nUntouched=yes\r\n");
        newPrism.ConfigureInstanceDirectory();
        var configText = File.ReadAllText(config);
        check(configText.Contains("InstanceDir=\"" + target.Replace('\\', '/') + "\"") && configText.Contains("MaxMemAlloc=6144") && configText.Contains("Untouched=yes"), "Prism receives the exact selected path while retaining unrelated settings");
        check(File.ReadAllText(accounts) == "account fixture kept in place", "Changing instance location keeps Microsoft account data in its existing Prism folder");
        var instance = newSync.GetInstanceDirectory("http://127.0.0.1:17842", "pack");
        check(PrismService.IsInstanceProcess("prismlauncher.exe", "prismlauncher.exe --dir \"" + newPrism.PrismDataDirectory + "\"", instance, newPrism.PrismDataDirectory), "Custom instance location still detects its Prism process under the original data folder");
        check(PrismService.IsInstanceProcess("javaw.exe", "javaw.exe --gameDir \"" + Path.Combine(instance, ".minecraft") + "\"", target, newPrism.PrismDataDirectory), "Migration running-game guard matches every Minecraft instance within the selected library");
        check(!PrismService.IsInstanceProcess("java.exe", "java.exe -jar \"C:\\Unrelated server\\server.jar\"", target, newPrism.PrismDataDirectory), "An unrelated Minecraft server does not block changing instance location");

        var blocked = Path.Combine(root, "occupied"); Directory.CreateDirectory(blocked); File.WriteAllText(Path.Combine(blocked, "precious.txt"), "keep");
        await Reject(() => InstanceStorage.MoveAsync(profile, target, blocked, _ => throw new Exception("Must not switch"), () => { }), "Migration refuses to merge with an occupied target", check);
        check(File.ReadAllText(Path.Combine(blocked, "precious.txt")) == "keep", "Occupied destination remains unchanged");
        await Reject(() => InstanceStorage.MoveAsync(profile, target, Path.Combine(target, "nested"), _ => { }, () => { }), "Migration rejects a nested destination", check);
        await Reject(() => InstanceStorage.MoveAsync(profile, target, root, _ => { }, () => { }), "Migration rejects a parent destination", check);
        await Reject(() => InstanceStorage.MoveAsync(profile, target, Path.Combine(root, "blocked-by-running"), _ => { }, () => throw new IOException("Game running")), "Running Minecraft blocks migration before files or preferences change", check);
        await Reject(() => InstanceStorage.MoveAsync(profile, Path.Combine(root, "unplugged-drive"), Path.Combine(root, "empty-replacement"), _ => throw new Exception("Must not switch"), () => { }), "An unavailable custom instance location cannot silently become an empty library", check);

        var cancelledTarget = Path.Combine(root, "cancelled"); var original = selected;
        using (var cancellation = new CancellationTokenSource())
        {
            var progress = new InlineProgress(_ => cancellation.Cancel());
            try { await InstanceStorage.MoveAsync(profile, target, cancelledTarget, value => selected = value, () => { }, progress, cancellation.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
        }
        check(selected == original && !Directory.Exists(cancelledTarget) && relativeFiles.All(x => File.ReadAllBytes(Path.Combine(target, x.Key)).SequenceEqual(x.Value)), "Cancellation during copying leaves the selected installation usable and removes partial files");

        var commitTarget = Path.Combine(root, "retry-after-save-failed");
        await Reject(() => InstanceStorage.MoveAsync(profile, target, commitTarget, _ => throw new IOException("Simulated settings save failure"), () => { }), "Failed preference save keeps the original installation selected", check);
        check(File.Exists(Path.Combine(profile, "instance-move.json")) && Directory.Exists(commitTarget), "A completed copy awaiting preference save has a durable recovery record");
        await InstanceStorage.MoveAsync(profile, target, commitTarget, value => selected = value, () => { });
        check(selected == commitTarget && !File.Exists(Path.Combine(profile, "instance-move.json")), "Retry re-verifies and adopts the completed copy after an interrupted preference save");

        var interruptedTarget = Path.Combine(root, "interrupted-copy"); var id = Guid.NewGuid().ToString("N"); var staging = interruptedTarget + ".hearth-moving-" + id;
        Directory.CreateDirectory(staging); File.WriteAllText(Path.Combine(staging, "partial.bin"), "interrupted");
        File.WriteAllText(Path.Combine(profile, "instance-move.json"), JsonSerializer.Serialize(new { Source = commitTarget, Target = interruptedTarget, Staging = staging, Id = id }));
        await InstanceStorage.MoveAsync(profile, commitTarget, interruptedTarget, value => selected = value, () => { });
        check(!Directory.Exists(staging) && relativeFiles.All(x => File.ReadAllBytes(Path.Combine(interruptedTarget, x.Key)).SequenceEqual(x.Value)), "Retry cleans recorded partial staging and recovers from a process interruption");

        var emptyProfile = Path.Combine(root, "new-profile"); var defaultFolder = InstanceStorage.DefaultDirectory(emptyProfile);
        var emptyExternal = Path.Combine(root, "new-library"); string? emptySelected = null;
        await InstanceStorage.MoveAsync(emptyProfile, defaultFolder, emptyExternal, value => emptySelected = value, () => { });
        await InstanceStorage.MoveAsync(emptyProfile, emptyExternal, defaultFolder, value => emptySelected = value, () => { });
        check(emptySelected == defaultFolder, "A new library can return to its empty default instance folder");

        if (OperatingSystem.IsWindows())
        {
            var links = Path.Combine(root, "linked-target");
            try
            {
                var create = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
                create.ArgumentList.Add("-NoProfile"); create.ArgumentList.Add("-NonInteractive"); create.ArgumentList.Add("-Command");
                create.ArgumentList.Add("New-Item -ItemType Junction -Path '" + links.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' -ErrorAction Stop | Out-Null");
                using var helper = System.Diagnostics.Process.Start(create)!; await helper.WaitForExitAsync();
                if (helper.ExitCode != 0) throw new IOException("Could not create fixture junction.");
                await Reject(() => InstanceStorage.MoveAsync(profile, interruptedTarget, links, _ => { }, () => { }), "Migration refuses a linked destination", check);
            }
            finally { if (Directory.Exists(links)) Directory.Delete(links); }
        }
    }

    public static void RunUi(string root, Action<bool, string> check, Action<Func<bool>, string> awaitUi)
    {
        var profile = new ProfileStore(Path.Combine(root, "profile")); profile.State.DisplayName = "Storage tester"; profile.Save();
        var source = InstanceStorage.DefaultDirectory(profile.Root); Directory.CreateDirectory(Path.Combine(source, "hearth-ui", ".minecraft"));
        File.WriteAllText(Path.Combine(source, "hearth-ui", ".minecraft", "options.txt"), "personal keys");
        var window = new MainWindow(profile); window.Show();
        try
        {
            Invoke(window, "ShowSettings");
            check(Descendants<TextBlock>(window).Any(x => x.Text == "Instance location") && Descendants<Button>(window).Any(x => Equals(x.Content, "Change folder…")), "Settings exposes an instance folder picker and current location");
            var destination = Path.Combine(root, "Player-selected folder");
            var move = (Task)Invoke(window, "ChangeInstanceLocation", destination)!;
            awaitUi(() => move.IsCompleted, "Settings instance migration completes"); move.GetAwaiter().GetResult();
            var reloaded = new ProfileStore(profile.Root);
            check(reloaded.State.InstanceDirectory == destination && reloaded.State.DisplayName == "Storage tester", "Instance location persists without losing the player profile");
            var sync = (PackSynchronizer)typeof(MainWindow).GetField("sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var prism = (PrismService)typeof(MainWindow).GetField("prism", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            check(sync.InstanceDirectory == destination && prism.InstanceDirectory == destination, "Settings switches both the synchronizer and Prism handoff to the same instance location");
            check(reloaded.State.PreviousInstanceDirectory == source && Descendants<Button>(window).Any(x => Equals(x.Content, "Open previous folder")), "Settings clearly exposes the retained original folder as a backup");
        }
        finally { window.Close(); }
    }
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    private static IEnumerable<T> Descendants<T>(DependencyObject item) where T : DependencyObject
    {
        if (item is T match) yield return match;
        foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>())
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
    }
    private static async Task Reject(Func<Task> action, string label, Action<bool, string> check)
    {
        try { await action(); throw new InvalidOperationException("Expected failure: " + label); }
        catch (IOException) { check(true, label); }
    }
    private sealed class InlineProgress(Action<SyncProgress> callback) : IProgress<SyncProgress>
    { public void Report(SyncProgress value) => callback(value); }
}
