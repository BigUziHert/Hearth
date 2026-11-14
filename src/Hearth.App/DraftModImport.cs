using System.IO;
using System.Windows;
using Hearth.Core;

namespace Hearth.App;

/// <summary>Copies user-selected JARs into draft storage before changing a draft.</summary>
public static class DraftModImport
{
    public static string[] ReadPaths(IDataObject data)
    {
        IEnumerable<string> paths = data.GetDataPresent(DataFormats.FileDrop)
            ? data.GetData(DataFormats.FileDrop) as string[] ?? []
            : data.GetDataPresent(DataFormats.UnicodeText)
                ? (data.GetData(DataFormats.UnicodeText) as string ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                : [];
        return paths.Select(p => p.Trim().Trim('"')).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool AreJarPaths(IEnumerable<string> paths)
    {
        var files = paths.ToArray();
        return files.Length is > 0 and <= 2000 && files.All(p => Path.IsPathFullyQualified(p)
            && p.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<List<DraftItem>> StageAsync(IEnumerable<string> paths, string profileRoot, string group, CancellationToken ct)
    {
        var sources = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!AreJarPaths(sources)) throw new InvalidDataException("Choose or copy .jar files from File Explorer. Folders and web links cannot be added as mods.");
        var staged = new List<DraftItem>();
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            var file = await ModFiles.InspectAsync(source, group, ct);
            var target = CachePath(profileRoot, file);
            if (!await ModFiles.MatchesAsync(target, file, ct))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temp = target + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                    await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        if (input.Length != file.Size) throw new IOException("A JAR changed while it was being copied. Add the file again.");
                        await input.CopyToAsync(output, ct);
                    }
                    if (!await ModFiles.MatchesAsync(temp, file, ct)) throw new IOException("A JAR changed while it was being copied. Add the file again.");
                    ct.ThrowIfCancellationRequested();
                    File.Move(temp, target, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            staged.Add(new(file, target));
        }
        // Ambiguous batches must never silently choose one of two different JARs with the same name.
        foreach (var entries in staged.GroupBy(x => x.File.Path, StringComparer.OrdinalIgnoreCase))
            if (entries.Select(x => x.File.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                throw new InvalidDataException("Two selected mods have the same filename but different contents: " + entries.Key[5..] + ". Add one version at a time.");
        return staged.DistinctBy(x => x.File.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<DraftItem> Apply(IEnumerable<DraftItem> existing, IEnumerable<DraftItem> staged, DraftItem? replacing = null)
    {
        var result = existing.Where(x => x != replacing).ToList();
        foreach (var item in staged)
        {
            result.RemoveAll(x => x.File.Group == item.File.Group && x.File.Path.Equals(item.File.Path, StringComparison.OrdinalIgnoreCase));
            result.Add(item);
        }
        if (result.Count > 2000) throw new InvalidDataException("A pack can contain at most 2,000 mod files.");
        return result;
    }

    public static async Task<string> GetSourceAsync(DraftItem item, string profileRoot, HearthApi api, string packId, CancellationToken ct)
    {
        if (item.LocalPath is not null)
        {
            if (!await ModFiles.MatchesAsync(item.LocalPath, item.File, ct)) throw new IOException("A draft JAR is missing or changed. Add it again before copying or updating it.");
            return item.LocalPath;
        }
        var target = CachePath(profileRoot, item.File);
        if (await ModFiles.MatchesAsync(target, item.File, ct)) return target;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await api.DownloadAsync(packId, item.File, temp, null, ct);
            if (!await ModFiles.MatchesAsync(temp, item.File, ct)) throw new IOException("The mod download failed verification. Try again.");
            ct.ThrowIfCancellationRequested();
            File.Move(temp, target, true);
            return target;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string CachePath(string profileRoot, PackFile file)
    {
        ModFiles.Validate(file);
        return Path.Combine(profileRoot, "Drafts", "Files", file.Sha256.ToLowerInvariant(), file.Path[5..]);
    }
}
