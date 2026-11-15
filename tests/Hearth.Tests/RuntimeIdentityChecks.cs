using System.Diagnostics;
using System.Text.Json;
using Hearth.Core;

internal static class RuntimeIdentityChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var sync = new PackSynchronizer(root);
        var broken = Path.Combine(sync.InstanceDirectory, "hearth-unrelated-broken", ".hearth");
        Directory.CreateDirectory(broken);
        await File.WriteAllTextAsync(Path.Combine(broken, "installed.json"), "null");
        var pin = new string('a', 64);
        check(sync.FindInstallationUrl("stable-host", "stable-pack", pin) is null,
            "An unrelated null installation record cannot block joining a pack");
        await File.WriteAllTextAsync(Path.Combine(broken, "installed.json"), "{ damaged");
        check(sync.FindInstallationUrl("stable-host", "stable-pack", pin) is null,
            "An unrelated malformed installation record is skipped without modification");
        const string original = "https://original-host.example:17843";
        var directory = sync.GetInstanceDirectory(original, "stable-pack");
        Directory.CreateDirectory(Path.Combine(directory, ".hearth"));
        var path = Path.Combine(directory, ".hearth", "installed.json");
        var manifest = new PackManifest("stable-pack", "fixture-release", "Fixture pack", "1.21.1", "fabric", "0.16.10", []);
        var installed = new InstalledPack(original, manifest.PackId, manifest.ReleaseId, TerrainMode.Off, [], manifest, "stable-host", pin);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(installed, JsonDefaults.Options));
        check(sync.FindInstallationUrl("stable-host", "stable-pack", pin) == original,
            "A verified stable host identity finds the original installation address after its endpoint moves");
        check(sync.FindInstallationUrl("stable-host", "stable-pack", new string('b', 64)) is null
            && sync.FindInstallationUrl("other-host", "stable-pack", pin) is null
            && sync.FindInstallationUrl("stable-host", "other-pack", pin) is null,
            "Matching pack names or claimed host IDs cannot reuse another certificate's installation");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(installed with { HostId = null, CertificatePin = null }, JsonDefaults.Options));
        check(sync.FindInstallationUrl("stable-host", "stable-pack", pin) is null,
            "Legacy installations without verified host identity are not guessed during an unrelated join");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(installed with { HostUrl = "https://incorrect-location.example" }, JsonDefaults.Options));
        check(sync.FindInstallationUrl("stable-host", "stable-pack", pin) is null,
            "An installation record must match its actual instance folder before reuse");
        check(await File.ReadAllTextAsync(Path.Combine(broken, "installed.json")) == "{ damaged",
            "Identity discovery preserves unrelated damaged installation records for recovery");

        if (OperatingSystem.IsWindows())
        {
            // This is the test executable under a candidate name; it only waits.
            // No Java installation or running user process is touched.
            var fixture = Path.Combine(root, "native-process-fixture"); Directory.CreateDirectory(fixture);
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(fixture, Path.GetFileName(file)), true);
            var java = Path.Combine(fixture, "java.exe"); File.Copy(Path.Combine(AppContext.BaseDirectory, "Hearth.Tests.exe"), java, true);
            var marker = "hearth-fixture-" + Guid.NewGuid().ToString("N");
            var start = new ProcessStartInfo(java) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--native-process-probe"); start.ArgumentList.Add(marker);
            using var process = Process.Start(start)!;
            try
            {
                var processes = await ProcessCommandLines.ReadAsync();
                check(processes.Any(p => p.Name.Equals("java.exe", StringComparison.OrdinalIgnoreCase) && p.Command?.Contains(marker, StringComparison.Ordinal) == true),
                    "The native Windows query reads an actual matching process command line without PowerShell");
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                try { await ProcessCommandLines.ReadAsync(canceled.Token); throw new InvalidOperationException("Canceled process query unexpectedly succeeded"); }
                catch (OperationCanceledException) { check(true, "The native process query honors cancellation"); }
                try { PrismService.EnsureNotRunning(root, ct: canceled.Token); throw new InvalidOperationException("Canceled Prism guard unexpectedly succeeded"); }
                catch (OperationCanceledException) { check(true, "Pack installation's running-process guard honors cancellation"); }
            }
            finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
        }
    }
}
