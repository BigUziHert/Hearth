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
public record HostConnection(string Id, string Name, string Url, string OwnerToken);
public record JoinedPack(string HostUrl, string PackId, string Name, string Token, string MemberId, TerrainMode Terrain = TerrainMode.Off);
public record Invite(string HostUrl, string PackId, string Code)
{
    public override string ToString() => $"hearth://join?host={Uri.EscapeDataString(HostUrl)}&pack={Uri.EscapeDataString(PackId)}&code={Uri.EscapeDataString(Code)}";
    public static Invite Parse(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "hearth" || uri.Host != "join"
            || (uri.AbsolutePath.Length > 0 && uri.AbsolutePath != "/") || uri.Fragment.Length > 0)
            throw new ArgumentException("Paste the complete Hearth invite starting with hearth://join.");
        const string incomplete = "This invite is incomplete or invalid. Ask the owner to copy a fresh invite from the pack's Members page.";
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || !query.TryAdd(pair[0], Uri.UnescapeDataString(pair[1]))) throw new ArgumentException(incomplete);
        }
        if (!query.TryGetValue("host", out var address) || !Uri.TryCreate(address, UriKind.Absolute, out var host)
            || !query.TryGetValue("pack", out var pack) || string.IsNullOrWhiteSpace(pack)
            || !query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) throw new ArgumentException(incomplete);
        if (host.Scheme != "https" && !(host.Scheme == "http" && host.IsLoopback)) throw new ArgumentException("Remote hosts must use HTTPS. HTTP is allowed only on this PC (localhost).");
        if (!string.IsNullOrEmpty(host.UserInfo) || host.Query.Length != 0 || host.Fragment.Length != 0) throw new ArgumentException("Invalid host address.");
        return new(host.ToString().TrimEnd('/'), pack, code);
    }
}
