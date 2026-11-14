using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Hearth.App;
using Hearth.Core;

internal static class OwnedHostChecks
{
    public static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Hearth.sln"))) directory = directory.Parent;
            if (directory == null) throw new FileNotFoundException("Cannot find the Hearth checkout for the integration host.");
            return directory.FullName;
        }
    }

    public static string HostExecutable
    {
        get
        {
            var executable = Path.Combine(RepositoryRoot, "src", "Hearth.Host", "bin", "Release", "net10.0", "Hearth.Host.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("Build Hearth.Host in Release before running lifecycle checks.", executable);
            return executable;
        }
    }

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static string NewOwnerToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var port = FreePort();
        var token = NewOwnerToken();
        using var host = new AppLocalHost(Path.Combine(root, "owner"), HostExecutable, port);
        await host.StartAsync(token);
        using var api = new HearthApi(host.HostUrl, token);
        var identity = await api.HealthAsync();
        var pack = await api.CreatePackAsync(new("Persistent lifecycle pack", "1.21.1", "fabric", "0.16.10"));
        var firstProcess = host.ProcessId;
        check(host.IsRunning && firstProcess.HasValue && (await api.ListPacksAsync()).Single().Id == pack.Id,
            "App-owned host serves real authenticated pack requests");
        check(!Directory.EnumerateFiles(Path.Combine(root, "owner"), "host-owner-*.tmp").Any(),
            "Successful startup removes its temporary plaintext owner-key handoff");
        await host.StartAsync(token);
        check(host.ProcessId == firstProcess, "Starting an already-running owned host is idempotent");

        // Deliberately reuse the owner key: probing /owner/packs is not proof that
        // this new controller owns the process already listening on the port.
        using (var contender = new AppLocalHost(Path.Combine(root, "contender"), HostExecutable, port))
        {
            await ExpectFailure(() => contender.StartAsync(token));
            check(!contender.IsRunning && contender.ProcessId == null,
                "A different profile cannot attach to an occupied host port, even with the same owner key");
            check((await api.HealthAsync()).HostId == identity.HostId && host.ProcessId == firstProcess,
                "A failed competing start leaves the existing host and its data available");
        }

        await host.StopAsync();
        await AssertStoppedAsync(host.HostUrl, firstProcess);
        await host.StopAsync();
        check(!host.IsRunning && host.ProcessId == null, "Stopping hosting closes its process and listener and is safe to repeat");

        using (var wrongOwner = new AppLocalHost(Path.Combine(root, "owner"), HostExecutable, port))
        {
            await ExpectFailure(() => wrongOwner.StartAsync(NewOwnerToken()));
            await AssertStoppedAsync(host.HostUrl, wrongOwner.ProcessId);
            check(!wrongOwner.IsRunning, "Invalid saved owner credentials cannot leave a host running");
        }

        await host.StartAsync(token);
        check((await api.HealthAsync()).HostId == identity.HostId
            && (await api.ListPacksAsync()).Single().Id == pack.Id
            && (await api.ListPacksAsync()).Single().InviteCode == pack.InviteCode,
            "Restarting after a failed attempt preserves host identity, owner access, pack, and invite");
        var restartedProcess = host.ProcessId;
        await host.StopAsync();
        await AssertStoppedAsync(host.HostUrl, restartedProcess);

        using (var canceled = new AppLocalHost(Path.Combine(root, "canceled"), HostExecutable, FreePort()))
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await ExpectFailure(() => canceled.StartAsync(NewOwnerToken(), cancellation.Token));
            check(!canceled.IsRunning && canceled.ProcessId == null, "Canceled host startup never leaves a child running");
        }

        using (var interrupted = new AppLocalHost(Path.Combine(root, "interrupted-start"), HostExecutable, FreePort()))
        {
            var starting = interrupted.StartAsync(NewOwnerToken());
            var initializingProcess = interrupted.ProcessId;
            interrupted.Dispose();
            try { await starting; }
            catch (Exception) { /* Exit is allowed to interrupt any startup stage. */ }
            await AssertStoppedAsync(interrupted.HostUrl, initializingProcess);
            check(!interrupted.IsRunning && interrupted.ProcessId == null,
                "Disposing the launcher during host startup cannot leave an initializing child behind");
        }

        using (var missing = new AppLocalHost(Path.Combine(root, "missing"), Path.Combine(root, "does-not-exist.exe"), FreePort()))
        {
            await ExpectFailure(() => missing.StartAsync(NewOwnerToken()));
            check(!missing.IsRunning && missing.ProcessId == null, "A missing host executable leaves hosting stopped");
        }

        await CheckStartupHandshakeAsync(Path.Combine(root, "no-handshake"), check);
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> predicate, string failure, int seconds = 20)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (await predicate()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException(failure);
    }

    public static async Task AssertStoppedAsync(string url, int? processId)
    {
        await WaitUntilAsync(async () => !ProcessAlive(processId) && !await PortOpenAsync(new Uri(url).Port),
            "The host process or listening port outlived its launcher.", seconds: 10);
    }

    public static async Task<bool> PortOpenAsync(int port)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        try { await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token); return true; }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) { return false; }
    }

    public static bool ProcessAlive(int? processId)
    {
        if (processId == null) return false;
        try { using var process = Process.GetProcessById(processId.Value); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task ExpectFailure(Func<Task> action)
    {
        try { await action(); }
        catch (Exception) { return; }
        throw new InvalidOperationException("The invalid host startup unexpectedly succeeded.");
    }

    private static async Task CheckStartupHandshakeAsync(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        var credentialPath = Path.Combine(root, "fixture-owner.txt");
        await File.WriteAllTextAsync(credentialPath, NewOwnerToken());
        var data = Path.Combine(root, "Host");
        var start = new ProcessStartInfo(HostExecutable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "--launcher-managed", "--init", "--data", data, "--owner-token-file", credentialPath }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new IOException("Could not start the handshake fixture.");
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            await Task.Delay(300);
            check(!child.HasExited && !Directory.Exists(data), "A child cannot initialize host data before the launcher's ownership handshake");
            child.StandardInput.Close(); // Launcher disappears before assigning its job.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await child.WaitForExitAsync(timeout.Token);
            check(child.ExitCode == 0 && !Directory.Exists(data), "A host abandoned before the ownership handshake exits without changing host data");
            await Task.WhenAll(output, errors);
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }
    }
}
