using System.Text.Json;

namespace Hearth.Core;

/// <summary>Selectable versions from the same official metadata service used by Prism.</summary>
public sealed class VersionCatalog : IDisposable
{
    private readonly HttpClient client;
    private readonly bool ownsClient;
    public VersionCatalog(HttpClient? client = null)
    {
        ownsClient = client is null;
        this.client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<List<string>> MinecraftAsync(CancellationToken ct = default)
    {
        var versions = await IndexAsync("net.minecraft", ct);
        return versions.Where(x => x.Type == "release").OrderByDescending(x => x.ReleaseTime).Select(x => x.Version).Distinct(StringComparer.Ordinal).ToList();
    }

    public async Task<List<string>> LoaderAsync(string loader, string minecraftVersion, CancellationToken ct = default)
    {
        loader = loader.Trim().ToLowerInvariant();
        if (loader is "vanilla" or "none") return [];
        if (string.IsNullOrWhiteSpace(minecraftVersion)) throw new ArgumentException("Choose a Minecraft version first.");
        var uid = loader switch
        {
            "fabric" => "net.fabricmc.fabric-loader",
            "quilt" => "org.quiltmc.quilt-loader",
            "forge" => "net.minecraftforge",
            "neoforge" => "net.neoforged",
            _ => throw new ArgumentException("Choose Fabric, Forge, NeoForge, Quilt, or Vanilla.")
        };
        if (loader is "fabric" or "quilt")
        {
            // Both Prism loader components depend on Fabric intermediary mappings.
            var intermediary = await IndexAsync("net.fabricmc.intermediary", ct);
            if (!intermediary.Any(x => RequiresMinecraft(x, minecraftVersion))) return [];
        }
        var versions = await IndexAsync(uid, ct);
        return versions.Where(x => (x.Type is null or "release") &&
                (loader is "fabric" or "quilt" || RequiresMinecraft(x, minecraftVersion)))
            .OrderByDescending(x => x.Recommended).ThenByDescending(x => x.ReleaseTime)
            .Select(x => x.Version).Distinct(StringComparer.Ordinal).ToList();
    }

    private static bool RequiresMinecraft(VersionEntry version, string minecraft) => version.Requires.Any(x => x.Uid == "net.minecraft" && x.EqualsVersion == minecraft);

    private async Task<List<VersionEntry>> IndexAsync(string uid, CancellationToken ct)
    {
        // UID comes exclusively from the fixed component names above, never a user URL.
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://meta.prismlauncher.org/v1/" + uid + "/index.json");
        request.Headers.UserAgent.ParseAdd("Hearth/1.0");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        const int limit = 10 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new IOException("Prism's version catalog is larger than expected.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (bytes.Length + read > limit) throw new IOException("Prism's version catalog is larger than expected.");
            bytes.Write(buffer, 0, read);
        }
        var index = JsonSerializer.Deserialize<VersionIndex>(bytes.ToArray(), JsonDefaults.Options);
        if (index is null || index.FormatVersion != 1 || index.Uid != uid || index.Versions is null) throw new IOException("Prism returned an unsupported version catalog.");
        return index.Versions.Where(x => !string.IsNullOrWhiteSpace(x.Version) && x.Version.Length <= 80).ToList();
    }

    private sealed record VersionIndex(int FormatVersion, string Uid, List<VersionEntry> Versions);
    private sealed record VersionEntry(string Version, string? Type, DateTimeOffset ReleaseTime, bool Recommended, List<VersionRequirement> Requires);
    private sealed record VersionRequirement(string Uid, [property: System.Text.Json.Serialization.JsonPropertyName("equals")] string? EqualsVersion);
    public void Dispose() { if (ownsClient) client.Dispose(); }
}
