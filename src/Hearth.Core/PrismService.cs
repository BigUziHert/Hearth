using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace Hearth.Core;

public sealed class PrismService(string profileRoot)
{
    public string ProfileRoot { get; } = Path.GetFullPath(profileRoot);
    public string PrismDataDirectory => Path.Combine(ProfileRoot, "Prism");
    public string? FindExecutable()
    {
        var manifest = Path.Combine(ProfileRoot, "tools", "prism-path.txt");
        if (File.Exists(manifest))
        {
            var path = File.ReadAllText(manifest).Trim();
            if (File.Exists(path) && Path.GetFileName(path).Equals("prismlauncher.exe", StringComparison.OrdinalIgnoreCase)) return path;
        }
        return null;
    }
    public async Task<string> InstallAsync(IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        var existing = FindExecutable();
        if (existing is not null) return existing;
        using var github = new GithubUpdater();
        using var metadata = await github.GetLatestAsync("PrismLauncher/PrismLauncher", ct);
        var release = metadata.RootElement;
        var asset = release.GetProperty("assets").EnumerateArray().FirstOrDefault(a => (a.GetProperty("name").GetString() ?? "").StartsWith("PrismLauncher-Windows-MinGW-w64-Portable-", StringComparison.Ordinal) && a.GetProperty("name").GetString()!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (asset.ValueKind == JsonValueKind.Undefined) throw new IOException("Prism's latest release has no Windows x64 portable package. Install Prism from prismlauncher.org, then choose its executable in Hearth.");
        var hash = await github.AssetHashAsync(release, asset, "PrismLauncher/PrismLauncher", ct);
        var url = asset.GetProperty("browser_download_url").GetString()!;
        github.ValidateDownloadUrl(url, "PrismLauncher/PrismLauncher");
        var version = release.GetProperty("tag_name").GetString()!;
        var download = new UpdateRelease(version, "", "", asset.GetProperty("name").GetString()!, url, hash, asset.GetProperty("size").GetInt64());
        var tools = Path.Combine(ProfileRoot, "tools");
        var zip = await github.DownloadAsync(download, Path.Combine(tools, "downloads"), progress, ct);
        var staging = Path.Combine(tools, "prism-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            progress?.Report(new("Setting up Prism Launcher…"));
            using (var archive = ZipFile.OpenRead(zip))
            {
                if (archive.Entries.Count > 20000 || archive.Entries.Sum(e => e.Length) > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("The Prism package is larger than expected.");
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var target = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (!target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains(':') || (entry.ExternalAttributes & 0xF0000000) == 0xA0000000) throw new InvalidDataException("The Prism package contains an unsafe path.");
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) Directory.CreateDirectory(target);
                    else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target, false); }
                }
            }
            var exe = Directory.GetFiles(staging, "prismlauncher.exe", SearchOption.AllDirectories).SingleOrDefault() ?? throw new IOException("The Prism package did not include the launcher.");
            Directory.CreateDirectory(PrismDataDirectory);
            // These are Prism's documented default settings. The first-run wizard and account login remain its own UI.
            var config = Path.Combine(PrismDataDirectory, "prismlauncher.cfg");
            if (!File.Exists(config)) await File.WriteAllTextAsync(config, "[General]\r\nAutomaticJavaDownload=true\r\nAutomaticJavaSwitch=true\r\n", ct);
            await File.WriteAllTextAsync(Path.Combine(tools, "prism-path.txt"), exe, ct);
            progress?.Report(new("Prism is ready. Open it and add your Microsoft account.", 1, 1));
            return exe;
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            throw;
        }
    }
    public void SetExecutable(string executable)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable) || !Path.GetFileName(executable).Equals("prismlauncher.exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose prismlauncher.exe from an existing Prism installation.");
        Directory.CreateDirectory(Path.Combine(ProfileRoot, "tools"));
        File.WriteAllText(Path.Combine(ProfileRoot, "tools", "prism-path.txt"), executable);
    }
    public Process Open(string executable) => Start(executable, null);
    public Process Launch(string executable, string instanceId)
    {
        if (instanceId != Path.GetFileName(instanceId) || !instanceId.StartsWith("hearth-", StringComparison.Ordinal) || instanceId.Contains('\\') || instanceId.Contains('/')) throw new ArgumentException("Invalid instance identifier.");
        var instance = Path.Combine(PrismDataDirectory, "instances", instanceId);
        if (!File.Exists(Path.Combine(instance, ".hearth", "installed.json"))) throw new IOException("Install this pack with Play before launching it.");
        EnsureNotRunning(instance);
        return Start(executable, instanceId);
    }
    public ProcessStartInfo BuildStartInfo(string executable, string? instanceId = null)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("Set up Prism in Settings before playing.", executable);
        var info = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))! };
        info.ArgumentList.Add("--dir");
        info.ArgumentList.Add(PrismDataDirectory);
        if (!string.IsNullOrWhiteSpace(instanceId)) { info.ArgumentList.Add("--launch"); info.ArgumentList.Add(instanceId); }
        return info;
    }
    private Process Start(string executable, string? instanceId)
    {
        Directory.CreateDirectory(PrismDataDirectory);
        return Process.Start(BuildStartInfo(executable, instanceId)) ?? throw new IOException("Windows could not start Prism.");
    }
    public static void EnsureNotRunning(string instanceDirectory)
    {
        if (!OperatingSystem.IsWindows()) return;
        var hasCandidates = false;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { hasCandidates |= process.ProcessName.Equals("prismlauncher", StringComparison.OrdinalIgnoreCase) || process.ProcessName.Equals("java", StringComparison.OrdinalIgnoreCase) || process.ProcessName.Equals("javaw", StringComparison.OrdinalIgnoreCase); }
                catch { /* A process can exit between enumeration and inspection. */ }
            }
        }
        if (!hasCandidates) return;
        // Query command lines so an unrelated Minecraft server can keep running on the same PC.
        // No user values enter the command. Instance matching happens after JSON parsing.
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("Get-CimInstance Win32_Process -Filter \"Name='prismlauncher.exe' OR Name='java.exe' OR Name='javaw.exe'\" | Select-Object Name,CommandLine | ConvertTo-Json -Compress");
        using var query = Process.Start(start) ?? throw new IOException("Could not check running Minecraft instances.");
        var outputTask = query.StandardOutput.ReadToEndAsync();
        var errorTask = query.StandardError.ReadToEndAsync();
        if (!query.WaitForExit(15000)) { try { query.Kill(); } catch { } throw new IOException("Could not verify whether Minecraft is running. Close this pack's Minecraft and Prism before updating."); }
        if (query.ExitCode != 0) throw new IOException("Could not verify whether Minecraft is running. Close this pack's Minecraft and Prism before updating.");
        var output = outputTask.GetAwaiter().GetResult();
        _ = errorTask.GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(output)) return;
        using var data = JsonDocument.Parse(output);
        var rows = data.RootElement.ValueKind == JsonValueKind.Array ? data.RootElement.EnumerateArray().ToArray() : [data.RootElement];
        var instance = Path.GetFullPath(instanceDirectory);
        var prismRoot = Directory.GetParent(Directory.GetParent(instance)!.FullName)!.FullName;
        foreach (var row in rows)
        {
            var name = row.GetProperty("Name").GetString() ?? "";
            var command = row.GetProperty("CommandLine").GetString();
            // A service or another Windows user's Java process may not expose its command line.
            // Those processes cannot normally access this user's private Hearth profile. Do not
            // block an unrelated always-on Minecraft server merely because it is protected.
            if (command is null) continue;
            if (command.Contains(instance, StringComparison.OrdinalIgnoreCase) || (name.Equals("prismlauncher.exe", StringComparison.OrdinalIgnoreCase) && command.Contains(prismRoot, StringComparison.OrdinalIgnoreCase))) throw new IOException("Close Minecraft and the Hearth Prism window before updating or switching terrain mods. Then press Play again.");
        }
    }
}
