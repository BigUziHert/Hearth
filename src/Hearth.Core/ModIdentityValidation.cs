namespace Hearth.Core;

/// <summary>Shared owner preflight and host validation for the mod sets players can select.</summary>
public static class ModIdentityValidation
{
    public static IReadOnlyList<PackFile> SelectOptional(IEnumerable<(PackFile File, IReadOnlyList<string> Ids)> required,
        IEnumerable<(PackFile File, IReadOnlyList<string> Ids)> optional)
    {
        var baseFiles = required.ToList();
        var provided = baseFiles.SelectMany(x => x.Ids).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hashes = baseFiles.Select(x => x.File.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return optional.Where(x => !x.Ids.Any(provided.Contains) && !hashes.Contains(x.File.Sha256)).Select(x => x.File).ToList();
    }
    public static void ValidateGroups(IEnumerable<(PackFile File, IReadOnlyList<string> Ids)> source)
    {
        var entries = source.ToList();
        foreach (var group in new[] { "base", "voxy", "distanthorizons" })
        {
            var ids = new Dictionary<string, PackFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var (file, modIds) in entries.Where(x => x.File.Group == "base" || x.File.Group == group))
            foreach (var id in modIds)
            {
                if (ids.TryGetValue(id, out var before) && !(before.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase) && before.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)))
                {
                    var advice = before.Group != file.Group && (before.Group == "base" || file.Group == "base")
                        ? "Keep the Required mods copy so players choosing Off still have it; remove the duplicate from the optional terrain group."
                        : "Keep one version in this group and remove the duplicate.";
                    throw new InvalidDataException($"Duplicate mod ID '{id}' in {before.Path} ({Name(before.Group)}) and {file.Path} ({Name(file.Group)}. {advice}");
                }
                ids[id] = file;
            }
        }
    }
    private static string Name(string group) => group switch { "base" => "Required mods", "voxy" => "Voxy", _ => "Distant Horizons" };
}
