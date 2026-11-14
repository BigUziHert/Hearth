using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Hearth.App.Tests")]

namespace Hearth.App;

public sealed record HostNetworkRoute(IPAddress LocalAddress, IPAddress Gateway);
public sealed record HostNetworkResult(IReadOnlyList<string> LocalAddresses, string? PublicAddress,
    bool FirewallReady, bool RouterMapped, string Status)
{
    // A router acknowledgement or a local hairpin request cannot prove external reachability.
    public bool InternetReachabilityVerified => false;
}

public interface IHostPortLease : IAsyncDisposable
{
    IPAddress ExternalAddress { get; }
    int ExternalPort { get; }
    TimeSpan Lifetime { get; }
    Task<bool> RenewAsync(CancellationToken cancellationToken);
}

/// <summary>All OS and network effects are replaceable; tests never need a real router or elevation.</summary>
public interface IHostNetworkPlatform
{
    IReadOnlyList<HostNetworkRoute> GetRoutes();
    Task<bool> PrepareFirewallAsync(bool interactive, CancellationToken cancellationToken);
    Task<IPAddress?> GetPublicAddressAsync(CancellationToken cancellationToken);
    Task<IHostPortLease?> MapPortAsync(HostNetworkRoute route, int port, string owner, CancellationToken cancellationToken);
}

public sealed class HostNetworkSetup : IDisposable
{
    private readonly IHostNetworkPlatform platform;
    private readonly int port;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? renewalCancellation;
    private Task? renewal;
    private IHostPortLease? lease;
    private bool disposed;
    public HostNetworkResult? Result { get; private set; }

    public HostNetworkSetup(string profileRoot, string hostExecutable, int httpsPort = 17843, IHostNetworkPlatform? platform = null)
    {
        if (httpsPort is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(httpsPort));
        port = httpsPort;
        this.platform = platform ?? new WindowsHostNetworkPlatform(profileRoot, hostExecutable, httpsPort);
    }

    public async Task<HostNetworkResult> PrepareAsync(string hostId, bool interactive, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await StopLeaseAsync().ConfigureAwait(false);
            Result = null;
            var routes = platform.GetRoutes();
            var addresses = routes.Select(route => $"https://{route.LocalAddress}:{port}").Distinct().ToArray();
            // Ask for elevation before any router mutation. A canceled UAC propagates to the caller,
            // which must not save an enabled host entry.
            if (!await platform.PrepareFirewallAsync(interactive, ct).ConfigureAwait(false))
                return Result = new(addresses, null, false, false,
                    "Windows has not allowed hosting connections. Open Hosting and choose Retry connection setup to finish the Windows permission step.");
            if (routes.Count == 0)
                return Result = new(addresses, null, true, false, "No home network connection was found. Connect to Wi-Fi or Ethernet and retry sharing.");

            IPAddress? observed = null;
            try { observed = await platform.GetPublicAddressAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is HttpRequestException or IOException or SocketException or OperationCanceledException) { }
            ct.ThrowIfCancellationRequested();
            // A server with a public address directly on its NIC needs no router mapping. Merely
            // learning an Internet address is insufficient: it must match an actual local interface.
            if (observed != null && IsPublicIPv4(observed) && routes.Any(route => route.LocalAddress.Equals(observed)))
                return Result = new(addresses, $"https://{observed}:{port}", true, false,
                    "Secure sharing is configured using this PC's public address. A friend connecting from another network will confirm it is reachable.");
            string? mappingIssue = null;
            foreach (var route in routes.Take(3))
            {
                ct.ThrowIfCancellationRequested();
                try { lease = await platform.MapPortAsync(route, port, "Hearth-" + SafeOwner(hostId), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is IOException or HttpRequestException or SocketException or XmlException or TimeoutException or OperationCanceledException)
                { mappingIssue = error is HostNetworkException ? error.Message : "Your router did not finish automatic sharing setup."; }
                if (lease != null) break;
            }
            ct.ThrowIfCancellationRequested();
            if (lease == null)
                return Result = new(addresses, null, true, false, mappingIssue ?? "Sharing works on your home network. Your router did not allow automatic Internet sharing. Retry after reconnecting, or use a network that permits incoming connections.");
            if (!IsPublicIPv4(lease.ExternalAddress) || (observed != null && !observed.Equals(lease.ExternalAddress)))
            {
                await lease.DisposeAsync().ConfigureAwait(false); lease = null;
                return Result = new(addresses, null, true, false,
                    "Sharing works on your home network. Another router or your Internet provider is blocking direct Internet sharing. Ask your Internet provider whether incoming connections and a public IP are available. Hearth cannot open this network automatically.");
            }
            var address = $"https://{lease.ExternalAddress}:{lease.ExternalPort}";
            Result = new(addresses, address, true, true, "Internet sharing is configured automatically. A friend connecting from another network will confirm it is reachable.");
            renewalCancellation = new();
            renewal = RenewLeaseAsync(lease, renewalCancellation.Token);
            return Result;
        }
        catch
        {
            await StopLeaseAsync().ConfigureAwait(false);
            throw;
        }
        finally { gate.Release(); }
    }

    private static string SafeOwner(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..20];
    private async Task RenewLeaseAsync(IHostPortLease active, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(active.Lifetime.TotalSeconds / 2, 1, 1800)), ct).ConfigureAwait(false);
                if (!await active.RenewAsync(ct).ConfigureAwait(false)) throw new IOException("The router mapping expired.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch
        {
            if (Result is { } result) Result = result with { PublicAddress = null, RouterMapped = false,
                Status = "The router stopped automatic Internet sharing. Your local host is still running. Retry sharing." };
            try { await active.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { await StopLeaseAsync().ConfigureAwait(false); Result = null; }
        finally { gate.Release(); }
    }
    private async Task StopLeaseAsync()
    {
        renewalCancellation?.Cancel();
        if (renewal != null) { try { await renewal.ConfigureAwait(false); } catch { } }
        renewalCancellation?.Dispose(); renewalCancellation = null; renewal = null;
        if (lease != null) { try { await lease.DisposeAsync().ConfigureAwait(false); } catch { } lease = null; }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // Graceful shutdown removes owned mappings. Routers that only support permanent mappings
        // can retain a port after a crash, but the owned host listener dies with the launcher.
        try { StopAsync().Wait(TimeSpan.FromSeconds(4)); } catch { }
    }

    public static bool IsPublicIPv4(IPAddress address) => Hearth.Core.HostNetworkAddress.IsPublicIPv4(address);
}

public sealed class HostNetworkException(string message) : IOException(message);

public sealed class WindowsHostNetworkPlatform : IHostNetworkPlatform
{
    private readonly string executable;
    private readonly int port;
    private readonly string ruleName;
    public WindowsHostNetworkPlatform(string profileRoot, string hostExecutable, int httpsPort)
    {
        executable = Path.GetFullPath(hostExecutable); port = httpsPort;
        ruleName = "Hearth.Hosting." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(profileRoot).ToUpperInvariant() + "\n" + executable.ToUpperInvariant() + "\n" + port)))[..24];
    }

    public IReadOnlyList<HostNetworkRoute> GetRoutes()
    {
        var routes = new List<HostNetworkRoute>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up
            && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel))
        {
            var properties = adapter.GetIPProperties();
            foreach (var gateway in properties.GatewayAddresses.Select(g => g.Address).Where(IsUsableV4))
            foreach (var local in properties.UnicastAddresses.Where(a => IsUsableV4(a.Address)))
            {
                var mask = local.IPv4Mask.GetAddressBytes(); var gatewayBytes = gateway.GetAddressBytes(); var localBytes = local.Address.GetAddressBytes();
                if (Enumerable.Range(0, 4).All(i => (gatewayBytes[i] & mask[i]) == (localBytes[i] & mask[i])))
                    routes.Add(new(local.Address, gateway));
            }
        }
        return routes.Distinct().ToArray();
    }
    private static bool IsUsableV4(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork
        && !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any)
        && !(address.GetAddressBytes()[0] == 169 && address.GetAddressBytes()[1] == 254);

    public async Task<bool> PrepareFirewallAsync(bool interactive, CancellationToken ct)
    {
        if (FirewallReady()) return true;
        if (!interactive) return false;
        ct.ThrowIfCancellationRequested();
        var script = Path.Combine(AppContext.BaseDirectory, "scripts", "configure-host-firewall.ps1");
        if (!File.Exists(script)) throw new IOException("The Windows hosting helper is missing. Reinstall Hearth and retry.");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
            "-ProgramPath", executable, "-Port", port.ToString(), "-RuleName", ruleName }) start.ArgumentList.Add(arg);
        Process process;
        try { process = Process.Start(start) ?? throw new IOException("Windows could not start the hosting permission helper."); }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { throw new OperationCanceledException("Windows permission was canceled. Hosting was not enabled.", error, ct); }
        using (process)
        {
            // Once launched, let the narrowly scoped elevation finish; do not leave a detached rule writer.
            await process.WaitForExitAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || !FirewallReady()) throw new IOException("Windows did not allow incoming Hearth connections. Retry hosting and approve the Windows permission request.");
        }
        return true;
    }
    private bool FirewallReady()
    {
        object? policy = null; object? rules = null; object? rule = null;
        try
        {
            policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!);
            rules = ((dynamic)policy!).Rules; rule = ((dynamic)rules).Item(ruleName); dynamic value = rule;
            return value.Enabled && value.Direction == 1 && value.Action == 1 && value.Protocol == 6
                && string.Equals((string)value.ApplicationName, executable, StringComparison.OrdinalIgnoreCase)
                && (string)value.LocalPorts == port.ToString() && (((int)value.Profiles & 7) == 7);
        }
        // The COM "rule not found" HRESULT is surfaced as FileNotFoundException by .NET.
        // Missing permission is normal on first run, not a failure to start hosting.
        catch (Exception error) when (error is COMException or FileNotFoundException or TypeLoadException or ArgumentException) { return false; }
        finally
        {
            foreach (var item in new[] { rule, rules, policy }) if (item != null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
        }
    }
    public async Task<IPAddress?> GetPublicAddressAsync(CancellationToken ct)
    {
        using var client = NewHttpClient(); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var value = await client.GetStringAsync("https://api.ipify.org", timeout.Token).ConfigureAwait(false);
        return value.Length < 64 && IPAddress.TryParse(value.Trim(), out var address) && HostNetworkSetup.IsPublicIPv4(address) ? address : null;
    }
    public async Task<IHostPortLease?> MapPortAsync(HostNetworkRoute route, int port, string owner, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15)); ct = timeout.Token;
        // NAT-PMP uses expiring mappings and never replaces a different client's mapping.
        IHostPortLease? nat = null;
        try { nat = await NatPmpLease.TryCreateAsync(route, port, ct).ConfigureAwait(false); }
        catch (SocketException) { /* Routers without NAT-PMP may reject UDP; try UPnP next. */ }
        if (nat != null) return nat;
        return await UpnpLease.TryCreateAsync(route, port, owner, ct).ConfigureAwait(false);
    }
    internal static HttpClient NewHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(4), MaxResponseContentBufferSize = 1024 };
}

internal sealed class NatPmpLease(HostNetworkRoute route, int localPort, IPAddress address, int externalPort, int lifetime,
    Func<HostNetworkRoute, byte[], byte, CancellationToken, Task<byte[]?>>? exchange = null) : IHostPortLease
{
    private bool established;
    public IPAddress ExternalAddress { get; } = address;
    public int ExternalPort { get; private set; } = externalPort;
    public TimeSpan Lifetime { get; private set; } = TimeSpan.FromSeconds(lifetime);
    public static async Task<IHostPortLease?> TryCreateAsync(HostNetworkRoute route, int port, CancellationToken ct)
    {
        var response = await Exchange(route, [0, 0], 128, ct).ConfigureAwait(false);
        if (response is not { Length: >= 12 }) return null;
        var address = new IPAddress(response.AsSpan(8, 4));
        if (!HostNetworkSetup.IsPublicIPv4(address)) throw new HostNetworkException("Your router has a private Internet address. Another router or your Internet provider prevents direct Internet sharing.");
        var lease = new NatPmpLease(route, port, address, port, 1800);
        return await lease.RenewAsync(ct).ConfigureAwait(false) ? lease : null;
    }
    public async Task<bool> RenewAsync(CancellationToken ct)
    {
        if (established)
        {
            var address = await Send([0, 0], 128, ct).ConfigureAwait(false);
            if (address is not { Length: >= 12 } || !new IPAddress(address.AsSpan(8, 4)).Equals(ExternalAddress)) return false;
        }
        var result = await Map(1800, ct).ConfigureAwait(false);
        if (result is not { Length: >= 16 } || BinaryPrimitives.ReadUInt16BigEndian(result.AsSpan(8)) != localPort) return false;
        var assigned = BinaryPrimitives.ReadUInt16BigEndian(result.AsSpan(10));
        var seconds = BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(12));
        if (assigned == 0 || seconds == 0) return false;
        // A changed external port invalidates copied links; do not advertise the old endpoint.
        var changed = established && ExternalPort != assigned;
        ExternalPort = assigned; Lifetime = TimeSpan.FromSeconds(seconds); established = true;
        return !changed;
    }
    private Task<byte[]?> Map(uint seconds, CancellationToken ct)
    {
        var request = new byte[12]; request[1] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)localPort);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), (ushort)ExternalPort);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(8), seconds);
        return Send(request, 130, ct);
    }
    private Task<byte[]?> Send(byte[] request, byte opcode, CancellationToken ct) => (exchange ?? Exchange)(route, request, opcode, ct);
    private static async Task<byte[]?> Exchange(HostNetworkRoute route, byte[] request, byte opcode, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(route.LocalAddress, 0)); udp.Connect(route.Gateway, 5351);
        foreach (var wait in new[] { 250, 500, 1000 })
        {
            ct.ThrowIfCancellationRequested(); await udp.SendAsync(request, ct).ConfigureAwait(false);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct); attempt.CancelAfter(wait);
            try
            {
                var packet = (await udp.ReceiveAsync(attempt.Token).ConfigureAwait(false)).Buffer;
                return packet.Length >= 8 && packet[0] == 0 && packet[1] == opcode && BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)) == 0 ? packet : null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (SocketException) { return null; }
        }
        return null;
    }
    public async ValueTask DisposeAsync()
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); try { await Map(0, timeout.Token).ConfigureAwait(false); } catch { } }
}

internal sealed class UpnpLease(HostNetworkRoute route, int port, string owner, Uri control, string service, IPAddress external,
    Func<string, KeyValuePair<string, string>[], CancellationToken, Task<XDocument>>? soap = null) : IHostPortLease
{
    public IPAddress ExternalAddress { get; } = external;
    public int ExternalPort => port;
    public TimeSpan Lifetime { get; private set; } = TimeSpan.FromMinutes(30);
    private bool owned;
    public static async Task<IHostPortLease?> TryCreateAsync(HostNetworkRoute route, int port, string owner, CancellationToken ct)
    {
        var locations = await Discover(route, ct).ConfigureAwait(false);
        using var http = WindowsHostNetworkPlatform.NewHttpClient();
        foreach (var location in locations.Take(4))
        {
            var document = await ReadXml(http, new HttpRequestMessage(HttpMethod.Get, location), ct).ConfigureAwait(false);
            foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "service"))
            {
                var service = element.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value;
                if (service is not ("urn:schemas-upnp-org:service:WANIPConnection:1" or "urn:schemas-upnp-org:service:WANIPConnection:2" or "urn:schemas-upnp-org:service:WANPPPConnection:1")) continue;
                var relative = element.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value;
                if (!Uri.TryCreate(location, relative, out var control) || !GatewayUri(control, route.Gateway)) continue;
                var candidate = new UpnpLease(route, port, owner, control, service, IPAddress.None);
                var response = await candidate.Soap("GetExternalIPAddress", [], ct).ConfigureAwait(false);
                var raw = response.Descendants().FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value;
                if (!IPAddress.TryParse(raw, out var external)) continue;
                if (!HostNetworkSetup.IsPublicIPv4(external)) throw new HostNetworkException("Your router has a private Internet address. Another router or your Internet provider prevents direct Internet sharing.");
                candidate = new(route, port, owner, control, service, external);
                try { if (await candidate.RenewAsync(ct).ConfigureAwait(false)) return candidate; }
                catch { await candidate.DisposeAsync().ConfigureAwait(false); throw; }
            }
        }
        return null;
    }
    public async Task<bool> RenewAsync(CancellationToken ct)
    {
        if (owned)
        {
            var current = await Soap("GetExternalIPAddress", [], ct).ConfigureAwait(false);
            if (!IPAddress.TryParse(Value(current, "NewExternalIPAddress"), out var currentAddress) || !currentAddress.Equals(ExternalAddress)) return false;
        }
        // Query before adding: UPnP AddPortMapping can overwrite a mapping. Treat ambiguous failures
        // as refusal. A permanent-only router may be used only for this exact owned mapping.
        var prior = await Soap("GetSpecificPortMappingEntry", Identity(), ct).ConfigureAwait(false);
        var error = Value(prior, "errorCode");
        if (error != "714" && !Matches(prior)) return false;
        if (owned && error == "714") owned = false;
        var values = Identity().Concat(new[] { Pair("NewInternalPort", port.ToString()), Pair("NewInternalClient", route.LocalAddress.ToString()),
            Pair("NewEnabled", "1"), Pair("NewPortMappingDescription", owner), Pair("NewLeaseDuration", "1800") }).ToArray();
        var response = await Soap("AddPortMapping", values, ct).ConfigureAwait(false);
        if (Value(response, "errorCode") == "725")
        {
            // OnlyPermanentLeasesSupported. No listener survives launcher exit; normal shutdown
            // removes the mapping, while the same stable owner label allows recovery after a crash.
            values[^1] = Pair("NewLeaseDuration", "0");
            response = await Soap("AddPortMapping", values, ct).ConfigureAwait(false);
        }
        if (Value(response, "errorCode") != null) return false;
        owned = true;
        var verification = await Soap("GetSpecificPortMappingEntry", Identity(), ct).ConfigureAwait(false);
        owned = Matches(verification);
        if (!uint.TryParse(Value(verification, "NewLeaseDuration"), out var duration))
        { if (owned) await DisposeAsync().ConfigureAwait(false); return false; }
        // Periodically revalidate permanent mappings as well, including WAN address changes.
        Lifetime = duration == 0 ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(duration);
        return owned;
    }
    private bool Matches(XDocument doc) => Value(doc, "NewInternalClient") == route.LocalAddress.ToString()
        && Value(doc, "NewInternalPort") == port.ToString() && Value(doc, "NewPortMappingDescription") == owner;
    private KeyValuePair<string, string>[] Identity() => [Pair("NewRemoteHost", ""), Pair("NewExternalPort", port.ToString()), Pair("NewProtocol", "TCP")];
    private static KeyValuePair<string, string> Pair(string name, string value) => new(name, value);
    private static string? Value(XDocument doc, string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
    private async Task<XDocument> Soap(string action, KeyValuePair<string, string>[] values, CancellationToken ct)
    {
        if (soap != null) return await soap(action, values, ct).ConfigureAwait(false);
        XNamespace envelope = "http://schemas.xmlsoap.org/soap/envelope/";
        var body = new XElement(XName.Get(action, service), values.Select(p => new XElement(p.Key, p.Value)));
        var document = new XDocument(new XElement(envelope + "Envelope", new XAttribute(XNamespace.Xmlns + "s", envelope),
            new XAttribute(envelope + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"), new XElement(envelope + "Body", body)));
        using var request = new HttpRequestMessage(HttpMethod.Post, control) { Content = new StringContent(document.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml") };
        request.Headers.TryAddWithoutValidation("SOAPAction", '"' + service + "#" + action + '"');
        using var http = WindowsHostNetworkPlatform.NewHttpClient(); return await ReadXml(http, request, ct).ConfigureAwait(false);
    }
    private static async Task<XDocument> ReadXml(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(4)); ct = timeout.Token;
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > 128 * 1024) throw new IOException("Router response was too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var data = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        { if (data.Length + count > 128 * 1024) throw new IOException("Router response was too large."); data.Write(buffer, 0, count); }
        data.Position = 0;
        using var reader = XmlReader.Create(data, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 128 * 1024 });
        return XDocument.Load(reader);
    }
    private static async Task<IReadOnlyList<Uri>> Discover(HostNetworkRoute route, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(route.LocalAddress, 0));
        var request = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n");
        await udp.SendAsync(request, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900), ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var found = new List<Uri>();
        try
        {
            while (found.Count < 4)
            {
                var reply = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (!reply.RemoteEndPoint.Address.Equals(route.Gateway) || reply.Buffer.Length > 16384) continue;
                foreach (var line in Encoding.ASCII.GetString(reply.Buffer).Split('\n'))
                    if (line.StartsWith("location:", StringComparison.OrdinalIgnoreCase)
                        && Uri.TryCreate(line[9..].Trim(), UriKind.Absolute, out var location) && GatewayUri(location, route.Gateway)
                        && !found.Contains(location)) found.Add(location);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return found;
    }
    private static bool GatewayUri(Uri uri, IPAddress gateway) => uri.Scheme == "http" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && IPAddress.TryParse(uri.Host, out var address) && address.Equals(gateway);
    public async ValueTask DisposeAsync()
    {
        if (!owned) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var current = await Soap("GetSpecificPortMappingEntry", Identity(), timeout.Token).ConfigureAwait(false);
            if (Matches(current)) await Soap("DeletePortMapping", Identity(), timeout.Token).ConfigureAwait(false);
        }
        catch { }
        owned = false;
    }
}
