using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Hearth.Core;

namespace Hearth.App;
public sealed class ProfileState
{
    public string DisplayName { get; set; } = Environment.UserName;
    public List<HostConnection> Hosts { get; set; } = [];
    public List<JoinedPack> Packs { get; set; } = [];
    public string InstanceDirectory { get; set; } = "";
    public string PreviousInstanceDirectory { get; set; } = "";
    public string PrismExecutable { get; set; } = "";
    public string SelectedPackHost { get; set; } = "";
    public string SelectedPackId { get; set; } = "";
    public bool CloseToTray { get; set; }
    public bool LocalHostingEnabled { get; set; }
    public int LocalHostingPort { get; set; } = 17842;
    public string LocalHostDataDirectory { get; set; } = "";
    public string LocalHostOwnerToken { get; set; } = "";
    public string LocalHostAdvertisedAddress { get; set; } = "";
    public List<RememberedInstance> RememberedInstances { get; set; } = [];
    public string LocalHostConnectionId { get; set; } = "";
    public bool LegacyHostingMigrated { get; set; }
}
public sealed record RememberedInstance(string HostId, string PackId, string CertificatePin, string InstallationUrl);
public sealed class ProfileStore
{
    private readonly object saveLock = new();
    public string Root { get; }
    public ProfileState State { get; private set; }
    public string? RecoveryNotice { get; private set; }
    public ProfileStore(string root)
    {
        Root = Path.GetFullPath(root); Directory.CreateDirectory(Root);
        var file = Path.Combine(Root, "profile.bin");
        var backup = file + ".bak";
        if (!File.Exists(file) && !File.Exists(backup)) { State = new(); return; }
        try { State = Read(file); return; }
        catch (Exception ex) when (Recoverable(ex)) { }
        ProfileState? recovered = null;
        try { recovered = Read(backup); }
        catch (Exception ex) when (Recoverable(ex)) { }
        if (recovered != null)
        {
            State = recovered;
            var damaged = Preserve(file);
            RecoveryNotice = "Hearth recovered your profile from its backup. Your previous profile was kept at " + (damaged ?? backup) + ". Recent changes may need to be repeated.";
            try { DurableWrite(file, File.ReadAllBytes(backup)); }
            catch (Exception ex) when (Recoverable(ex))
            {
                RecoveryNotice += " The recovered profile could not be saved yet. The valid backup is still at " + backup + ". Check the folder's permissions and free disk space.";
            }
            return;
        }
        var saved = new[] { Preserve(file), Preserve(backup) }.Where(p => p != null);
        State = new();
        RecoveryNotice = "Hearth couldn't read your profile or its backup and opened a new profile. Your Minecraft files and host data are untouched. The unreadable files were kept for recovery: " + string.Join(", ", saved) + ". Reconnect your hosts or restore a known-good profile backup.";
    }
    private static bool Recoverable(Exception ex) => ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidDataException;
    private static ProfileState Read(string path)
    {
        var state = JsonSerializer.Deserialize<ProfileState>(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser), JsonDefaults.Options)
            ?? throw new InvalidDataException("The profile is empty.");
        if (state.Hosts == null || state.Packs == null || state.DisplayName == null || state.Hosts.Any(x => x == null) || state.Packs.Any(x => x == null)) throw new InvalidDataException("The profile is incomplete.");
        state.RememberedInstances ??= [];
        return state;
    }
    private static string? Preserve(string path)
    {
        if (!File.Exists(path)) return null;
        var saved = path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        try { File.Move(path, saved); return saved; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return path; }
    }
    private static void DurableWrite(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes); stream.Flush(true);
    }
    public void Save()
    {
        lock (saveLock)
        {
            var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(State, JsonDefaults.Options), null, DataProtectionScope.CurrentUser);
            var file = Path.Combine(Root, "profile.bin"); var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                DurableWrite(temp, bytes);
                if (File.Exists(file)) File.Replace(temp, file, file + ".bak", true);
                else File.Move(temp, file);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
