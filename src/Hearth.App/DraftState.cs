using Hearth.Core;

namespace Hearth.App;

public sealed class DraftState
{
    public string? BaseReleaseId { get; set; }
    public PackManifest? Baseline { get; set; }
    public CreatePackRequest? Metadata { get; set; }
    public List<DraftItem> Files { get; set; } = [];
    public string Notes { get; set; } = "";
    public bool OptionalExpanded { get; set; }
    public string SelectedGroup { get; set; } = "voxy";
    public bool HasChanges => BaseReleaseId == null || Baseline == null
        || Notes != Baseline.Notes || Metadata != new CreatePackRequest(Baseline.Name, Baseline.MinecraftVersion, Baseline.Loader, Baseline.LoaderVersion)
        || !Files.Select(x => x.File).OrderBy(Key).SequenceEqual(Baseline.Files.OrderBy(Key));
    private static string Key(PackFile file) => file.Group + "\n" + file.Path.ToLowerInvariant();
    public static DraftState From(PackSummary pack, PackManifest? manifest) => new()
    {
        BaseReleaseId = manifest?.ReleaseId, Baseline = manifest,
        Metadata = new(pack.Name, pack.MinecraftVersion, pack.Loader, pack.LoaderVersion),
        Files = manifest?.Files.Select(x => new DraftItem(x, null)).ToList() ?? [], Notes = manifest?.Notes ?? ""
    };

    public DraftState KeepChangesOn(PackManifest latest)
    {
        var before = (Baseline?.Files ?? []).ToDictionary(Key);
        var local = Files.ToDictionary(x => Key(x.File));
        var merged = latest.Files.ToDictionary(Key, x => new DraftItem(x, null));
        foreach (var key in before.Keys.Union(local.Keys))
        {
            before.TryGetValue(key, out var original); local.TryGetValue(key, out var edited);
            if (original == edited?.File) continue;
            if (edited == null) merged.Remove(key); else merged[key] = edited;
        }
        var originalMetadata = Baseline == null ? null : new CreatePackRequest(Baseline.Name, Baseline.MinecraftVersion, Baseline.Loader, Baseline.LoaderVersion);
        var localMetadata = Metadata ?? new(latest.Name, latest.MinecraftVersion, latest.Loader, latest.LoaderVersion);
        return new()
        {
            BaseReleaseId = latest.ReleaseId, Baseline = latest, Files = merged.Values.ToList(),
            Notes = Notes == (Baseline?.Notes ?? "") ? latest.Notes : Notes,
            Metadata = new(
                localMetadata.Name == originalMetadata?.Name ? latest.Name : localMetadata.Name,
                localMetadata.MinecraftVersion == originalMetadata?.MinecraftVersion ? latest.MinecraftVersion : localMetadata.MinecraftVersion,
                localMetadata.Loader == originalMetadata?.Loader ? latest.Loader : localMetadata.Loader,
                localMetadata.LoaderVersion == originalMetadata?.LoaderVersion ? latest.LoaderVersion : localMetadata.LoaderVersion),
            OptionalExpanded = OptionalExpanded, SelectedGroup = SelectedGroup
        };
    }

    public List<string> ConflictsWith(PackManifest latest)
    {
        var before = (Baseline?.Files ?? []).ToDictionary(Key); var local = Files.ToDictionary(x => Key(x.File)); var remote = latest.Files.ToDictionary(Key);
        var conflicts = new List<string>();
        foreach (var key in before.Keys.Union(local.Keys))
        {
            before.TryGetValue(key, out var original); local.TryGetValue(key, out var edited); remote.TryGetValue(key, out var current);
            if (original != edited?.File && original != current && edited?.File != current)
                conflicts.Add((edited?.File ?? original!).Path);
        }
        if (Notes != (Baseline?.Notes ?? "") && latest.Notes != (Baseline?.Notes ?? "") && Notes != latest.Notes) conflicts.Add("Release notes");
        if (Metadata is {} data && Baseline is {} baseline)
        {
            if (data.Name != baseline.Name && latest.Name != baseline.Name && data.Name != latest.Name) conflicts.Add("Pack name");
            if (data.MinecraftVersion != baseline.MinecraftVersion && latest.MinecraftVersion != baseline.MinecraftVersion && data.MinecraftVersion != latest.MinecraftVersion) conflicts.Add("Minecraft version");
            if (data.Loader != baseline.Loader && latest.Loader != baseline.Loader && data.Loader != latest.Loader) conflicts.Add("Mod loader");
            if (data.LoaderVersion != baseline.LoaderVersion && latest.LoaderVersion != baseline.LoaderVersion && data.LoaderVersion != latest.LoaderVersion) conflicts.Add("Loader version");
        }
        return conflicts;
    }
}
public sealed record DraftItem(PackFile File, string? LocalPath)
{
    public string Label => $"{File.Path[5..]}   ·   {File.Group}   ·   {File.Size / 1024d:N1} KB";
}
