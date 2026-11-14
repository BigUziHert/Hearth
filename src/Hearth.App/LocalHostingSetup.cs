using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using Hearth.Core;

namespace Hearth.App;

public static class LocalHostingSetup
{
    public const string HostUrl = "http://127.0.0.1:17842";

    // A null result means Windows elevation was canceled before setup started.
    public static async Task<HostConnection?> EnableAsync(ProfileStore store, Func<string, Task> install,
        Func<HostConnection, CancellationToken, Task> verify, CancellationToken cancellationToken = default)
    {
        var local = store.State.Hosts.FirstOrDefault(host => host.Url == HostUrl);
        var created = local == null;
        if (local == null)
        {
            local = new(Guid.NewGuid().ToString("N"), "This PC", HostUrl,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
            store.State.Hosts.Add(local);
            // Preserve the owner key before setup can initialize host data. Other setup
            // failures may happen after initialization, so they must retain this key.
            try { store.Save(); }
            catch { store.State.Hosts.Remove(local); throw; }
        }

        var temporaryCredential = Path.Combine(store.Root, "host-owner-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryCredential, local.OwnerToken, cancellationToken);
            try { await install(temporaryCredential); }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED from ShellExecute/runas means the script never ran.
                // Only undo this attempt's new connection; existing hosts stay intact.
                if (created)
                {
                    store.State.Hosts.Remove(local);
                    try { store.Save(); }
                    catch { store.State.Hosts.Add(local); throw; }
                }
                return null;
            }
            await verify(local, cancellationToken);
            return local;
        }
        finally { if (File.Exists(temporaryCredential)) File.Delete(temporaryCredential); }
    }
}
