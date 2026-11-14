using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hearth.Core;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
}
public enum TerrainMode { Off, Voxy, DistantHorizons }
public record PackFile(string Path, string Sha256, long Size, string Group = "base");
public record PackManifest(string PackId, string ReleaseId, string Name, string MinecraftVersion, string Loader, string LoaderVersion, List<PackFile> Files, string Notes = "", DateTimeOffset PublishedAt = default);
public record PackSummary(string Id, string Name, string MinecraftVersion, string Loader, string LoaderVersion, string? ReleaseId, string InviteCode);
public record CreatePackRequest(string Name, string MinecraftVersion, string Loader, string LoaderVersion);
public record JoinRequest(string InviteCode, string DisplayName);
public record JoinResponse(string PackId, string Name, string MemberId, string Token, string Status);
public record MemberInfo(string Id, string DisplayName, string Status, string Role, string Fingerprint, DateTimeOffset RequestedAt);
public record MemberUpdate(string Status, string Role = "player");
public record PublishRequest(string Name, string MinecraftVersion, string Loader, string LoaderVersion, List<PackFile> Files, string Notes, string? ExpectedReleaseId);
public record HealthResponse(string Name, string HostId, string Version, DateTimeOffset Time);
public record AccessResponse(string Status, string Role, string Name);
public record HostConnection(string Id, string Name, string Url, string OwnerToken, string? CertificatePin = null, string? HostId = null);
public record JoinedPack(string HostUrl, string PackId, string Name, string Token, string MemberId, TerrainMode Terrain = TerrainMode.Off,
    string? CertificatePin = null, string? InstanceHostUrl = null, string? HostId = null)
{
    [JsonIgnore] public string InstallationUrl => InstanceHostUrl ?? HostUrl;
}
public record Invite(string HostUrl, string PackId, string Code, string? CertificatePin = null, string? HostId = null)
{
    public override string ToString() => $"hearth://join?host={Uri.EscapeDataString(HostUrl)}&pack={Uri.EscapeDataString(PackId)}&code={Uri.EscapeDataString(Code)}" + HostLink.IdentityQuery(CertificatePin, HostId);
    public static Invite Parse(string value)
    {
        var query = HostLink.ParseQuery(value, "join", ["host", "pack", "code", "pin", "id"]);
        var address = HostLink.Address(query);
        var pack = HostLink.Required(query, "pack");
        var code = HostLink.Required(query, "code");
        return new(address, pack, code, HostLink.Pin(query, address), HostLink.OptionalIdentity(query));
    }
}
