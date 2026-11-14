using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hearth.Core;

public sealed class HostApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed class HearthApi : IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly string? _token;
    private sealed record ClientTrust(string? Pin);
    private sealed class HostIdentityException(string message) : AuthenticationException(message);
    private sealed class HttpsOnlyHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Scheme != "https") throw new AuthenticationException("A pinned host connection requires HTTPS. No access credentials were sent.");
            try { return await base.SendAsync(request, cancellationToken).ConfigureAwait(false); }
            catch (HttpRequestException exception)
            {
                for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
                    if (cause is HostIdentityException)
                        throw new HttpRequestException(cause.Message, exception, exception.StatusCode);
                throw;
            }
        }
    }
    private static readonly ConditionalWeakTable<HttpClient, ClientTrust> TrustedClients = new();
    public string HostUrl { get; }
    public HearthApi(string hostUrl, string? token = null, HttpClient? client = null, string? certificatePin = null)
    {
        HostUrl = NormalizeHost(hostUrl);
        var pin = certificatePin is null ? null : HostCertificate.NormalizePin(certificatePin);
        if (pin is not null && !HostUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A host certificate identity requires HTTPS. Reconnect using the owner's current host connection link.");
        if (pin is not null && client is not null && (!TrustedClients.TryGetValue(client, out var trust) || trust.Pin != pin))
            throw new ArgumentException("Create a pinned HTTP client with HearthApi.CreateClient before supplying it to HearthApi.");
        _token = token;
        _ownsClient = client is null;
        _client = client ?? CreateClient(pin);
    }

    public static HttpClient CreateClient(string? certificatePin = null, TimeSpan? timeout = null)
    {
        var pin = certificatePin is null ? null : HostCertificate.NormalizePin(certificatePin);
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (pin is not null)
        {
            var expected = Convert.FromHexString(pin);
            handler.ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
            {
                if (request.RequestUri?.Scheme != "https" || certificate is null || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
                    throw new HostIdentityException("The host did not provide its HTTPS certificate. No access credentials were sent.");
                if (!CryptographicOperations.FixedTimeEquals(expected, certificate.GetCertHash(HashAlgorithmName.SHA256)))
                    throw new HostIdentityException("The host certificate identity changed or does not match this invite. Ask the owner for a fresh link. No access credentials were sent.");
                if (certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
                    throw new HostIdentityException("The host certificate is expired or not yet valid. Check the host and player PC clocks. No access credentials were sent.");
                // The invite's exact certificate is the trust anchor; DNS names and public CA trust are not needed.
                return true;
            };
        }
        var client = new HttpClient(pin is null ? handler : new HttpsOnlyHandler(handler)) { Timeout = timeout ?? TimeSpan.FromMinutes(15) };
        TrustedClients.Add(client, new(pin));
        return client;
    }
    public static string NormalizeHost(string value)
    {
        if (value is null || value.Length > 2048 || value.Any(char.IsControl) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("Use an HTTPS host address. HTTP is allowed only for localhost on this PC.");
        return uri.ToString().TrimEnd('/');
    }
    private HttpRequestMessage Request(HttpMethod method, string route, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, HostUrl + route) { Content = content };
        request.Headers.UserAgent.ParseAdd("Hearth/1.0");
        if (!string.IsNullOrEmpty(_token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }
    private static string Id(string value) => Uri.EscapeDataString(value);
    private async Task<T> Send<T>(HttpMethod method, string route, object? payload, CancellationToken ct)
    {
        using var request = Request(method, route, payload is null ? null : JsonContent.Create(payload, options: JsonDefaults.Options));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new IOException("The host returned too much metadata.");
        var bytes = await ReadLimited(response.Content, 8 * 1024 * 1024, ct);
        return JsonSerializer.Deserialize<T>(bytes, JsonDefaults.Options) ?? throw new IOException("The host returned an empty response.");
    }
    private static async Task<byte[]> ReadLimited(HttpContent content, int limit, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var result = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (result.Length + count > limit) throw new IOException("The host response exceeded the allowed size.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }
    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = "";
        try
        {
            var data = await ReadLimited(response.Content, 65536, ct);
            using var json = JsonDocument.Parse(data);
            if (json.RootElement.TryGetProperty("error", out var error)) detail = error.GetString() ?? "";
            else if (json.RootElement.TryGetProperty("detail", out var d)) detail = d.GetString() ?? "";
        }
        catch { /* Never expose an HTML response or credentials in errors. */ }
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "This host did not accept your access key. Reconnect or request access again.",
            HttpStatusCode.Forbidden => "You do not have access to this pack. Ask its owner to approve you.",
            HttpStatusCode.NotFound => "The pack or release is not available on this host.",
            HttpStatusCode.Conflict => "This pack changed since you opened it. Refresh before publishing again.",
            (HttpStatusCode)429 => "Too many requests. Wait a moment, then retry.",
            _ => "The host could not complete the request (" + (int)response.StatusCode + ")."
        };
        throw new HostApiException(response.StatusCode, message + (detail.Length is > 0 and < 500 ? " " + detail : ""));
    }
    public Task<HealthResponse> HealthAsync(CancellationToken ct = default) => Send<HealthResponse>(HttpMethod.Get, "/health", null, ct);
    public Task<List<PackSummary>> ListPacksAsync(CancellationToken ct = default) => Send<List<PackSummary>>(HttpMethod.Get, "/owner/packs", null, ct);
    public Task<PackSummary> CreatePackAsync(CreatePackRequest request, CancellationToken ct = default) => Send<PackSummary>(HttpMethod.Post, "/owner/packs", request, ct);
    public Task<JoinResponse> JoinAsync(string packId, JoinRequest request, CancellationToken ct = default) => Send<JoinResponse>(HttpMethod.Post, $"/packs/{Id(packId)}/join", request, ct);
    public Task<AccessResponse> AccessAsync(string packId, CancellationToken ct = default) => Send<AccessResponse>(HttpMethod.Get, $"/packs/{Id(packId)}/access", null, ct);
    public Task<List<MemberInfo>> MembersAsync(string packId, CancellationToken ct = default) => Send<List<MemberInfo>>(HttpMethod.Get, $"/packs/{Id(packId)}/members", null, ct);
    public Task<MemberInfo> UpdateMemberAsync(string packId, string memberId, MemberUpdate request, CancellationToken ct = default) => Send<MemberInfo>(HttpMethod.Patch, $"/packs/{Id(packId)}/members/{Id(memberId)}", request, ct);
    public Task<PackManifest> ManifestAsync(string packId, CancellationToken ct = default) => Send<PackManifest>(HttpMethod.Get, $"/packs/{Id(packId)}/manifest", null, ct);
    public Task<PackManifest> PublishAsync(string packId, PublishRequest request, CancellationToken ct = default) => Send<PackManifest>(HttpMethod.Post, $"/packs/{Id(packId)}/releases", request, ct);
    public async Task UploadAsync(string packId, string localPath, PackFile file, IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        var verified = await ModFiles.InspectAsync(localPath, file.Group, ct);
        if (!verified.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase) || verified.Size != file.Size) throw new IOException("This JAR changed on disk. Add it to the draft again before publishing.");
        await using var input = File.OpenRead(localPath);
        using var request = Request(HttpMethod.Put, $"/packs/{Id(packId)}/blobs/{Id(file.Sha256)}", new StreamContent(input));
        request.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/java-archive");
        progress?.Report(new("Uploading " + file.Path, 0, file.Size));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        progress?.Report(new("Uploaded " + file.Path, file.Size, file.Size));
    }
    public async Task DownloadAsync(string packId, PackFile file, string destination, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, $"/packs/{Id(packId)}/blobs/{Id(file.Sha256)}");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        if (response.Content.Headers.ContentLength is long length && length != file.Size) throw new IOException("The download size does not match the published release.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            total += count;
            if (total > file.Size) throw new IOException("The host sent more bytes than the release declares.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
            progress?.Report(new("Downloading " + file.Path, total, file.Size));
        }
        await output.FlushAsync(ct);
        output.Flush(true);
        if (total != file.Size) throw new IOException("The connection ended before the file was complete. Your previous installation is safe; retry Play.");
    }
    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
