using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hearth.Core;

namespace Hearth.App;

/// <summary>Owns hosting for one launcher profile; never attaches to an existing process.</summary>
public sealed class AppLocalHost : IDisposable
{
    private readonly string profileRoot;
    private readonly string executablePath;
    private readonly int port;
    private readonly int? httpsPort;
    private readonly object stateLock = new();
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private OwnedHostProcess? child;
    private bool ready;
    private bool disposed;
    private string? lastError;
    private bool httpsActive;
    private string advertisedAddress;
    public event EventHandler? StateChanged;

    public AppLocalHost(string profileRoot, string? executablePath = null, int port = 17842, string? dataDirectory = null,
        int? httpsPort = null, IHostNetworkPlatform? networkPlatform = null, string? advertisedAddress = null)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        this.profileRoot = Path.GetFullPath(profileRoot);
        this.executablePath = Path.GetFullPath(executablePath ?? Path.Combine(AppContext.BaseDirectory, "host", "Hearth.Host.exe"));
        this.port = port;
        if (httpsPort is < 1024 or > 65535 || httpsPort == port) throw new ArgumentOutOfRangeException(nameof(httpsPort));
        this.httpsPort = httpsPort;
        this.advertisedAddress = string.IsNullOrWhiteSpace(advertisedAddress) ? "" : NormalizeAdvertisedAddress(advertisedAddress);
        DataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(dataDirectory) ? Path.Combine(this.profileRoot, "Host") : dataDirectory);
        HostUrl = $"http://127.0.0.1:{port}";
        if (httpsPort.HasValue)
        {
            Network = new HostNetworkSetup(this.profileRoot, this.executablePath, httpsPort.Value, networkPlatform);
            Network.ResultChanged += (_, _) => PublishEndpoints();
        }
    }

    public string DataDirectory { get; }
    public string HostUrl { get; }
    public string? HostId { get; private set; }
    public string? CertificatePin { get; private set; }
    public string? AdvertisedAddress => string.IsNullOrEmpty(advertisedAddress) ? null : advertisedAddress;
    public bool IsSharing => IsRunning && httpsActive && Network?.Result?.FirewallReady == true;
    public string? InternetAddress => IsSharing
        ? AdvertisedAddress ?? Network?.Result?.PublicAddress : null;
    public HostNetworkSetup? Network { get; }
    public bool IsRunning { get { lock (stateLock) return ready && child?.IsRunning == true; } }
    public int? ProcessId { get { lock (stateLock) return child?.IsRunning == true ? child.Id : null; } }
    public string? LastError { get { lock (stateLock) return lastError ?? (ready && child?.IsRunning != true ? "Hosting stopped. Start hosting again." : null); } }

    public Task StartAsync(string ownerToken, CancellationToken cancellationToken = default) => StartAsync(ownerToken, true, cancellationToken);

    public async Task StartAsync(string ownerToken, bool interactive, CancellationToken cancellationToken = default)
    {
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (stateLock)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (ready && child?.IsRunning == true) return;
                lastError = null;
            }
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            stopping.CancelAfter(TimeSpan.FromSeconds(25));
            var ct = stopping.Token;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(ownerToken) || ownerToken.Trim().Length is < 32 or > 512)
                    throw new ArgumentException("The local host needs its saved owner key before it can start.", nameof(ownerToken));
                if (!File.Exists(executablePath))
                    throw new FileNotFoundException("The hosting component is missing. Reinstall Hearth to enable hosting.", executablePath);

                DetachChild()?.Dispose();
                EnsurePortAvailable();
                Directory.CreateDirectory(profileRoot);
                var settingsPath = Path.Combine(DataDirectory, "host.json");
                if (!File.Exists(settingsPath)) await InitializeAsync(ownerToken, ct).ConfigureAwait(false);
                var identity = JsonSerializer.Deserialize<HostIdentity>(await File.ReadAllTextAsync(settingsPath, ct).ConfigureAwait(false), JsonDefaults.Options);
                if (string.IsNullOrWhiteSpace(identity?.HostId))
                    throw new IOException("The local host's saved identity could not be read. Its files have been preserved.");
                HostId = identity.HostId;
                var arguments = new List<string> { "--data", DataDirectory, "--port", port.ToString(), "--bind", "127.0.0.1" };
                if (Network != null && httpsPort.HasValue)
                {
                    using var certificate = HostCertificate.LoadOrCreate(DataDirectory);
                    CertificatePin = HostCertificate.GetPin(certificate);
                    // A canceled Windows permission prompt happens before any saved host
                    // entry is added. Startup never opens that prompt without a click.
                    stopping.CancelAfter(Timeout.InfiniteTimeSpan);
                    var network = await Network.PrepareAsync(HostId, interactive, ct).ConfigureAwait(false);
                    if (network.FirewallReady) arguments.AddRange(["--https-port", httpsPort.Value.ToString()]);
                }
                stopping.CancelAfter(TimeSpan.FromSeconds(25));
                var running = StartChild(arguments);
                // This signal comes from this exact child after Kestrel has bound its
                // socket. A different process on the same port cannot impersonate it.
                await running.WaitUntilListeningAsync(ct).ConfigureAwait(false);
                using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
                using var api = new HearthApi(HostUrl, ownerToken, client);
                var health = await api.HealthAsync(ct).ConfigureAwait(false);
                if (health.HostId != identity.HostId)
                    throw new IOException("A different host answered on this port. Hearth left it alone and stopped its own host.");
                await api.ListPacksAsync(ct).ConfigureAwait(false);
                if (arguments.Contains("--https-port"))
                {
                    using var secure = new HearthApi($"https://127.0.0.1:{httpsPort}", ownerToken, certificatePin: CertificatePin);
                    if ((await secure.HealthAsync(ct).ConfigureAwait(false)).HostId != HostId)
                        throw new IOException("The secure host returned an unexpected identity.");
                }
                ct.ThrowIfCancellationRequested();
                lock (stateLock)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    if (child != running || !running.IsRunning) throw new IOException("The local host stopped during startup. Try enabling hosting again.");
                    ready = true;
                    httpsActive = arguments.Contains("--https-port");
                }
                PublishEndpoints();
            }
            catch (Exception exception)
            {
                var failed = DetachChild();
                if (failed is not null) await failed.StopAsync().ConfigureAwait(false);
                if (Network != null) await Network.StopAsync().ConfigureAwait(false);
                var error = exception is OperationCanceledException && stopping.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested
                    ? new IOException("The local host took too long to start. Its files are safe; try enabling hosting again.", exception)
                    : exception;
                lock (stateLock) lastError = error.Message;
                if (!ReferenceEquals(error, exception)) throw error;
                throw;
            }
        }
        finally { operation.Release(); }
    }

    public static string NormalizeAdvertisedAddress(string value)
    {
        var input = value.Trim();
        if (!input.Contains("://", StringComparison.Ordinal)) input = "https://" + input;
        var normalized = HearthApi.NormalizeHost(input);
        if (!normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use an HTTPS address, including its port, such as https://packs.example.com:17843.");
        return normalized;
    }
    public void SetAdvertisedAddress(string? address)
    {
        advertisedAddress = string.IsNullOrWhiteSpace(address) ? "" : NormalizeAdvertisedAddress(address);
        PublishEndpoints();
    }
    private void PublishEndpoints()
    {
        if (string.IsNullOrEmpty(HostId) || !Directory.Exists(DataDirectory)) return;
        try
        {
            var active = IsRunning && httpsActive && Network?.Result?.FirewallReady == true;
            var value = new HostEndpoints(HostId, CertificatePin, active ? InternetAddress : null,
                active ? Network!.Result!.LocalAddresses.ToList() : [], DateTimeOffset.UtcNow);
            var path = Path.Combine(DataDirectory, "advertised-endpoints.json");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(output, value, JsonDefaults.Options); output.Flush(true);
                }
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { lock (stateLock) lastError = "Hosting is running, but its shared addresses could not be saved. Check the host data folder's permissions."; }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RetryNetworkAsync(string ownerToken, CancellationToken ct = default)
    {
        if (!IsRunning) throw new InvalidOperationException("Hosting is off on this PC. Choose Enable hosting first.");
        if (Network == null) return;
        // Windows permission is the only interactive part. Cancellation here cannot stop the host.
        if (!await Network.EnsureFirewallAsync(true, ct).ConfigureAwait(false))
            throw new IOException("Windows did not allow incoming Hearth connections. Hosting is still running; retry connection setup when ready.");
        ct.ThrowIfCancellationRequested();
        if (httpsActive)
        {
            await Network.PrepareAsync(HostId!, false, ct).ConfigureAwait(false);
            PublishEndpoints(); return;
        }
        await StopAsync().ConfigureAwait(false);
        try { await StartAsync(ownerToken, false, ct).ConfigureAwait(false); }
        catch
        {
            // A running loopback-only host must remain usable if the secure restart is interrupted.
            if (!disposed && !lifetime.IsCancellationRequested)
                try { await StartAsync(ownerToken, false, CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private async Task InitializeAsync(string ownerToken, CancellationToken cancellationToken)
    {
        // The caller persists the DPAPI-protected owner credential before StartAsync.
        // Its short-lived plaintext handoff stays inside that user's profile folder.
        var tokenFile = Path.Combine(profileRoot, "host-owner-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // Windows deletes this handoff even if Hearth crashes before finally
            // runs. The host opens it with delete sharing while this handle lives.
            await using var tokenStream = new FileStream(tokenFile, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            await tokenStream.WriteAsync(Encoding.UTF8.GetBytes(ownerToken.Trim()), cancellationToken).ConfigureAwait(false);
            await tokenStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            var initializer = StartChild(["--init", "--data", DataDirectory, "--owner-token-file", tokenFile, "--port", port.ToString(), "--bind", "127.0.0.1"]);
            if (await initializer.WaitForExitAsync(cancellationToken).ConfigureAwait(false) != 0)
                throw new IOException("The local host could not prepare its data folder. Check that Hearth can write to that folder and try again.");
            DetachChild()?.Dispose();
        }
        finally
        {
            try { File.Delete(tokenFile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private OwnedHostProcess StartChild(IEnumerable<string> arguments)
    {
        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            child = OwnedHostProcess.Start(executablePath, arguments);
            ready = false;
            httpsActive = false;
            return child;
        }
    }

    private OwnedHostProcess? DetachChild()
    {
        lock (stateLock)
        {
            var previous = child;
            child = null;
            ready = false;
            return previous;
        }
    }

    private void EnsurePortAvailable()
    {
        var probe = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        try { probe.Start(); }
        catch (SocketException exception)
        {
            throw new IOException($"Port {port} is already in use. Close the other Hearth profile or program using it, then retry. Hearth did not connect to or stop that process.", exception);
        }
        finally { probe.Stop(); }
    }

    public async Task StopAsync()
    {
        await operation.WaitAsync().ConfigureAwait(false);
        try
        {
            var previous = DetachChild();
            if (previous is not null) await previous.StopAsync().ConfigureAwait(false);
            if (Network != null) await Network.StopAsync().ConfigureAwait(false);
            PublishEndpoints();
        }
        finally { operation.Release(); }
    }

    public void Dispose()
    {
        OwnedHostProcess? previous;
        lock (stateLock)
        {
            if (disposed) return;
            disposed = true;
            ready = false;
            previous = child;
            child = null;
        }
        lifetime.Cancel();
        previous?.Dispose();
        Network?.Dispose();
        PublishEndpoints();
    }

    private sealed record HostIdentity(string HostId);
}
