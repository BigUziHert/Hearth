using System.IO;
using System.Net;
using System.Text.Json;
using Hearth.App;
using Hearth.Core;

internal static class SecureHostingLifecycleChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var profile = new ProfileStore(root);
        var httpPort = OwnedHostChecks.FreePort();
        var httpsPort = OwnedHostChecks.FreePort();
        while (httpsPort == httpPort) httpsPort = OwnedHostChecks.FreePort();
        var platform = new NetworkFixture { CancelPermission = true };
        string identity;
        string pin;
        string packId;
        using (var host = new AppLocalHost(root, OwnedHostChecks.HostExecutable, httpPort, httpsPort: httpsPort, networkPlatform: platform))
        {
            var canceled = false;
            try { await LocalHostingSetup.EnableAsync(profile, host.HostUrl, host.DataDirectory, host.StartAsync); }
            catch (OperationCanceledException) { canceled = true; }
            var saved = new ProfileStore(root).State;
            check(canceled && !host.IsRunning && host.ProcessId is null && saved.Hosts.Count == 0 && !saved.LocalHostingEnabled,
                "Canceling Windows sharing permission leaves the real host stopped and saves no broken enabled host entry");
            check(!string.IsNullOrWhiteSpace(saved.LocalHostOwnerToken) && File.Exists(Path.Combine(host.DataDirectory, "host.json"))
                && File.Exists(HostCertificate.FilePath(host.DataDirectory)) && platform.MapCalls == 0,
                "Canceled secure setup preserves its recoverable owner key, host data, and certificate without changing the router");
            identity = host.HostId!; pin = host.CertificatePin!;
            check(identity.Length > 0 && pin.Length == 64 && !await OwnedHostChecks.PortOpenAsync(httpPort) && !await OwnedHostChecks.PortOpenAsync(httpsPort),
                "Canceled secure setup leaves neither the local management port nor the HTTPS port listening");

            platform.CancelPermission = false;
            var connection = await LocalHostingSetup.EnableAsync(profile, host.HostUrl, host.DataDirectory, host.StartAsync);
            using var api = new HearthApi($"https://127.0.0.1:{httpsPort}", connection.OwnerToken, certificatePin: pin);
            var health = await api.HealthAsync();
            var pack = await api.CreatePackAsync(new("Secure lifecycle pack", "1.21.1", "fabric", "0.16.10"));
            packId = pack.Id;
            check(host.IsRunning && profile.State.LocalHostingEnabled && profile.State.Hosts.Count == 1
                && health.HostId == identity && host.CertificatePin == pin && (await api.ListPacksAsync()).Single().Id == packId,
                "Retrying secure setup starts authenticated HTTPS with the original identity, certificate, and owner credential");
            check(host.Network?.Result is { FirewallReady: true, RouterMapped: true, InternetReachabilityVerified: false }
                && platform.MapCalls == 1, "Secure host startup advertises its configured route without claiming an external connection was verified");
            var process = host.ProcessId;
            var mapping = platform.LastLease;
            platform.CancelPermission = true;
            try { await host.RetryNetworkAsync(connection.OwnerToken); throw new InvalidOperationException("Canceled repair was accepted"); }
            catch (OperationCanceledException) { }
            check(host.IsRunning && host.ProcessId == process && mapping?.Disposed == false
                && (await api.ListPacksAsync()).Single().Id == packId && platform.MapCalls == 1,
                "Canceling the Windows permission step during repair preserves the running HTTPS host, its packs, and its router lease");
            platform.CancelPermission = false;
            host.SetAdvertisedAddress($"https://localhost:{httpsPort}");
            using var dnsApi = new HearthApi(host.InternetAddress!, connection.OwnerToken, certificatePin: pin);
            var advertised = await dnsApi.EndpointsAsync();
            check((await dnsApi.HealthAsync()).HostId == identity && (await dnsApi.ListPacksAsync()).Single().Id == packId,
                "A DNS address connects using the existing certificate pin without changing the self-signed host certificate");
            check(advertised.HostId == identity && advertised.CertificatePin == pin
                && advertised.InternetAddress == $"https://localhost:{httpsPort}"
                && advertised.HomeNetworkAddresses.Single() == $"https://192.168.1.42:{httpsPort}",
                "Remote administrators receive the owner's configured Internet and home-network endpoints with host identity and certificate pin");
            host.Dispose();
            await OwnedHostChecks.AssertStoppedAsync(host.HostUrl, process);
            await OwnedHostChecks.AssertStoppedAsync($"https://127.0.0.1:{httpsPort}", process);
            check(platform.LastLease?.Disposed == true && !host.IsRunning,
                "Fully disposing the launcher-owned secure host stops both listeners and releases its router lease");
            var stoppedEndpoints = JsonSerializer.Deserialize<HostEndpoints>(File.ReadAllText(Path.Combine(host.DataDirectory, "advertised-endpoints.json")), JsonDefaults.Options)!;
            check(stoppedEndpoints.InternetAddress == null && stoppedEndpoints.HomeNetworkAddresses.Count == 0,
                "Stopping the host withdraws its saved advertised endpoints instead of leaving links marked active");
        }

        platform.FirewallAllowed = false;
        var interactiveCalls = platform.InteractiveCalls;
        using (var resumed = new AppLocalHost(root, OwnedHostChecks.HostExecutable, httpPort, httpsPort: httpsPort, networkPlatform: platform))
        {
            var connection = await LocalHostingSetup.EnableAsync(profile, resumed.HostUrl, resumed.DataDirectory,
                (key, ct) => resumed.StartAsync(key, interactive: false, ct));
            using var api = new HearthApi(resumed.HostUrl, connection.OwnerToken);
            check(resumed.IsRunning && resumed.Network?.Result is { FirewallReady: false, PublicAddress: null, RouterMapped: false }
                && !await OwnedHostChecks.PortOpenAsync(httpsPort) && platform.InteractiveCalls == interactiveCalls,
                "Automatic startup with missing Windows permission keeps local hosting usable without prompting or exposing HTTPS");
            check((await api.HealthAsync()).HostId == identity && resumed.CertificatePin == pin
                && (await api.ListPacksAsync()).Single().Id == packId,
                "Restarting without firewall permission preserves hosted packs and the stable secure host identity");
            var process = resumed.ProcessId;
            await resumed.StopAsync();
            await OwnedHostChecks.AssertStoppedAsync(resumed.HostUrl, process);
            check(resumed.Network?.Result is null && !await OwnedHostChecks.PortOpenAsync(httpsPort),
                "Stopping after automatic startup clears sharing status and leaves both host ports closed");
        }

        var recoveryRoot = Path.Combine(root, "recovery-profile");
        var recovery = new ProfileStore(recoveryRoot);
        recovery.Save();
        using var recovered = new AppLocalHost(recoveryRoot, OwnedHostChecks.HostExecutable, httpPort, Path.Combine(root, "Host"));
        var incorrect = OwnedHostChecks.NewOwnerToken();
        var rejected = false;
        try
        {
            await LocalHostingSetup.EnableAsync(recovery, recovered.HostUrl, recovered.DataDirectory, recovered.StartAsync, recoveryKey: incorrect);
        }
        catch (InvalidOperationException error) when (error.Message.Contains("doesn't match this host's data", StringComparison.Ordinal)) { rejected = true; }
        var rejectedState = new ProfileStore(recoveryRoot).State;
        check(rejected && rejectedState.LocalHostOwnerToken == "" && !rejectedState.LocalHostingEnabled
            && rejectedState.Hosts.Count == 0 && !recovered.IsRunning,
            "A real 403 for a pasted recovery key keeps the previous profile unchanged, hosting disabled, and recovery available");
        recovery.State.LocalHostOwnerToken = incorrect; recovery.Save();
        var original = profile.State.LocalHostOwnerToken;
        var replacement = await LocalHostingSetup.EnableAsync(recovery, recovered.HostUrl, recovered.DataDirectory,
            async (candidate, ct) =>
            {
                check(new ProfileStore(recoveryRoot).State.LocalHostOwnerToken == incorrect,
                    "An already-saved wrong key is not overwritten until the replacement passes real owner authentication");
                await recovered.StartAsync(candidate, ct);
            }, verifiedHostId: () => recovered.HostId, recoveryKey: original);
        using var recoveredApi = new HearthApi(recovered.HostUrl, replacement.OwnerToken);
        check(new ProfileStore(recoveryRoot).State.LocalHostOwnerToken == original && recovered.IsRunning
            && replacement.HostId == identity && (await recoveredApi.ListPacksAsync()).Single().Id == packId,
            "Replacing an already-saved wrong owner key recovers the original packs and saves only the verified key");
    }

    private sealed class NetworkFixture : IHostNetworkPlatform
    {
        public bool CancelPermission;
        public bool FirewallAllowed = true;
        public int InteractiveCalls;
        public int MapCalls;
        public Lease? LastLease;
        public IReadOnlyList<HostNetworkRoute> GetRoutes() => [new(IPAddress.Parse("192.168.1.42"), IPAddress.Parse("192.168.1.1"))];
        public Task<bool> PrepareFirewallAsync(bool interactive, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (interactive) InteractiveCalls++;
            if (CancelPermission) throw new OperationCanceledException("The Windows permission request was canceled.");
            return Task.FromResult(FirewallAllowed);
        }
        public Task<IPAddress?> GetPublicAddressAsync(CancellationToken ct) => Task.FromResult<IPAddress?>(IPAddress.Parse("8.8.4.4"));
        public Task<IHostPortLease?> MapPortAsync(HostNetworkRoute route, int port, string owner, CancellationToken ct)
        {
            MapCalls++; LastLease = new Lease(port);
            return Task.FromResult<IHostPortLease?>(LastLease);
        }
    }
    private sealed class Lease(int port) : IHostPortLease
    {
        public bool Disposed;
        public IPAddress ExternalAddress => IPAddress.Parse("8.8.4.4");
        public int ExternalPort => port;
        public TimeSpan Lifetime => TimeSpan.FromMinutes(30);
        public Task<bool> RenewAsync(CancellationToken ct) => Task.FromResult(!Disposed);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
