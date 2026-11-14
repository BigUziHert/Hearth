using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Hearth.Core;

internal static class SecureHostChecks
{
    public static async Task RunAsync(string directory, Action<bool, string> check)
    {
        using var certificate = HostCertificate.LoadOrCreate(Path.Combine(directory, "identity"));
        var pin = HostCertificate.GetPin(certificate);
        using (var restored = HostCertificate.LoadOrCreate(Path.Combine(directory, "identity")))
            check(HostCertificate.GetPin(restored) == pin && restored.HasPrivateKey, "HTTPS host identity persists across restarts with its private key");
        var missing = Path.Combine(directory, "missing-identity");
        try { using var unexpected = HostCertificate.LoadExisting(missing); throw new InvalidOperationException("Missing identity accepted"); }
        catch (FileNotFoundException) { check(!Directory.Exists(missing), "Read-only identity verification never creates a missing host certificate"); }

        var link = new HostLink("https://localhost:17843", pin, "host-identity");
        check(HostLink.Parse("https://packs.example.com/") == new HostLink("https://packs.example.com")
            && HostLink.Parse("http://127.0.0.1:17842").CertificatePin is null, "Existing HTTPS addresses and localhost addresses remain usable without a pin");
        check(HostLink.Parse(link.ToString()) == link, "Owner connection link preserves address and certificate identity without credentials");
        var invite = new Invite(link.HostUrl, "private-pack", "invitation", pin, link.HostId);
        check(Invite.Parse(invite.ToString()) == invite, "Pack invite preserves the exact trusted HTTPS identity");
        foreach (var invalid in new[]
        {
            link + "&pin=" + pin, link + "&token=owner-secret", link + "&unexpected=value",
            "hearth://host?host=https%3A%2F%2Flocalhost&pin=bad", "hearth://host:99?host=https%3A%2F%2Flocalhost",
            "hearth://host?host=https%3A%2F%2Flocalhost%0a", "hearth://host?host=https%3A%2F%2Flocalhost%XX"
        })
            Rejected(() => HostLink.Parse(invalid), check, "Invalid, ambiguous, or credential-bearing host connection link is rejected");
        Rejected(() => new HearthApi(link.HostUrl, certificatePin: "invalid"), check, "Malformed certificate identity fails before any request");
        Rejected(() => new HearthApi("http://127.0.0.1:17842", certificatePin: pin), check, "Pinned connections cannot downgrade to HTTP");
        using (var untrustedClient = new HttpClient())
            Rejected(() => new HearthApi(link.HostUrl, "secret", untrustedClient, pin), check, "Custom HTTP clients cannot bypass a supplied certificate identity");

        await using var server = new TlsFixture(certificate);
        using (var api = new HearthApi(server.Url, "test-owner-secret", certificatePin: pin))
            check((await api.HealthAsync()).HostId == "tls-fixture", "Actual HTTPS handshake accepts only the expected self-signed host identity");
        check(server.Credentials.Count == 1 && server.Credentials[0] == "Bearer test-owner-secret", "Verified HTTPS delivers the credential only after host identity validation");
        using (var api = new HearthApi(server.Url, "must-not-arrive", certificatePin: new string('0', 64)))
            await Refused(() => api.HealthAsync(), check, "Mismatched HTTPS certificate blocks credentials and displays an actionable identity error", "certificate identity");
        using (var api = new HearthApi(server.Url, "must-not-arrive"))
            await Refused(() => api.HealthAsync(), check, "Unpinned HTTPS retains Windows certificate validation and rejects a self-signed host");
        check(server.Credentials.Count == 1, "Rejected TLS handshakes deliver no HTTP request or owner credential");
        using (var client = HearthApi.CreateClient(pin, TimeSpan.FromSeconds(3)))
            await Refused(() => client.GetAsync("http://127.0.0.1:17842/health"), check, "A pinned raw HTTP client also refuses plaintext transport before sending");

        await using (var redirected = new TlsFixture(certificate))
        await using (var redirecting = new TlsFixture(certificate, redirected.Url))
        using (var api = new HearthApi(redirecting.Url, "do-not-forward", certificatePin: pin))
        {
            try { await api.HealthAsync(); throw new InvalidOperationException("Redirect accepted"); }
            catch (HostApiException) { check(redirected.Credentials.Count == 0, "HTTPS redirects are not followed and cannot forward owner credentials"); }
        }

        using var expired = CreateExpiredCertificate();
        await using (var expiredServer = new TlsFixture(expired))
        using (var api = new HearthApi(expiredServer.Url, "must-not-arrive", certificatePin: HostCertificate.GetPin(expired)))
        {
            await Refused(() => api.HealthAsync(), check, "Even an exact pinned certificate is refused after expiry with a clear error", "expired");
            check(expiredServer.Credentials.Count == 0, "Expired certificate receives no credential");
        }

        var damaged = Path.Combine(directory, "damaged");
        using (HostCertificate.LoadOrCreate(damaged)) { }
        var file = HostCertificate.FilePath(damaged);
        File.WriteAllText(file, "damaged host identity");
        try { using var unexpected = HostCertificate.LoadOrCreate(damaged); throw new InvalidOperationException("Damaged identity replaced"); }
        catch (IOException) { check(File.ReadAllText(file) == "damaged host identity", "Damaged persisted certificate is reported without silently replacing host identity"); }
        File.WriteAllBytes(file, expired.Export(X509ContentType.Pfx));
        var original = File.ReadAllBytes(file);
        try { using var unexpected = HostCertificate.LoadOrCreate(damaged); throw new InvalidOperationException("Expired identity replaced"); }
        catch (IOException) { check(File.ReadAllBytes(file).SequenceEqual(original), "Expired persisted certificate is reported without silently replacing host identity"); }
    }

    private static void Rejected(Action action, Action<bool, string> check, string name)
    {
        try { action(); throw new InvalidOperationException(name); }
        catch (ArgumentException) { check(true, name); }
    }
    private static async Task Refused(Func<Task> action, Action<bool, string> check, string name, string? visibleError = null)
    {
        try { await action(); throw new InvalidOperationException(name); }
        catch (HttpRequestException exception) { check(visibleError is null || exception.Message.Contains(visibleError, StringComparison.OrdinalIgnoreCase), name); }
        catch (AuthenticationException exception) { check(visibleError is null || exception.Message.Contains(visibleError, StringComparison.OrdinalIgnoreCase), name); }
    }
    private static X509Certificate2 CreateExpiredCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-1));
        return X509CertificateLoader.LoadPkcs12(temporary.Export(X509ContentType.Pfx), null,
            (OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet) | X509KeyStorageFlags.Exportable);
    }

    private sealed class TlsFixture : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task serving;
        public List<string> Credentials { get; } = [];
        public string Url { get; }
        public TlsFixture(X509Certificate2 certificate, string? redirect = null)
        {
            listener.Start(); Url = "https://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            serving = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                    catch (SocketException) when (stop.IsCancellationRequested) { break; }
                    catch (ObjectDisposedException) when (stop.IsCancellationRequested) { break; }
                    using (client)
                    using (var ssl = new SslStream(client.GetStream(), false))
                    {
                        try
                        {
                            using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                            requestDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, requestDeadline.Token);
                            using var reader = new StreamReader(ssl, Encoding.ASCII, false, 1024, true);
                            var first = await reader.ReadLineAsync(requestDeadline.Token);
                            if (string.IsNullOrEmpty(first)) continue;
                            string? line; var credential = "";
                            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(requestDeadline.Token)))
                                if (line.StartsWith("Authorization: ", StringComparison.OrdinalIgnoreCase)) credential = line[15..];
                            Credentials.Add(credential);
                            var body = "{\"name\":\"Test\",\"hostId\":\"tls-fixture\",\"version\":\"1.0.0\",\"time\":\"2026-01-01T00:00:00Z\"}";
                            var response = redirect is null ? "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" : "HTTP/1.1 302 Found\r\nLocation: " + redirect + "/health\r\n";
                            response += "Content-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body;
                            await ssl.WriteAsync(Encoding.UTF8.GetBytes(response), requestDeadline.Token);
                        }
                        catch (Exception exception) when (exception is AuthenticationException or IOException or OperationCanceledException) { }
                    }
                }
            });
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); listener.Stop();
            await serving;
            stop.Dispose();
        }
    }
}
