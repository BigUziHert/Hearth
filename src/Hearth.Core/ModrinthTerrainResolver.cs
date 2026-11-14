using System.Security.Cryptography;
using System.Text.Json;

namespace Hearth.Core;

/// <summary>Resolves the selected terrain mod for the exact game and loader, including required dependencies.</summary>
public sealed class ModrinthTerrainResolver : IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _owns;
    public ModrinthTerrainResolver(HttpClient? client = null)
    {
        _owns = client is null;
        _client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
    }
    public async Task<List<string>> ResolveAsync(TerrainMode mode, string minecraftVersion, string loader, string downloadDirectory, IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        var project = mode switch { TerrainMode.Voxy => "voxy", TerrainMode.DistantHorizons => "distanthorizons", _ => throw new ArgumentException("Choose Voxy or Distant Horizons.") };
        loader = loader.Trim().ToLowerInvariant();
        if (loader is not ("fabric" or "forge" or "neoforge" or "quilt")) throw new InvalidOperationException("Terrain mods require a compatible mod loader.");
        if (minecraftVersion.Length is < 1 or > 64) throw new ArgumentException("Select the pack's Minecraft version first.");
        var versions = new Dictionary<string, JsonElement>();
        var pending = new Queue<JsonElement>();
        progress?.Report(new("Finding compatible " + (mode == TerrainMode.Voxy ? "Voxy" : "Distant Horizons") + "…"));
        pending.Enqueue(await LatestAsync(project, minecraftVersion, loader, ct));
        while (pending.TryDequeue(out var version))
        {
            ct.ThrowIfCancellationRequested();
            if (versions.Count >= 100) throw new InvalidDataException("The dependency graph is unexpectedly large.");
            ValidateCompatibility(version, minecraftVersion, loader);
            var projectId = version.GetProperty("project_id").GetString()!;
            var versionId = version.GetProperty("id").GetString()!;
            if (versions.TryGetValue(projectId, out var previous))
            {
                if (previous.GetProperty("id").GetString() != versionId) throw new InvalidDataException("The terrain mod requires conflicting versions of one dependency. Select a compatible set manually.");
                continue;
            }
            versions.Add(projectId, version);
            foreach (var dependency in version.GetProperty("dependencies").EnumerateArray().Where(d => d.GetProperty("dependency_type").GetString() == "required"))
            {
                var depVersion = dependency.GetProperty("version_id").GetString();
                var depProject = dependency.GetProperty("project_id").GetString();
                if (depVersion is not null) pending.Enqueue(await JsonAsync("version/" + Uri.EscapeDataString(depVersion), ct));
                else if (depProject is not null) pending.Enqueue(await LatestAsync(depProject, minecraftVersion, loader, ct));
                else throw new InvalidDataException("A required dependency is not downloadable from Modrinth. Add the owner's verified dependency JAR manually.");
            }
        }
        foreach (var version in versions.Values)
        foreach (var dependency in version.GetProperty("dependencies").EnumerateArray().Where(d => d.GetProperty("dependency_type").GetString() == "incompatible"))
        {
            var conflictProject = dependency.GetProperty("project_id").GetString();
            var conflictVersion = dependency.GetProperty("version_id").GetString();
            if (conflictProject is not null && versions.TryGetValue(conflictProject, out var conflict) && (conflictVersion is null || conflict.GetProperty("id").GetString() == conflictVersion)) throw new InvalidDataException("Modrinth lists a conflict between the resolved terrain dependencies. Select a compatible set manually.");
        }
        Directory.CreateDirectory(downloadDirectory);
        var files = new List<string>();
        var fileHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var version in versions.Values)
        {
            var candidates = version.GetProperty("files").EnumerateArray().Where(f => (f.GetProperty("filename").GetString() ?? "").EndsWith(".jar", StringComparison.OrdinalIgnoreCase)).ToList();
            var selected = candidates.FirstOrDefault(f => f.TryGetProperty("primary", out var primary) && primary.GetBoolean());
            if (selected.ValueKind == JsonValueKind.Undefined && candidates.Count == 1) selected = candidates[0];
            if (selected.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("A terrain dependency has no unambiguous primary JAR.");
            var filename = selected.GetProperty("filename").GetString()!;
            var size = selected.GetProperty("size").GetInt64();
            ModFiles.Validate(new("mods/" + filename, new string('0', 64), size));
            var url = new Uri(selected.GetProperty("url").GetString()!);
            if (url.Scheme != "https" || url.Host != "cdn.modrinth.com" || url.UserInfo.Length != 0) throw new InvalidDataException("A mod download did not point to the official Modrinth CDN.");
            var hashes = selected.GetProperty("hashes");
            var algorithm = hashes.TryGetProperty("sha512", out var sha512) ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA1;
            var expectedHash = algorithm == HashAlgorithmName.SHA512 ? sha512.GetString()! : hashes.GetProperty("sha1").GetString()!;
            if (expectedHash.Length != (algorithm == HashAlgorithmName.SHA512 ? 128 : 40) || !expectedHash.All(Uri.IsHexDigit)) throw new InvalidDataException("The mod has no valid verification hash.");
            if (fileHashes.TryGetValue(filename, out var duplicateHash) && duplicateHash != expectedHash) throw new InvalidDataException("Two dependencies have the same filename but different content.");
            fileHashes[filename] = expectedHash;
            // Keep each resolved version in a dedicated directory so another draft cannot overwrite its source JAR.
            var versionDirectory = Path.Combine(Path.GetFullPath(downloadDirectory), version.GetProperty("id").GetString()!);
            if (!versionDirectory.StartsWith(Path.GetFullPath(downloadDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid Modrinth version identifier.");
            Directory.CreateDirectory(versionDirectory);
            var path = Path.Combine(versionDirectory, filename);
            if (!await VerifiedAsync(path, size, expectedHash, algorithm, ct))
            {
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    using var request = Request(url);
                    using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength is long length && length != size) throw new IOException("The mod download size did not match Modrinth's metadata.");
                    await using (var source = await response.Content.ReadAsStreamAsync(ct))
                    await using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        var buffer = new byte[65536]; long total = 0; int count;
                        while ((count = await source.ReadAsync(buffer, ct)) != 0)
                        {
                            total += count;
                            if (total > size) throw new IOException("The mod download exceeded its expected size.");
                            await target.WriteAsync(buffer.AsMemory(0, count), ct);
                            progress?.Report(new("Downloading " + filename, total, size));
                        }
                    }
                    if (!await VerifiedAsync(temp, size, expectedHash, algorithm, ct)) throw new IOException("The mod failed verification. Retry the download.");
                    File.Move(temp, path, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            await ModFiles.InspectAsync(path, mode == TerrainMode.Voxy ? "voxy" : "distanthorizons", ct);
            files.Add(path);
        }
        progress?.Report(new("Terrain option and required dependencies are ready for review.", 1, 1));
        return files;
    }
    private static void ValidateCompatibility(JsonElement version, string minecraftVersion, string loader)
    {
        if (!version.GetProperty("game_versions").EnumerateArray().Any(v => v.GetString() == minecraftVersion) || !version.GetProperty("loaders").EnumerateArray().Any(v => v.GetString() == loader)) throw new InvalidDataException("A required terrain dependency does not support this Minecraft version and loader. Select a compatible set manually.");
        var id = version.GetProperty("id").GetString();
        if (string.IsNullOrEmpty(id) || id.Length > 64 || !id.All(char.IsAsciiLetterOrDigit)) throw new InvalidDataException("Invalid mod version identifier.");
    }
    private async Task<JsonElement> LatestAsync(string project, string minecraftVersion, string loader, CancellationToken ct)
    {
        var query = "project/" + Uri.EscapeDataString(project) + "/version?loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader })) + "&game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { minecraftVersion }));
        var versions = await JsonAsync(query, ct);
        var selected = versions.EnumerateArray().Where(v => v.GetProperty("game_versions").EnumerateArray().Any(g => g.GetString() == minecraftVersion) && v.GetProperty("loaders").EnumerateArray().Any(l => l.GetString() == loader)).OrderByDescending(v => v.GetProperty("date_published").GetDateTimeOffset()).FirstOrDefault();
        if (selected.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("Modrinth has no compatible " + project + " release for Minecraft " + minecraftVersion + " / " + loader + ". Choose another terrain option or add a compatible JAR manually.");
        return selected.Clone();
    }
    private async Task<JsonElement> JsonAsync(string route, CancellationToken ct)
    {
        using var request = Request(new Uri("https://api.modrinth.com/v2/" + route));
        using var response = await _client.SendAsync(request, ct);
        if ((int)response.StatusCode == 429) throw new IOException("Modrinth's request limit was reached. Wait a moment before trying again.");
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Modrinth returned too much metadata.");
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }
    private static HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Hearth/1.0 (Minecraft-modpack-desktop-launcher)");
        return request;
    }
    private static async Task<bool> VerifiedAsync(string path, long size, string hash, HashAlgorithmName algorithm, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size) return false;
        await using var input = File.OpenRead(path);
        var actual = algorithm == HashAlgorithmName.SHA512 ? await SHA512.HashDataAsync(input, ct) : await SHA1.HashDataAsync(input, ct);
        return Convert.ToHexStringLower(actual).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }
    public void Dispose() { if (_owns) _client.Dispose(); }
}
