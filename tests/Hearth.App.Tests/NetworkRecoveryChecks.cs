using System.IO;
using System.Net;
using System.Xml.Linq;
using Hearth.App;

internal static class NetworkRecoveryChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var profile = Path.Combine(Path.GetTempPath(), "Hearth-recovery-network-fixture");
        HostNetworkSetup Setup(Fixture fixture) => new(profile, Path.Combine(profile, "host.exe"), 17843, fixture, TimeSpan.FromMilliseconds(60));
        var offline = new Fixture { HasRoutes = false };
        using (var setup = Setup(offline))
        {
            var initial = await setup.PrepareAsync("offline-fixture", false);
            check(initial.LocalAddresses.Count == 0 && initial.PublicAddress == null,
                "Sign-in before the network is ready does not advertise a missing address");
            offline.HasRoutes = true;
            setup.NotifyNetworkChanged();
            await Until(() => setup.Result?.PublicAddress != null);
            check(setup.Result!.LocalAddresses.Single() == "https://192.168.1.42:17843" && offline.InteractiveCalls == 0,
                "A network-address notification automatically detects routes and restores sharing without a Windows prompt");
            var existing = setup.Result; var ownedLease = offline.LastLease;
            offline.CancelPermission = true;
            try { await setup.PrepareAsync("offline-fixture", true); throw new InvalidOperationException("Canceled permission accepted"); }
            catch (OperationCanceledException) { }
            check(ReferenceEquals(existing, setup.Result) && ownedLease?.Disposed == false && offline.MapCalls == 1,
                "Canceling repair permission preserves the active mapping and advertised connection result");
            offline.CancelPermission = false;
            offline.FailRoutes = true;
            try { await setup.PrepareAsync("offline-fixture", false); throw new InvalidOperationException("Expected route failure"); }
            catch (IOException) { }
            check(setup.Result is { PublicAddress: null, RouterMapped: false },
                "An unexpected redetection failure withdraws a released mapping rather than advertising a stale address");
            offline.FailRoutes = false;
            await Until(() => setup.Result?.PublicAddress != null);
            check(offline.MapCalls >= 2, "A failed redetection retries with backoff and restores a new mapping");
            await setup.StopAsync();
            var mapCalls = offline.MapCalls;
            setup.NotifyNetworkChanged();
            await Task.Delay(160);
            check(offline.MapCalls == mapCalls && setup.Result == null,
                "Stopped hosting unsubscribes and cancels network recovery so no new router mapping is created");
        }

        var interrupted = new Fixture { FirstRenewalFails = true };
        using (var setup = Setup(interrupted))
        {
            await setup.PrepareAsync("renewal-fixture", false);
            await Until(() => interrupted.MapCalls >= 2 && setup.Result?.PublicAddress != null);
            check(interrupted.Leases[0].Disposed && setup.Result is { RouterMapped: true } && interrupted.InteractiveCalls == 0,
                "A failed router-lease renewal automatically retries and restores Internet sharing without restarting hosting");
        }
        check(AppLocalHost.NormalizeAdvertisedAddress("packs.example.com:17843") == "https://packs.example.com:17843"
            && AppLocalHost.NormalizeAdvertisedAddress("https://100.64.1.4:27843/") == "https://100.64.1.4:27843",
            "Owner-supplied DNS and VPN addresses retain their chosen port without requiring a public IPv4 literal");
        await CheckSeparateHostingPcs(check);
    }

    private static async Task CheckSeparateHostingPcs(Action<bool, string> check)
    {
        var route = new HostNetworkRoute(IPAddress.Parse("192.168.1.42"), IPAddress.Parse("192.168.1.1"));
        var mappings = new Dictionary<int, (string Address, int Inside, string Owner)>
        { [17843] = ("192.168.1.20", 17843, "Hearth-first-PC") };
        var adds = new List<int>(); var deleted = new List<int>();
        XDocument Document(params (string Name, string Value)[] values) => new(new XElement("response", values.Select(v => new XElement(v.Name, v.Value))));
        Task<XDocument> Soap(string action, KeyValuePair<string, string>[] values, CancellationToken ct)
        {
            var arguments = values.ToDictionary(p => p.Key, p => p.Value);
            if (action == "GetExternalIPAddress") return Task.FromResult(Document(("NewExternalIPAddress", "8.8.4.4")));
            var outside = int.Parse(arguments["NewExternalPort"]);
            if (action == "GetSpecificPortMappingEntry")
                return Task.FromResult(mappings.TryGetValue(outside, out var value)
                    ? Document(("NewInternalClient", value.Address), ("NewInternalPort", value.Inside.ToString()),
                        ("NewPortMappingDescription", value.Owner), ("NewLeaseDuration", "1800"))
                    : Document(("errorCode", "714")));
            if (action == "AddPortMapping")
            {
                adds.Add(outside); mappings[outside] = (arguments["NewInternalClient"], int.Parse(arguments["NewInternalPort"]), arguments["NewPortMappingDescription"]);
                return Task.FromResult(Document());
            }
            if (action == "DeletePortMapping") { deleted.Add(outside); mappings.Remove(outside); return Task.FromResult(Document()); }
            throw new InvalidOperationException("Unexpected router request");
        }
        var lease = await UpnpLease.TryMapCandidatesAsync(route, 17843, "Hearth-second-PC", new Uri("http://192.168.1.1/control"),
            "urn:schemas-upnp-org:service:WANIPConnection:1", IPAddress.Parse("8.8.4.4"), default, Soap);
        check(lease?.ExternalPort == 17844 && adds.SequenceEqual([17844]) && mappings[17844].Inside == 17843
            && mappings[17843].Owner == "Hearth-first-PC",
            "A second hosting PC selects the next external port while keeping its internal port and the first PC's mapping intact");
        if (lease != null) await lease.DisposeAsync();
        check(deleted.SequenceEqual([17844]) && mappings.ContainsKey(17843)
            && UpnpLease.CandidatePorts(65534).SequenceEqual([65534, 65535]),
            "Alternate-port cleanup removes only the second host's mapping and never exceeds valid port numbers");
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        if (!condition()) throw new TimeoutException("Automatic network recovery did not complete.");
    }

    private sealed class Fixture : IHostNetworkPlatform
    {
        public bool HasRoutes = true, CancelPermission, FailRoutes, FirstRenewalFails;
        public int InteractiveCalls, MapCalls;
        public List<Lease> Leases = [];
        public Lease? LastLease => Leases.LastOrDefault();
        public IReadOnlyList<HostNetworkRoute> GetRoutes()
        {
            if (FailRoutes) throw new IOException("Fixture routes temporarily unavailable");
            return HasRoutes ? [new(IPAddress.Parse("192.168.1.42"), IPAddress.Parse("192.168.1.1"))] : [];
        }
        public Task<bool> PrepareFirewallAsync(bool interactive, CancellationToken ct)
        {
            if (interactive) InteractiveCalls++;
            if (CancelPermission) throw new OperationCanceledException("Fixture permission canceled");
            return Task.FromResult(true);
        }
        public Task<IPAddress?> GetPublicAddressAsync(CancellationToken ct) => Task.FromResult<IPAddress?>(IPAddress.Parse("8.8.4.4"));
        public Task<IHostPortLease?> MapPortAsync(HostNetworkRoute route, int port, string owner, CancellationToken ct)
        {
            MapCalls++;
            var lease = new Lease(FirstRenewalFails && MapCalls == 1); Leases.Add(lease);
            return Task.FromResult<IHostPortLease?>(lease);
        }
    }
    private sealed class Lease(bool failRenewal) : IHostPortLease
    {
        public bool Disposed;
        public IPAddress ExternalAddress => IPAddress.Parse("8.8.4.4");
        public int ExternalPort => 27843;
        public TimeSpan Lifetime => failRenewal ? TimeSpan.FromSeconds(2) : TimeSpan.FromMinutes(30);
        public Task<bool> RenewAsync(CancellationToken ct) => Task.FromResult(!failRenewal && !Disposed);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
