using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hearth.Core;

public static partial class ModFiles
{
    public const long MaxJarSize = 512L * 1024 * 1024;
    [GeneratedRegex(@"^[a-fA-F0-9]{64}$")] private static partial Regex HashPattern();
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])($|\.)", RegexOptions.IgnoreCase)] private static partial Regex ReservedPattern();
    public static void Validate(PackFile file)
    {
        if (!HashPattern().IsMatch(file.Sha256) || file.Size is <= 0 or > MaxJarSize) throw new InvalidDataException("The release contains an invalid file hash or size.");
        if (file.Group is not ("base" or "voxy" or "distanthorizons")) throw new InvalidDataException("Unknown optional mod group.");
        if (!file.Path.StartsWith("mods/", StringComparison.Ordinal) || file.Path.Length > 180) throw new InvalidDataException("Only files inside mods/ can be managed.");
        var name = file.Path[5..];
        if (name.Length == 0 || name.StartsWith('.') || name.Contains('/') || name.Contains('\\') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(':') || name.EndsWith(' ') || name.EndsWith('.') || !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || ReservedPattern().IsMatch(name))
            throw new InvalidDataException("The release contains an unsafe mod filename.");
    }
    public static async Task<PackFile> InspectAsync(string path, string group = "base", CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaxJarSize) throw new InvalidDataException("Choose a non-empty JAR smaller than 512 MB.");
        using (var archive = ZipFile.OpenRead(path))
        {
            if (archive.Entries.Count == 0) throw new InvalidDataException("This JAR is empty.");
            // Parsing the ZIP directory rejects truncated archives without executing mod code.
            if (!archive.Entries.Any(e => !e.FullName.EndsWith('/'))) throw new InvalidDataException("This JAR contains no files.");
        }
        var result = new PackFile("mods/" + info.Name, await HashAsync(path, ct), info.Length, group);
        Validate(result);
        return result;
    }
    public static async Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
    }
    /// <summary>Reads root mod identities without loading or executing any JAR code.</summary>
    public static IReadOnlyList<string> ReadModIds(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "fabric.mod.json", "quilt.mod.json" })
        {
            var entry = archive.GetEntry(name);
            if (entry is null) continue;
            if (entry.Length > 1024 * 1024) throw new InvalidDataException("A mod descriptor is too large.");
            using var stream = entry.Open();
            using var json = JsonDocument.Parse(stream);
            var root = name == "quilt.mod.json" && json.RootElement.TryGetProperty("quilt_loader", out var quilt) ? quilt : json.RootElement;
            if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString())) ids.Add(id.GetString()!);
        }
        foreach (var name in new[] { "META-INF/mods.toml", "META-INF/neoforge.mods.toml" })
        {
            var entry = archive.GetEntry(name);
            if (entry is null) continue;
            if (entry.Length > 1024 * 1024) throw new InvalidDataException("A mod descriptor is too large.");
            using var reader = new StreamReader(entry.Open());
            bool inMod = false;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('[')) inMod = trimmed.StartsWith("[[mods]]", StringComparison.Ordinal);
                if (!inMod) continue;
                var match = TomlModId().Match(trimmed);
                if (match.Success) ids.Add(match.Groups[1].Value);
            }
        }
        return ids.ToList();
    }
    [GeneratedRegex("^modId\\s*=\\s*[\"']([^\"']+)[\"']")] private static partial Regex TomlModId();
    public static async Task<bool> MatchesAsync(string path, PackFile file, CancellationToken ct = default) => File.Exists(path) && new FileInfo(path).Length == file.Size && string.Equals(await HashAsync(path, ct), file.Sha256, StringComparison.OrdinalIgnoreCase);
    public static IReadOnlyList<PackFile> Select(PackManifest manifest, TerrainMode mode)
    {
        if (manifest.Files.Count > 2000) throw new InvalidDataException("The pack contains too many files.");
        foreach (var file in manifest.Files) Validate(file);
        var group = mode switch { TerrainMode.Voxy => "voxy", TerrainMode.DistantHorizons => "distanthorizons", _ => "base" };
        if (mode != TerrainMode.Off && !manifest.Files.Any(f => f.Group == group)) throw new InvalidOperationException("The owner has not offered this terrain option for this release. Choose Off or another option.");
        var selected = new Dictionary<string, PackFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files.Where(f => f.Group == "base" || f.Group == group))
        {
            if (selected.TryGetValue(file.Path, out var prior) && (!prior.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase) || prior.Size != file.Size)) throw new InvalidDataException("Two selected mod groups contain conflicting versions of " + file.Path + ".");
            selected[file.Path] = file;
        }
        return selected.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
