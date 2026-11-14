using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hearth.Core;

public record SyncProgress(string Message, long CompletedBytes = 0, long TotalBytes = 0)
{
    public double Percent => TotalBytes > 0 ? Math.Clamp(100.0 * CompletedBytes / TotalBytes, 0, 100) : 0;
}
public record SyncResult(string InstanceId, string InstanceDirectory, int DownloadedFiles, int RemovedFiles, string ReleaseId);
public record InstalledPack(string HostUrl, string PackId, string ReleaseId, TerrainMode Terrain, List<PackFile> Files, PackManifest Manifest);

/// <summary>Stages verified downloads first, then applies a journaled transaction to owned files only.</summary>
public sealed class PackSynchronizer
{
    public string ProfileRoot { get; }
    public PackSynchronizer(string profileRoot) => ProfileRoot = Path.GetFullPath(profileRoot);
    public string GetInstanceId(string hostUrl, string packId) => "hearth-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(HearthApi.NormalizeHost(hostUrl) + "\n" + packId)))[..24];
    public string GetInstanceDirectory(string hostUrl, string packId) => Path.Combine(ProfileRoot, "Prism", "instances", GetInstanceId(hostUrl, packId));
    public InstalledPack? GetInstalled(string hostUrl, string packId)
    {
        var path = Path.Combine(GetInstanceDirectory(hostUrl, packId), ".hearth", "installed.json");
        return File.Exists(path) ? Read<InstalledPack>(path) : null;
    }
    public Task RecoverAsync(string hostUrl, string packId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var instance = GetInstanceDirectory(hostUrl, packId);
        RejectLinks(Path.Combine(instance, ".hearth"));
        PrismService.EnsureNotRunning(instance);
        Directory.CreateDirectory(Path.Combine(instance, ".hearth"));
        using var syncLock = AcquireLock(instance);
        Recover(instance);
        return Task.CompletedTask;
    }
    public async Task<SyncResult> SyncAsync(HearthApi api, JoinedPack pack, IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        if (!api.HostUrl.Equals(HearthApi.NormalizeHost(pack.HostUrl), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The connection does not match this pack's host.");
        var instance = GetInstanceDirectory(pack.HostUrl, pack.PackId);
        RejectLinks(Path.Combine(instance, ".hearth"));
        Directory.CreateDirectory(Path.Combine(instance, ".hearth"));
        RejectLinks(instance);
        using var syncLock = AcquireLock(instance);
        PrismService.EnsureNotRunning(instance);
        Recover(instance);
        progress?.Report(new("Checking your access and the latest complete release…"));
        var manifest = await api.ManifestAsync(pack.PackId, ct);
        if (manifest.PackId != pack.PackId || string.IsNullOrWhiteSpace(manifest.ReleaseId)) throw new InvalidDataException("The host returned metadata for a different pack.");
        var selected = ModFiles.Select(manifest, pack.Terrain).ToList();
        var installed = GetInstalled(pack.HostUrl, pack.PackId);
        if (installed is not null)
        {
            if (installed.PackId != pack.PackId || installed.HostUrl != HearthApi.NormalizeHost(pack.HostUrl)) throw new InvalidDataException("Local installation metadata does not match this pack.");
            foreach (var file in installed.Files) ModFiles.Validate(file);
        }
        var previous = (installed?.Files ?? []).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var next = selected.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var replacements = new List<PackFile>();
        var downloads = 0;
        var cache = Path.Combine(ProfileRoot, "cache", "sha256");
        RejectLinks(cache);
        Directory.CreateDirectory(cache);
        RejectLinks(cache);
        foreach (var file in selected)
        {
            ct.ThrowIfCancellationRequested();
            var target = OwnedPath(instance, ".minecraft/" + file.Path);
            if (File.Exists(target) && !previous.ContainsKey(file.Path)) throw new IOException("A personal file already exists at " + file.Path + ". Move or rename it before retrying. Hearth will not overwrite it.");
            if (await ModFiles.MatchesAsync(target, file, ct)) continue;
            replacements.Add(file);
            var blob = Path.Combine(cache, file.Sha256.ToLowerInvariant());
            if (await ModFiles.MatchesAsync(blob, file, ct)) continue;
            var temporary = blob + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                await api.DownloadAsync(pack.PackId, file, temporary, progress, ct);
                if (!await ModFiles.MatchesAsync(temporary, file, ct)) throw new IOException("The SHA-256 verification failed for " + file.Path + ". Your previous installation is safe; retry Play.");
                // Treat every downloaded mod as a JAR, never as executable installer content.
                using (var jar = System.IO.Compression.ZipFile.OpenRead(temporary))
                    if (jar.Entries.Count == 0) throw new InvalidDataException("The downloaded mod is not a valid JAR.");
                File.Move(temporary, blob, true);
                downloads++;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var removals = previous.Values.Where(f => !next.ContainsKey(f.Path)).ToList();
        var modIdentities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in selected)
        {
            var source = replacements.Any(r => r.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)) ? Path.Combine(cache, file.Sha256.ToLowerInvariant()) : OwnedPath(instance, ".minecraft/" + file.Path);
            foreach (var id in ModFiles.ReadModIds(source))
            {
                if (modIdentities.TryGetValue(id, out var other)) throw new InvalidDataException("The selected release contains two versions of mod '" + id + "': " + other + " and " + file.Path + ". Ask the owner to resolve the duplicate or choose another terrain option. Your previous installation is safe.");
                modIdentities.Add(id, file.Path);
            }
        }
        var versionJson = CreatePrismMetadata(manifest);
        var newInstalled = new InstalledPack(HearthApi.NormalizeHost(pack.HostUrl), pack.PackId, manifest.ReleaseId, pack.Terrain, selected, manifest);
        ct.ThrowIfCancellationRequested();
        // Check again after network activity, in case the user opened Prism while downloads ran.
        PrismService.EnsureNotRunning(instance);
        var txn = Path.Combine(instance, ".hearth", "transaction");
        Directory.CreateDirectory(Path.Combine(txn, "backup"));
        Directory.CreateDirectory(Path.Combine(txn, "new"));
        var changed = replacements.Select(f => ".minecraft/" + f.Path).Concat(removals.Select(f => ".minecraft/" + f.Path)).Concat(["mmc-pack.json", ".hearth/installed.json"]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!File.Exists(Path.Combine(instance, "instance.cfg"))) changed.Add("instance.cfg");
        var journal = new SyncJournal("prepared", []);
        foreach (var relative in changed)
        {
            var source = OwnedPath(instance, relative);
            var entry = new JournalEntry(relative, File.Exists(source), journal.Entries.Count.ToString());
            if (entry.Existed) CopyDurable(source, Path.Combine(txn, "backup", entry.Backup));
            journal.Entries.Add(entry);
        }
        for (var i = 0; i < replacements.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            CopyDurable(Path.Combine(cache, replacements[i].Sha256.ToLowerInvariant()), Path.Combine(txn, "new", i.ToString()));
        }
        ct.ThrowIfCancellationRequested();
        AtomicJson(Path.Combine(txn, "journal.json"), journal);
        try
        {
            progress?.Report(new("Applying the verified release…"));
            for (var i = 0; i < replacements.Count; i++)
            {
                var file = replacements[i];
                var target = OwnedPath(instance, ".minecraft/" + file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(Path.Combine(txn, "new", i.ToString()), target, true);
            }
            foreach (var file in removals)
            {
                var target = OwnedPath(instance, ".minecraft/" + file.Path);
                if (File.Exists(target)) File.Delete(target);
            }
            AtomicBytes(Path.Combine(instance, "mmc-pack.json"), versionJson);
            var cfg = Path.Combine(instance, "instance.cfg");
            if (!File.Exists(cfg)) AtomicBytes(cfg, Encoding.UTF8.GetBytes("[General]\r\nInstanceType=OneSix\r\nname=" + SanitizeIni(manifest.Name) + "\r\niconKey=default\r\n"));
            AtomicJson(Path.Combine(instance, ".hearth", "installed.json"), newInstalled);
            AtomicJson(Path.Combine(txn, "journal.json"), journal with { Phase = "committed" });
        }
        catch
        {
            Recover(instance);
            throw;
        }
        // Failure to remove backup files after a durable commit does not undo the successful release.
        try { Directory.Delete(txn, true); } catch (IOException) { }
        progress?.Report(new("Ready to play", 1, 1));
        return new(GetInstanceId(pack.HostUrl, pack.PackId), instance, downloads, removals.Count, manifest.ReleaseId);
    }
    private static string SanitizeIni(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('=', '-').Replace('[', '(').Replace(']', ')');
    public static byte[] CreatePrismMetadata(PackManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.MinecraftVersion) || manifest.MinecraftVersion.Length > 64) throw new InvalidDataException("The release has no Minecraft version.");
        var components = new List<object> { new { uid = "net.minecraft", version = manifest.MinecraftVersion, important = true, cachedName = "Minecraft" } };
        var uid = manifest.Loader.ToLowerInvariant() switch
        {
            "vanilla" or "none" => null,
            "fabric" => "net.fabricmc.fabric-loader",
            "quilt" => "org.quiltmc.quilt-loader",
            "forge" => "net.minecraftforge",
            "neoforge" => "net.neoforged",
            _ => throw new InvalidDataException("This Minecraft mod loader is not supported.")
        };
        if (uid is not null)
        {
            if (string.IsNullOrWhiteSpace(manifest.LoaderVersion)) throw new InvalidDataException("The owner must select a mod loader version before publishing.");
            components.Add(new { uid, version = manifest.LoaderVersion });
        }
        return JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, components }, JsonDefaults.Options);
    }
    private static FileStream AcquireLock(string instance)
    {
        try { return new FileStream(Path.Combine(instance, ".hearth", "sync.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("Another Hearth window is updating this pack. Wait for it to finish.", ex); }
    }
    private static void Recover(string instance)
    {
        var txn = Path.Combine(instance, ".hearth", "transaction");
        var journalPath = Path.Combine(txn, "journal.json");
        if (!File.Exists(journalPath))
        {
            if (Directory.Exists(txn)) Directory.Delete(txn, true); // Only uncommitted backup preparation, no live files changed.
            return;
        }
        var journal = Read<SyncJournal>(journalPath);
        if (journal.Phase != "committed")
        {
            if (journal.Phase != "prepared") throw new InvalidDataException("The recovery journal is invalid. Your installation has not been changed.");
            foreach (var entry in journal.Entries)
            {
                if (!int.TryParse(entry.Backup, out var index) || index < 0 || index > 10000) throw new InvalidDataException("Invalid recovery file.");
                var destination = OwnedPath(instance, entry.Path);
                if (entry.Existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var restoring = Path.Combine(txn, "restore-" + entry.Backup);
                    CopyDurable(Path.Combine(txn, "backup", entry.Backup), restoring);
                    File.Move(restoring, destination, true);
                }
                else if (File.Exists(destination)) File.Delete(destination);
            }
        }
        Directory.Delete(txn, true);
    }
    private static string OwnedPath(string instance, string relative)
    {
        if (relative is not ("mmc-pack.json" or "instance.cfg" or ".hearth/installed.json"))
        {
            if (!relative.StartsWith(".minecraft/", StringComparison.Ordinal)) throw new InvalidDataException("Invalid managed path.");
            ModFiles.Validate(new(relative[11..], new string('0', 64), 1));
        }
        var path = Path.Combine(instance, relative.Replace('/', Path.DirectorySeparatorChar));
        RejectLinks(path);
        return path;
    }
    private static void RejectLinks(string path)
    {
        for (var item = Path.GetFullPath(path); !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new IOException("Hearth will not update files through symbolic links or junctions: " + item);
    }
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonDefaults.Options) ?? throw new InvalidDataException("Local metadata is unreadable.");
    private static void AtomicJson<T>(string path, T value) => AtomicBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options));
    private static void AtomicBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
    private static void CopyDurable(string source, string destination)
    {
        using var input = File.OpenRead(source);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
        input.CopyTo(output);
        output.Flush(true);
    }
    private record JournalEntry(string Path, bool Existed, string Backup);
    private record SyncJournal(string Phase, List<JournalEntry> Entries);
}
