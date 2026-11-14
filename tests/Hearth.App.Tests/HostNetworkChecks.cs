using System.IO;
using System.Net;
using System.Buffers.Binary;
using System.Xml.Linq;
using Hearth.App;

internal static class HostNetworkChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var profile = Path.Combine(Path.GetTempPath(), "Hearth-network-fixtures");
        HostNetworkSetup Setup(Fixture fixture) => new(profile, Path.Combine(profile, "Hearth.Host.exe"), 17843, fixture);
        var canceled = new Fixture { CancelPermission = true };
        using (var service = Setup(canceled))
        {
            try { await service.PrepareAsync("fixture", true); throw new InvalidOperationException("Canceled permission was accepted"); }
            catch (OperationCanceledException) { }
            check(canceled.MapCalls == 0 && canceled.PublicCalls == 0 && service.Result == null,
                "Canceling Windows permission aborts sharing before router changes or a successful result");
        }
        var startup = new Fixture { FirewallReady = false };
        using (var service = Setup(startup))
        {
            var result = await service.PrepareAsync("fixture", false);
            check(!startup.Interactive && !result.FirewallReady && result.PublicAddress == null && startup.MapCalls == 0,
                "Automatic startup never asks for elevation or opens the router when Windows permission is missing");
        }
        var success = new Fixture();
        using (var service = Setup(success))
        {
            var result = await service.PrepareAsync("fixture", true);
            check(result.LocalAddresses.Single() == "https://192.168.1.42:17843" && result.PublicAddress == "https://8.8.4.4:27843",
                "Sharing exposes the detected LAN address and honors the port assigned by the router");
            check(result.FirewallReady && result.RouterMapped && !result.InternetReachabilityVerified,
                "Automatic router setup never claims an external player connection was verified");
            await service.StopAsync();
            check(success.Lease.Disposed && service.Result == null, "Stopping sharing releases its own lease and clears advertised addresses");
        }
        var unsupported = new Fixture { NoMapping = true };
        using (var service = Setup(unsupported))
        {
            var result = await service.PrepareAsync("fixture", true);
            check(result.FirewallReady && result.LocalAddresses.Count == 1 && !result.RouterMapped && result.PublicAddress == null,
                "An unsupported router leaves LAN hosting usable without advertising a broken Internet address");
        }
        var direct = new Fixture { LocalAddress = IPAddress.Parse("8.8.4.4"), NoMapping = true };
        using (var service = Setup(direct))
        {
            var result = await service.PrepareAsync("fixture", true);
            check(result.PublicAddress == "https://8.8.4.4:17843" && result.FirewallReady && !result.RouterMapped
                && !result.InternetReachabilityVerified && direct.MapCalls == 0,
                "A confirmed public NIC address configures direct HTTPS sharing without touching the router or claiming outside verification");
        }
        var mismatchedNic = new Fixture { LocalAddress = IPAddress.Parse("8.8.4.4"), Observed = IPAddress.Parse("1.1.1.1"), NoMapping = true };
        using (var service = Setup(mismatchedNic))
        {
            var result = await service.PrepareAsync("fixture", true);
            check(result.PublicAddress == null && mismatchedNic.MapCalls == 1,
                "An observed Internet address that does not match the local NIC cannot bypass router setup");
        }
        foreach (var wan in new[] { "100.64.10.2", "192.168.0.2" })
        {
            var carrier = new Fixture { Lease = new Lease { ExternalAddress = IPAddress.Parse(wan) } };
            using var service = Setup(carrier); var result = await service.PrepareAsync("fixture", true);
            check(result.PublicAddress == null && !result.RouterMapped && carrier.Lease.Disposed,
                "A private or carrier-grade NAT WAN address is rejected and its temporary lease is removed");
        }
        var doubleNat = new Fixture { Observed = IPAddress.Parse("1.1.1.1") };
        using (var service = Setup(doubleNat))
        {
            var result = await service.PrepareAsync("fixture", true);
            check(result.PublicAddress == null && doubleNat.Lease.Disposed,
                "A router WAN address that disagrees with the observed Internet address is not shared as reachable");
        }
        var lost = new Fixture { Lease = new Lease { Lifetime = TimeSpan.FromMilliseconds(100), RenewalWorks = false } };
        using (var service = Setup(lost))
        {
            await service.PrepareAsync("fixture", true);
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (service.Result?.RouterMapped == true && DateTime.UtcNow < deadline) await Task.Delay(30);
            check(service.Result is { RouterMapped: false, PublicAddress: null, FirewallReady: true },
                "A failed lease renewal withdraws the Internet address while preserving local host availability");
        }
        foreach (var blocked in new[] { "0.0.0.0", "0.1.2.3", "10.1.2.3", "100.64.0.1", "100.127.255.254", "127.0.0.1",
            "169.254.1.2", "172.16.0.1", "172.31.255.254", "192.168.1.1", "192.0.2.1", "198.18.0.1", "198.51.100.1", "203.0.113.1", "224.0.0.1", "255.255.255.255", "::1" })
            check(!HostNetworkSetup.IsPublicIPv4(IPAddress.Parse(blocked)), "Non-public address is not advertised: " + blocked);
        check(HostNetworkSetup.IsPublicIPv4(IPAddress.Parse("8.8.8.8")) && HostNetworkSetup.IsPublicIPv4(IPAddress.Parse("100.128.0.1")),
            "Public IPv4 classification retains valid addresses bordering the carrier-grade NAT range");
        await CheckMappingProtocols(check);
    }

    private static async Task CheckMappingProtocols(Action<bool, string> check)
    {
        var route = new HostNetworkRoute(IPAddress.Parse("192.168.1.42"), IPAddress.Parse("192.168.1.1"));
        UpnpLease Gateway(SoapFixture fixture) => new(route, 17843, "Hearth-fixture-owner", new Uri("http://192.168.1.1/control"),
            "urn:schemas-upnp-org:service:WANIPConnection:1", IPAddress.Parse("8.8.4.4"), fixture.Send);
        var foreign = new SoapFixture { Exists = true, Description = "another application" };
        await using (var mapping = Gateway(foreign))
        {
            check(!await mapping.RenewAsync(default) && foreign.Adds == 0 && foreign.Deletes == 0,
                "UPnP refuses a foreign mapping without overwriting or deleting it");
        }
        var finite = new SoapFixture { Duration = "60" };
        var lease = Gateway(finite);
        check(await lease.RenewAsync(default) && lease.Lifetime == TimeSpan.FromSeconds(60),
            "UPnP renews according to the router's actual shorter lease");
        finite.Description = "replacement owner";
        await lease.DisposeAsync();
        check(finite.Deletes == 0, "Cleanup leaves a mapping that another application has replaced untouched");
        var permanent = new SoapFixture { PermanentOnly = true, Duration = "0" };
        lease = Gateway(permanent);
        check(await lease.RenewAsync(default) && permanent.RequestedDurations.SequenceEqual(new[] { "1800", "0" }),
            "Permanent-only UPnP fallback occurs only after the router explicitly returns error 725");
        await lease.DisposeAsync();
        check(permanent.Deletes == 1, "Graceful shutdown removes a verified app-owned permanent mapping");
        var drift = new SoapFixture(); lease = Gateway(drift);
        check(await lease.RenewAsync(default), "UPnP fixture starts with a valid mapping");
        drift.External = "1.1.1.1";
        check(!await lease.RenewAsync(default), "UPnP detects a changed WAN address before extending an outdated invite endpoint");
        await lease.DisposeAsync();

        var assigned = 27843; var currentAddress = IPAddress.Parse("8.8.4.4"); var maps = new List<byte[]>();
        Task<byte[]?> Exchange(HostNetworkRoute _, byte[] request, byte opcode, CancellationToken ct)
        {
            byte[] response;
            if (opcode == 128)
            { response = new byte[12]; currentAddress.GetAddressBytes().CopyTo(response, 8); }
            else
            {
                maps.Add(request.ToArray()); response = new byte[16];
                request.AsSpan(4, 2).CopyTo(response.AsSpan(8));
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(10), (ushort)assigned);
                BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(12), 90);
            }
            response[1] = opcode; return Task.FromResult<byte[]?>(response);
        }
        var nat = new NatPmpLease(route, 17843, currentAddress, 17843, 1800, Exchange);
        check(await nat.RenewAsync(default) && nat.ExternalPort == 27843 && nat.Lifetime == TimeSpan.FromSeconds(90),
            "NAT-PMP honors the assigned external port and actual lease length");
        assigned = 27844;
        check(!await nat.RenewAsync(default), "NAT-PMP detects a changed external port on renewal");
        await nat.DisposeAsync();
        check(BinaryPrimitives.ReadUInt16BigEndian(maps.Last().AsSpan(4)) == 17843
            && BinaryPrimitives.ReadUInt16BigEndian(maps.Last().AsSpan(6)) == 27844
            && BinaryPrimitives.ReadUInt32BigEndian(maps.Last().AsSpan(8)) == 0,
            "NAT-PMP cleanup sends a zero lifetime for only this app's internal port and assigned external port");
        nat = new NatPmpLease(route, 17843, currentAddress, 17843, 1800, Exchange); await nat.RenewAsync(default);
        currentAddress = IPAddress.Parse("1.1.1.1");
        check(!await nat.RenewAsync(default), "NAT-PMP detects a changed WAN address before renewing a stale endpoint");
        await nat.DisposeAsync();
    }

    private sealed class SoapFixture
    {
        public bool Exists, PermanentOnly;
        public string Description = "Hearth-fixture-owner", Duration = "1800", External = "8.8.4.4";
        public int Adds, Deletes;
        public List<string> RequestedDurations = [];
        private static XDocument Document(params (string Name, string Value)[] values) => new(new XElement("response", values.Select(v => new XElement(v.Name, v.Value))));
        public Task<XDocument> Send(string action, KeyValuePair<string, string>[] values, CancellationToken ct)
        {
            var arguments = values.ToDictionary(p => p.Key, p => p.Value);
            if (action == "GetExternalIPAddress") return Task.FromResult(Document(("NewExternalIPAddress", External)));
            if (action == "GetSpecificPortMappingEntry")
                return Task.FromResult(Exists ? Document(("NewInternalClient", "192.168.1.42"), ("NewInternalPort", "17843"),
                    ("NewPortMappingDescription", Description), ("NewLeaseDuration", Duration)) : Document(("errorCode", "714")));
            if (action == "AddPortMapping")
            {
                Adds++; RequestedDurations.Add(arguments["NewLeaseDuration"]);
                if (PermanentOnly && arguments["NewLeaseDuration"] != "0") return Task.FromResult(Document(("errorCode", "725")));
                Exists = true; Description = arguments["NewPortMappingDescription"]; return Task.FromResult(Document());
            }
            if (action == "DeletePortMapping") { Deletes++; Exists = false; return Task.FromResult(Document()); }
            throw new InvalidOperationException("Unexpected SOAP request " + action);
        }
    }

    private sealed class Fixture : IHostNetworkPlatform
    {
        public bool CancelPermission, Interactive, NoMapping;
        public bool FirewallReady = true;
        public int MapCalls, PublicCalls;
        public IPAddress LocalAddress = IPAddress.Parse("192.168.1.42");
        public IPAddress? Observed = IPAddress.Parse("8.8.4.4");
        public Lease Lease = new();
        public IReadOnlyList<HostNetworkRoute> GetRoutes() => [new(LocalAddress, IPAddress.Parse("192.168.1.1"))];
        public Task<bool> PrepareFirewallAsync(bool interactive, CancellationToken cancellationToken)
        {
            Interactive = interactive;
            if (CancelPermission) throw new OperationCanceledException("Fixture Windows permission canceled");
            return Task.FromResult(FirewallReady);
        }
        public Task<IPAddress?> GetPublicAddressAsync(CancellationToken cancellationToken) { PublicCalls++; return Task.FromResult(Observed); }
        public Task<IHostPortLease?> MapPortAsync(HostNetworkRoute route, int port, string owner, CancellationToken cancellationToken)
        { MapCalls++; return Task.FromResult<IHostPortLease?>(NoMapping ? null : Lease); }
    }
    private sealed class Lease : IHostPortLease
    {
        public IPAddress ExternalAddress { get; init; } = IPAddress.Parse("8.8.4.4");
        public int ExternalPort => 27843;
        public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(30);
        public bool RenewalWorks = true, Disposed;
        public Task<bool> RenewAsync(CancellationToken cancellationToken) => Task.FromResult(RenewalWorks);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
