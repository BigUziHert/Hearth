using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hearth.Core;

public record UpdateRelease(string Version, string Notes, string PageUrl, string AssetName, string DownloadUrl, string Sha256, long Size, string Repository = "");
public sealed class ReleaseUnavailableException() : IOException("No public stable release is available from this update source yet.");

/// <summary>Explicit check, verified download, then installer handoff. Never auto-installs.</summary>
public sealed partial class GithubUpdater : IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _owns;
    private readonly Uri _apiBase;
    private readonly bool _testTransport;
    public GithubUpdater(HttpClient? client = null, Uri? apiBase = null)
    {
        _owns = client is null;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        _apiBase = apiBase ?? new Uri("https://api.github.com/");
        if (_apiBase.Scheme != "https" && !(_apiBase.Scheme == "http" && _apiBase.IsLoopback)) throw new ArgumentException("Release metadata must use HTTPS.");
        _testTransport = _apiBase.IsLoopback;
    }
    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")] private static partial Regex RepositoryPattern();
    [GeneratedRegex(@"^[a-fA-F0-9]{64}$")] private static partial Regex HashPattern();
    public static string NormalizeRepository(string value)
    {
        value = value.Trim();
        if (value.Length > 220 || !RepositoryPattern().IsMatch(value) || value.Split('/').Any(s => s is "." or "..")) throw new ArgumentException("Enter a public GitHub repository as owner/repository.");
        return value;
    }
    private static Version ComparableVersion(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
    public async Task<UpdateRelease?> CheckAsync(string ownerRepo, Version currentVersion, CancellationToken ct = default)
    {
        ownerRepo = NormalizeRepository(ownerRepo);
        var release = await GetLatestAsync(ownerRepo, ct);
        using (release)
        {
            var root = release.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var versionText = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
            if (!Version.TryParse(versionText, out var next) || next.Build < 0) throw new InvalidDataException("The GitHub release tag must be a version such as v1.0.1.");
            if (ComparableVersion(next) <= ComparableVersion(currentVersion)) return null;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() || root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) throw new InvalidDataException("Publish a stable GitHub release before updating.");
            var candidates = root.GetProperty("assets").EnumerateArray().Where(a =>
            {
                var name = a.GetProperty("name").GetString() ?? "";
                return name.StartsWith("Hearth-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.Contains("win-x64", StringComparison.OrdinalIgnoreCase);
            }).ToList();
            if (candidates.Count != 1) throw new InvalidDataException("The release must contain one Hearth-Setup-<version>-win-x64.exe installer.");
            var asset = candidates[0];
            var hash = await AssetHashAsync(root, asset, ownerRepo, ct);
            var name = asset.GetProperty("name").GetString()!;
            ValidateAssetName(name);
            if (!name.Equals($"Hearth-Setup-{versionText}-win-x64.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The installer version does not match its release tag. Wait for the publisher to correct this release.");
            var url = asset.GetProperty("browser_download_url").GetString()!;
            ValidateDownloadUrl(url, ownerRepo);
            var size = asset.GetProperty("size").GetInt64();
            if (size is < 1 or > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("The release installer has an invalid size.");
            return new(next.ToString(), root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "", root.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "", name, url, hash, size, ownerRepo);
        }
    }
    internal async Task<JsonDocument> GetLatestAsync(string ownerRepo, CancellationToken ct)
    {
        ownerRepo = NormalizeRepository(ownerRepo);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var request = Request(new Uri(_apiBase, "repos/" + ownerRepo + "/releases/latest"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new ReleaseUnavailableException();
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden || (int)response.StatusCode == 429) throw new IOException("GitHub's request limit was reached. Try again later.");
        response.EnsureSuccessStatusCode();
        var bytes = await ReadLimitedAsync(response.Content, 8 * 1024 * 1024, timeout.Token);
        return JsonDocument.Parse(bytes);
    }
    internal async Task<string> AssetHashAsync(JsonElement release, JsonElement asset, string ownerRepo, CancellationToken ct)
    {
        if (asset.TryGetProperty("digest", out var digest))
        {
            var value = digest.GetString();
            if (value?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true && HashPattern().IsMatch(value[7..])) return value[7..].ToLowerInvariant();
        }
        var checksum = release.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString()?.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) == true);
        if (checksum.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("The release needs a GitHub SHA-256 digest or SHA256SUMS.txt. The update was not downloaded.");
        var url = checksum.GetProperty("browser_download_url").GetString()!;
        ValidateDownloadUrl(url, ownerRepo);
        using var request = Request(new Uri(url));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var text = System.Text.Encoding.UTF8.GetString(await ReadLimitedAsync(response.Content, 1024 * 1024, ct));
        var expectedName = asset.GetProperty("name").GetString();
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart(' ', '*') == expectedName && HashPattern().IsMatch(parts[0])) return parts[0].ToLowerInvariant();
        }
        throw new InvalidDataException("The checksum file does not identify this installer.");
    }
    private static HttpRequestMessage Request(Uri url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Hearth/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return request;
    }
    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("The update source returned more metadata than expected.");
        await using var input = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("The update source returned more metadata than expected.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    internal void ValidateDownloadUrl(string url, string ownerRepo)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0) throw new InvalidDataException("Invalid release download URL.");
        if (_testTransport && uri.IsLoopback && uri.Scheme == "http") return;
        if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/" + ownerRepo + "/releases/download/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Release assets must come from the configured GitHub repository over HTTPS.");
    }
    private static void ValidateAssetName(string name)
    {
        if (name.Length is < 1 or > 180 || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\') || name.Contains(':')) throw new InvalidDataException("The release has an unsafe asset name.");
    }
    public async Task<string> DownloadAsync(UpdateRelease release, string folder, IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        ValidateAssetName(release.AssetName);
        if (!HashPattern().IsMatch(release.Sha256) || release.Size is < 1 or > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Invalid update verification metadata.");
        var uri = new Uri(release.DownloadUrl);
        if (uri.Scheme != "https" && !(_testTransport && uri.IsLoopback && uri.Scheme == "http")) throw new InvalidDataException("Update downloads require HTTPS.");
        if (!string.IsNullOrEmpty(release.Repository)) ValidateDownloadUrl(release.DownloadUrl, NormalizeRepository(release.Repository));
        Directory.CreateDirectory(folder);
        var target = Path.Combine(Path.GetFullPath(folder), release.AssetName);
        if (File.Exists(target) && new FileInfo(target).Length == release.Size && (await ModFiles.HashAsync(target, ct)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(target + ".sha256", release.Sha256);
            progress?.Report(new("The verified update is already downloaded.", release.Size, release.Size));
            return target;
        }
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var request = Request(uri);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != release.Size) throw new IOException("The update size does not match its release metadata.");
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) != 0)
                {
                    total += count;
                    if (total > release.Size) throw new IOException("The update is larger than expected.");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    progress?.Report(new("Downloading " + release.AssetName, total, release.Size));
                }
                await output.FlushAsync(ct);
                output.Flush(true);
                if (total != release.Size) throw new IOException("The update download was interrupted. Retry the download.");
            }
            if (!(await ModFiles.HashAsync(temporary, ct)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("The update failed SHA-256 verification and was discarded.");
            File.Move(temporary, target, true);
            // Save the trusted digest separately and recheck immediately before executing.
            File.WriteAllText(target + ".sha256", release.Sha256);
            progress?.Report(new("Verified update. Restart when you're ready.", release.Size, release.Size));
            return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static Process StartInstaller(string path)
    {
        path = Path.GetFullPath(path);
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Expected a Windows installer.");
        var digest = File.ReadAllText(path + ".sha256").Trim();
        using (var stream = File.OpenRead(path))
            if (!HashPattern().IsMatch(digest) || !Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(digest, StringComparison.OrdinalIgnoreCase)) throw new IOException("The downloaded installer has changed. Download it again.");
        var info = new ProcessStartInfo(path) { UseShellExecute = true };
        info.Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTAPPLICATIONS /HEARTHUPDATE=1";
        return Process.Start(info) ?? throw new IOException("Windows could not start the updater. The current app can remain open.");
    }
    public void Dispose() { if (_owns) _client.Dispose(); }
}
