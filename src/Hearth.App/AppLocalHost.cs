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
    private readonly object stateLock = new();
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private OwnedHostProcess? child;
    private bool ready;
    private bool disposed;
    private string? lastError;

    public AppLocalHost(string profileRoot, string? executablePath = null, int port = 17842, string? dataDirectory = null)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        this.profileRoot = Path.GetFullPath(profileRoot);
        this.executablePath = Path.GetFullPath(executablePath ?? Path.Combine(AppContext.BaseDirectory, "host", "Hearth.Host.exe"));
        this.port = port;
        DataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(dataDirectory) ? Path.Combine(this.profileRoot, "Host") : dataDirectory);
        HostUrl = $"http://127.0.0.1:{port}";
    }

    public string DataDirectory { get; }
    public string HostUrl { get; }
    public bool IsRunning { get { lock (stateLock) return ready && child?.IsRunning == true; } }
    public int? ProcessId { get { lock (stateLock) return child?.IsRunning == true ? child.Id : null; } }
    public string? LastError { get { lock (stateLock) return lastError ?? (ready && child?.IsRunning != true ? "Hosting stopped. Start hosting again." : null); } }

    public async Task StartAsync(string ownerToken, CancellationToken cancellationToken = default)
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

                var running = StartChild(["--data", DataDirectory, "--port", port.ToString()]);
                // This signal comes from this exact child after Kestrel has bound its
                // socket. A different process on the same port cannot impersonate it.
                await running.WaitUntilListeningAsync(ct).ConfigureAwait(false);
                using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
                using var api = new HearthApi(HostUrl, ownerToken, client);
                var health = await api.HealthAsync(ct).ConfigureAwait(false);
                if (health.HostId != identity.HostId)
                    throw new IOException("A different host answered on this port. Hearth left it alone and stopped its own host.");
                await api.ListPacksAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                lock (stateLock)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    if (child != running || !running.IsRunning) throw new IOException("The local host stopped during startup. Try enabling hosting again.");
                    ready = true;
                }
            }
            catch (Exception exception)
            {
                var failed = DetachChild();
                if (failed is not null) await failed.StopAsync().ConfigureAwait(false);
                var error = exception is OperationCanceledException && !cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested
                    ? new IOException("The local host took too long to start. Its files are safe; try enabling hosting again.", exception)
                    : exception;
                lock (stateLock) lastError = error.Message;
                if (!ReferenceEquals(error, exception)) throw error;
                throw;
            }
        }
        finally { operation.Release(); }
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
    }

    private sealed record HostIdentity(string HostId);
}
