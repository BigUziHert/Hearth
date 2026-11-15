using System.IO;
using System.Security.Cryptography;
using Hearth.Core;

namespace Hearth.App;

public static class LocalHostingSetup
{
    public static async Task<HostConnection> EnableAsync(ProfileStore store, string hostUrl, string dataDirectory,
        Func<string, CancellationToken, Task> start, CancellationToken cancellationToken = default, Func<string?>? verifiedHostId = null,
        string? recoveryKey = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = store.State;
        var local = state.Hosts.FirstOrDefault(host => host.Id == state.LocalHostConnectionId)
            ?? state.Hosts.FirstOrDefault(host => host.Url == hostUrl);
        var previousToken = state.LocalHostOwnerToken;
        var candidate = string.IsNullOrWhiteSpace(recoveryKey) ? previousToken : recoveryKey.Trim();
        if (candidate.Length > 0 && candidate.Length is < 32 or > 512) throw new InvalidOperationException("Paste the original owner key before enabling hosting.");
        if (string.IsNullOrEmpty(candidate))
        {
            // Keep the key separately: a failed first start must be recoverable
            // without displaying a host that never became ready.
            if (local == null && File.Exists(Path.Combine(dataDirectory, "host.json")))
                throw new InvalidOperationException("Existing host data needs its original owner key. Open Recover or replace owner key on the This PC card, then enable hosting.");
            candidate = local?.OwnerToken
                ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            // A brand-new host needs a recoverable key even if initialization is interrupted.
            // An existing host's recovery credential is never persisted before verification.
            if (!File.Exists(Path.Combine(dataDirectory, "host.json")))
            {
                state.LocalHostOwnerToken = candidate;
                try { store.Save(); }
                catch { state.LocalHostOwnerToken = previousToken; throw; }
            }
        }

        try { await start(candidate, cancellationToken); }
        catch (HostApiException error) when (error.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
        { throw new InvalidOperationException("That owner key doesn't match this host's data. Paste the original owner key and try again.", error); }
        cancellationToken.ThrowIfCancellationRequested();
        var tokenBeforeCommit = state.LocalHostOwnerToken;
        state.LocalHostOwnerToken = candidate;
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
            state.LocalHostOwnerToken = tokenBeforeCommit;
            throw;
        }
        return ready;
    }
}
