using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace Hearth.Core;

public sealed class PrismService(string profileRoot, string? instanceDirectory = null)
{
    public string ProfileRoot { get; } = Path.GetFullPath(profileRoot);
    public string InstanceDirectory { get; } = InstanceStorage.Resolve(profileRoot, instanceDirectory);
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
        var instance = Path.Combine(InstanceDirectory, instanceId);
        if (!File.Exists(Path.Combine(instance, ".hearth", "installed.json"))) throw new IOException("Install this pack with Play before launching it.");
        EnsureNotRunning(instance, PrismDataDirectory);
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
        ConfigureInstanceDirectory();
        return Process.Start(BuildStartInfo(executable, instanceId)) ?? throw new IOException("Windows could not start Prism.");
    }
    public void ConfigureInstanceDirectory()
    {
        Directory.CreateDirectory(PrismDataDirectory);
        var config = Path.Combine(PrismDataDirectory, "prismlauncher.cfg");
        var original = File.Exists(config) ? File.ReadAllText(config) : "[General]\r\nAutomaticJavaDownload=true\r\nAutomaticJavaSwitch=true\r\n";
        var lines = original.Replace("\r\n", "\n").Split('\n').ToList();
        var general = lines.FindIndex(line => line.Trim() == "[General]");
        if (general < 0) { lines.Insert(0, "[General]"); general = 0; }
        var end = lines.FindIndex(general + 1, line => line.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;
        var value = InstanceDirectory.Replace('\\', '/');
        // Qt INI treats leading @ and comma specially; quoted values preserve ordinary Windows paths.
        value = "InstanceDir=\"" + value.Replace("\"", "\\\"") + "\"";
        var found = false;
        for (var i = end - 1; i > general; i--)
            if (lines[i].StartsWith("InstanceDir=", StringComparison.Ordinal))
            {
                if (!found) { lines[i] = value; found = true; }
                else lines.RemoveAt(i);
            }
        if (!found) lines.Insert(general + 1, value);
        var updated = string.Join("\r\n", lines);
        if (updated == original) return;
        EnsureNotRunning(Path.Combine(InstanceDirectory, ".hearth-location-check"), PrismDataDirectory);
        File.WriteAllText(config + ".hearth.tmp", updated);
        File.Move(config + ".hearth.tmp", config, true);
    }
    public static void EnsureNotRunning(string instanceDirectory, string? prismDataDirectory = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return;
        var hasCandidates = false;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                ct.ThrowIfCancellationRequested();
                try { hasCandidates |= process.ProcessName.Equals("prismlauncher", StringComparison.OrdinalIgnoreCase) || process.ProcessName.Equals("java", StringComparison.OrdinalIgnoreCase) || process.ProcessName.Equals("javaw", StringComparison.OrdinalIgnoreCase); }
                catch { /* A process can exit between enumeration and inspection. */ }
            }
        }
        if (!hasCandidates) return;
        foreach (var (name, command) in ProcessCommandLines.ReadAsync(ct).GetAwaiter().GetResult())
            if (IsInstanceProcess(name, command, instanceDirectory, prismDataDirectory)) throw new IOException("Close Minecraft and the Hearth Prism window before updating packs or changing their folder. Then try again.");
    }
    public static bool IsInstanceProcess(string processName, string? commandLine, string instanceDirectory, string? prismDataDirectory = null)
    {
        if (commandLine is null) return false;
        var instance = Path.GetFullPath(instanceDirectory);
        var prismRoot = prismDataDirectory ?? Directory.GetParent(Directory.GetParent(instance)!.FullName)!.FullName;
        // Qt and Java can spell Windows paths with either separator. Boundaries
        // also prevent C:\Hearth from matching the unrelated C:\HearthTest profile.
        var arguments = Regex.Matches(commandLine.Replace('/', '\\'), "(?:[^\\s\"]+|\"[^\"]*\")+")
            .Select(match => match.Value.Replace("\"", "")).ToArray();
        bool References(string directory)
        {
            var path = directory.Replace('/', '\\').TrimEnd('\\');
            foreach (var argument in arguments)
            {
                for (var offset = 0; offset < argument.Length;)
                {
                    var index = argument.IndexOf(path, offset, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) break;
                    var end = index + path.Length;
                    var starts = index == 0 || argument[index - 1] is '=' or ';';
                    var ends = end == argument.Length || argument[end] is '\\' or ';';
                    if (starts && ends) return true;
                    offset = end;
                }
            }
            return false;
        }
        return References(instance) || (processName.Equals("prismlauncher.exe", StringComparison.OrdinalIgnoreCase) && References(prismRoot));
    }
}
