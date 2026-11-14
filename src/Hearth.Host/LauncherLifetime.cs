namespace Hearth.Host;

/// <summary>Keeps an app-launched host inside the launcher's lifetime.</summary>
internal sealed class LauncherLifetime : IDisposable
{
    public const string ReadyMessage = "HEARTH_HOST_READY";
    private readonly CancellationTokenSource stopping = new();
    public bool IsManaged { get; private init; }
    public bool MayStart { get; private init; }
    public CancellationToken Stopping => stopping.Token;

    public static async Task<LauncherLifetime> ConnectAsync(string[] arguments)
    {
        if (!arguments.Contains("--launcher-managed", StringComparer.Ordinal))
            return new() { MayStart = true };

        // The launcher sends this only after assigning us to its kill-on-close job.
        // EOF before the handshake means the launcher died before assignment.
        var lifetime = new LauncherLifetime
        {
            IsManaged = true,
            MayStart = await Console.In.ReadLineAsync() == "start"
        };
        // Console.In may implement ReadLineAsync with a synchronous read. Keep its
        // lifetime monitor off the startup thread after the initial handshake.
        if (lifetime.MayStart) _ = Task.Run(lifetime.WatchInputAsync);
        return lifetime;
    }

    private async Task WatchInputAsync()
    {
        try
        {
            while (await Console.In.ReadLineAsync() is { } line)
                if (line == "stop") break;
        }
        catch (IOException) { }
        finally
        {
            try { stopping.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    public void Dispose() => stopping.Dispose();
}
