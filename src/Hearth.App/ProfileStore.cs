using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hearth.Core;

namespace Hearth.App;
public sealed class ProfileState
{
    public string DisplayName { get; set; } = Environment.UserName;
    public List<HostConnection> Hosts { get; set; } = [];
    public List<JoinedPack> Packs { get; set; } = [];
    public string UpdateRepository { get; set; } = "";
    // Empty in both existing and new profiles means the app's official source.
    // Explicit custom values are preserved across application upgrades.
    [JsonIgnore] public string EffectiveUpdateRepository => string.IsNullOrWhiteSpace(UpdateRepository) ? AppInfo.DefaultUpdateRepository : UpdateRepository.Trim();
    [JsonIgnore] public bool UsesOfficialUpdateRepository => EffectiveUpdateRepository.Equals(AppInfo.DefaultUpdateRepository, StringComparison.OrdinalIgnoreCase);
    public string PrismExecutable { get; set; } = "";
    public string SelectedPackHost { get; set; } = "";
    public string SelectedPackId { get; set; } = "";
}
public sealed class ProfileStore
{
    public string Root { get; }
    public ProfileState State { get; private set; }
    public ProfileStore(string root)
    {
        Root = Path.GetFullPath(root); Directory.CreateDirectory(Root);
        var file = Path.Combine(Root, "profile.bin");
        State = File.Exists(file) ? JsonSerializer.Deserialize<ProfileState>(ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser), JsonDefaults.Options) ?? new() : new();
    }
    public void Save()
    {
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(State, JsonDefaults.Options), null, DataProtectionScope.CurrentUser);
        var file = Path.Combine(Root, "profile.bin"); File.WriteAllBytes(file + ".tmp", bytes); File.Move(file + ".tmp", file, true);
    }
}
public sealed class DraftState
{
    public string? BaseReleaseId { get; set; }
    public List<DraftItem> Files { get; set; } = [];
    public string Notes { get; set; } = "";
}
public sealed record DraftItem(PackFile File, string? LocalPath)
{
    public string Label => $"{File.Path[5..]}   ·   {File.Group}   ·   {File.Size / 1024d:N1} KB";
}
