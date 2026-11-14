using System.IO;
using System.Security.Cryptography;
using Hearth.Core;

namespace Hearth.App;

public static class LocalHostingSetup
{
    public static async Task<HostConnection> EnableAsync(ProfileStore store, string hostUrl, string dataDirectory,
        Func<string, CancellationToken, Task> start, CancellationToken cancellationToken = default, Func<string?>? verifiedHostId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = store.State;
        var local = state.Hosts.FirstOrDefault(host => host.Id == state.LocalHostConnectionId)
            ?? state.Hosts.FirstOrDefault(host => host.Url == hostUrl);
        if (string.IsNullOrEmpty(state.LocalHostOwnerToken))
        {
            // Keep the key separately: a failed first start must be recoverable
            // without displaying a host that never became ready.
            if (local == null && File.Exists(Path.Combine(dataDirectory, "host.json")))
                throw new InvalidOperationException("Existing host data needs its original owner key. Paste that key under Host on this PC, then enable hosting.");
            state.LocalHostOwnerToken = local?.OwnerToken
                ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            try { store.Save(); }
            catch { state.LocalHostOwnerToken = ""; throw; }
        }

        await start(state.LocalHostOwnerToken, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var ready = local == null
            ? new HostConnection(Guid.NewGuid().ToString("N"), "This PC", hostUrl, state.LocalHostOwnerToken, HostId: verifiedHostId?.Invoke())
            : local with { Url = hostUrl, OwnerToken = state.LocalHostOwnerToken, HostId = verifiedHostId?.Invoke() ?? local.HostId };
        var oldEnabled = state.LocalHostingEnabled;
        var oldId = state.LocalHostConnectionId;
        var oldMigration = state.LegacyHostingMigrated;
        var index = local == null ? -1 : state.Hosts.IndexOf(local);
        if (index < 0) state.Hosts.Add(ready); else state.Hosts[index] = ready;
        state.LocalHostConnectionId = ready.Id;
        state.LocalHostingEnabled = true;
        state.LegacyHostingMigrated = true;
        try { store.Save(); }
        catch
        {
            if (index < 0) state.Hosts.Remove(ready); else state.Hosts[index] = local!;
            state.LocalHostingEnabled = oldEnabled;
            state.LocalHostConnectionId = oldId;
            state.LegacyHostingMigrated = oldMigration;
            throw;
        }
        return ready;
    }
}
